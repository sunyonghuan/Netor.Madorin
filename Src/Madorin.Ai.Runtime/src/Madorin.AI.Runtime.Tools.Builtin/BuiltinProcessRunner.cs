using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Starts a pinned executable without shell expansion and enforces bounded process I/O.</summary>
internal static class BuiltinProcessRunner
{
    private const int MaxArgumentCount = 128;
    private const int MaxArgumentLength = 8192;
    private const int MaxEnvironmentVariables = 128;
    private const int MaxInputBytes = 1024 * 1024;
    private const int MaxOutputBytesPerStream = 1024 * 1024;
    private const int MaxTotalOutputBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public static PinnedExecutable ResolveAndPinExecutable(
        string requested,
        IReadOnlyList<string> allowedExecutables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        ArgumentNullException.ThrowIfNull(allowedExecutables);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (allowedExecutables.Count == 0)
        {
            throw new UnauthorizedAccessException("No executable is allowed by the effective Grant.");
        }

        string executablePath;
        if (Path.IsPathFullyQualified(requested))
        {
            executablePath = Path.GetFullPath(requested);
            if (!allowedExecutables
                .Where(Path.IsPathFullyQualified)
                .Select(Path.GetFullPath)
                .Contains(executablePath, comparer))
            {
                throw new UnauthorizedAccessException(
                    "The requested executable path is not in the effective allowlist.");
            }
        }
        else
        {
            if (requested.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                || requested.Contains(':', StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "Relative executable paths are not allowed.");
            }

            if (!allowedExecutables
                .Where(static item => !Path.IsPathFullyQualified(item))
                .Contains(requested, comparer))
            {
                throw new UnauthorizedAccessException(
                    "The requested executable name is not in the effective allowlist.");
            }

            executablePath = FindOnRuntimePath(requested);
        }

        return PinExecutable(executablePath);
    }

    public static PinnedExecutable ResolveAndPinFirstAvailable(
        IReadOnlyList<string> executableNames)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        foreach (var executableName in executableNames)
        {
            try
            {
                return PinExecutable(FindOnRuntimePath(executableName));
            }
            catch (FileNotFoundException)
            {
            }
        }

