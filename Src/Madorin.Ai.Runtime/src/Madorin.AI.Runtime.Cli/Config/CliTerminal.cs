using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>Provides injectable terminal input and secure secret entry for CLI workflows.</summary>
public interface ICliTerminal
{
    /// <summary>Writes text without a trailing line terminator.</summary>
    public void Write(string value);

    /// <summary>Writes text with a trailing line terminator.</summary>
    public void WriteLine(string value = "");

    /// <summary>Clears an attached interactive console without emitting terminal control sequences.</summary>
    public void Clear();

    /// <summary>Reads one input line.</summary>
    public ValueTask<string?> ReadLineAsync(CancellationToken ct = default);

    /// <summary>Reads a secret without echoing it when attached to an interactive console.</summary>
    public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default);
}

/// <summary>Default CLI terminal adapter.</summary>
public sealed class CliTerminal : ICliTerminal
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _useSecureConsoleInput;

    /// <summary>Creates an injectable reader/writer terminal.</summary>
    public CliTerminal(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _useSecureConsoleInput = ReferenceEquals(input, Console.In)
            && ReferenceEquals(output, Console.Out)
            && !Console.IsInputRedirected;
    }

    /// <summary>Creates the production console terminal.</summary>
    public static CliTerminal ConsoleTerminal { get; } = new(Console.In, Console.Out);

    /// <inheritdoc />
    public void Write(string value) => _output.Write(value);

    /// <inheritdoc />
    public void WriteLine(string value = "") => _output.WriteLine(value);

    /// <inheritdoc />
    public void Clear()
    {
        if (ReferenceEquals(_output, Console.Out) && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default) =>
        await _input.ReadLineAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<string> ReadSecretAsync(
        string prompt,
        CancellationToken ct = default)
    {
        Write(prompt);
        if (!_useSecureConsoleInput)
        {
            return await _input.ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty;
        }

        var value = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