        throw new FileNotFoundException(
            $"None of the required executables were found: {string.Join(", ", executableNames)}.");
    }

    public static string[] ParseArguments(System.Text.Json.JsonElement arguments)
    {
        if (!arguments.TryGetProperty("arguments", out var property))
        {
            return [];
        }

        if (property.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new ArgumentException("Tool argument 'arguments' must be an array.");
        }

        var values = property.EnumerateArray()
            .Select(static item => item.ValueKind == System.Text.Json.JsonValueKind.String
                ? item.GetString()
                    ?? throw new ArgumentException("Process arguments cannot be null.")
                : throw new ArgumentException("Every process argument must be a string."))
            .ToArray();
        if (values.Length > MaxArgumentCount || values.Any(static value => value.Length > MaxArgumentLength))
        {
            throw new ArgumentException("The process argument limit was exceeded.");
        }

        return values;
    }

    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        System.Text.Json.JsonElement arguments,
        IReadOnlyList<string>? allowedVariables)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = (allowedVariables ?? []).ToHashSet(comparer);
        if (allowed.Count > MaxEnvironmentVariables)
        {
            throw new InvalidDataException("The environment variable allowlist is too large.");
        }

        var result = new Dictionary<string, string>(comparer);
        foreach (var name in allowed)
        {
            ValidateEnvironmentName(name);
            var inherited = Environment.GetEnvironmentVariable(name);
            if (inherited is not null)
            {
                result[name] = inherited;
            }
        }

        if (!arguments.TryGetProperty("environment", out var property))
        {
            return result;
        }

        if (property.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException("Tool argument 'environment' must be an object.");
        }

        foreach (var item in property.EnumerateObject())
        {
            ValidateEnvironmentName(item.Name);
            if (!allowed.Contains(item.Name))
            {
                throw new UnauthorizedAccessException(
                    $"Environment variable '{item.Name}' is not allowed by the effective Grant.");
            }

            if (item.Value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw new ArgumentException(
                    $"Environment variable '{item.Name}' must have a string value.");
            }

            result[item.Name] = item.Value.GetString() ?? string.Empty;
        }

        return result;
    }

    public static string? ParseStandardInput(
        System.Text.Json.JsonElement arguments,
        string propertyName = "stdin")
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        var value = property.GetString() ?? string.Empty;
        ValidateStandardInput(value);
        return value;
    }

    public static void ValidateStandardInput(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Utf8.GetByteCount(value) > MaxInputBytes)
        {
            throw new InvalidDataException("Process standard input exceeds the 1 MB limit.");
        }
    }

    public static async Task<ProcessOutput> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string? standardInput,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var variable in environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The process could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"The process could not be started: {ex.Message}",
                ex);
        }

        try
        {
            var outputBudget = new OutputBudget(MaxTotalOutputBytes);
            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                outputBudget,
                ct);
            var stderrTask = ReadBoundedAsync(
                process.StandardError.BaseStream,
                outputBudget,
                ct);
            var inputTask = WriteInputAsync(process, standardInput, ct);
            var waitTask = process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask, inputTask, waitTask).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessOutput(
                process.ExitCode,
                Utf8.GetString(stdout.Content),
                Utf8.GetString(stderr.Content),
                stdout.IsTruncated,
                stderr.IsTruncated);
        }
        catch
        {
            TryKillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private static PinnedExecutable PinExecutable(string executablePath)
    {
        var path = Path.GetFullPath(executablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The allowlisted executable was not found.", path);
        }

        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new UnauthorizedAccessException(
                    "The executable link target could not be resolved.");
            path = Path.GetFullPath(target.FullName);
        }

        if (!File.Exists(path) || Directory.Exists(path))
        {
            throw new FileNotFoundException("The executable target was not found.", path);
        }

        return new PinnedExecutable(path, BuiltinFsHandle.Open(path, isDirectory: false));
    }

    private static string FindOnRuntimePath(string executableName)
    {
        var candidates = OperatingSystem.IsWindows()
            && string.IsNullOrEmpty(Path.GetExtension(executableName))
                ? new[] { executableName, $"{executableName}.exe" }
                : [executableName];
        var runtimePath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in runtimePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Join(directory.Trim(), candidate);
                if (File.Exists(path) && !Directory.Exists(path))
                {
                    var extension = Path.GetExtension(path);
                    if (OperatingSystem.IsWindows()
                        && !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return Path.GetFullPath(path);
                }
            }
        }

        throw new FileNotFoundException(
            $"Executable '{executableName}' was not found on the Runtime PATH.");
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(
        Stream stream,
        OutputBudget totalBudget,
        CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return new BoundedOutput(output.ToArray(), IsTruncated: false);
            }

            var streamRemaining = MaxOutputBytesPerStream - (int)output.Length;
            var accepted = totalBudget.Reserve(Math.Min(read, streamRemaining));
            if (accepted > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, accepted), ct).ConfigureAwait(false);
            }

            if (accepted < read)
            {
                await DrainAsync(stream, buffer, ct).ConfigureAwait(false);
                return new BoundedOutput(output.ToArray(), IsTruncated: true);
            }
        }
    }

    private static async Task DrainAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken ct)
    {
        while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) != 0)
        {
        }
    }

    private static async Task WriteInputAsync(
        Process process,
        string? standardInput,
        CancellationToken ct)
    {
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }

        process.StandardInput.Close();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void ValidateEnvironmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('=', StringComparison.Ordinal)
            || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An environment variable name is invalid.", nameof(name));
        }
    }

    public sealed class PinnedExecutable(string path, SafeFileHandle handle) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public string Path { get; } = path;

        public void Dispose() => Handle.Dispose();
    }

    public sealed record ProcessOutput(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool StandardOutputTruncated,
        bool StandardErrorTruncated);

    private sealed record BoundedOutput(byte[] Content, bool IsTruncated);

    private sealed class OutputBudget(int availableBytes)
    {
        private int _availableBytes = availableBytes;

        public int Reserve(int requestedBytes)
        {
            lock (this)
            {
                var reserved = Math.Min(requestedBytes, _availableBytes);
                _availableBytes -= reserved;
                return reserved;
            }
        }
    }
}
