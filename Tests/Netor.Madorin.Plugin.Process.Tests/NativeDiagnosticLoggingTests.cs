using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netor.Madorin.Plugin.Process.Tests;

namespace Sdk.Logging.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeDiagnosticLoggingTests
{
    private string _directory = null!;
    private readonly TestLogClock _clock = new();

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("native-logs-").FullName;
        NativeLogFixture.SetDiagnosticContext(_directory, _clock);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeLogFixture.SetDiagnosticContext(null, TimeProvider.System);
        Directory.Delete(_directory, true);
    }

    [TestMethod]
    public void SameMinute_AppendsExistingDiagnosticFile()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
        File.WriteAllText(PathFor("2610121304"), "existing\n");
        NativeLogFixture.WriteDiagnostic("first");
        _clock.Advance(TimeSpan.FromSeconds(20));
        NativeLogFixture.WriteDiagnostic("second");
        var lines = File.ReadAllLines(PathFor("2610121304"));
        Assert.AreEqual(3, lines.Length);
        Assert.AreEqual("existing", lines[0]);
        StringAssert.Contains(lines[1], "|Error|NativeRuntime|first|");
        StringAssert.Contains(lines[2], "|Error|NativeRuntime|second|");
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_directory, "logs")).Length);
    }

    [TestMethod]
    public void NextMinute_WritesDifferentDiagnosticFile()
    {
        NativeLogFixture.WriteDiagnostic("before");
        _clock.Advance(TimeSpan.FromMinutes(1));
        NativeLogFixture.WriteDiagnostic("after");
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(_directory, "logs")).Length);
        StringAssert.Contains(File.ReadAllText(PathFor("2610121304")), "|before|");
        var current = File.ReadAllText(PathFor("2610121305"));
        StringAssert.Contains(current, "|after|");
        Assert.IsFalse(current.Contains("|before|", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_DisposesFileHandleImmediately()
    {
        NativeLogFixture.WriteDiagnostic("released");
        using var file = File.Open(PathFor("2610121304"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(file.Length > 0);
    }

    [TestMethod]
    public void FailedWrite_RetriesInSameMinute()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
        using (var blocked = File.Open(PathFor("2610121304"), FileMode.Create, FileAccess.Write, FileShare.None))
            NativeLogFixture.WriteDiagnostic("blocked");
        NativeLogFixture.WriteDiagnostic("recovered");
        var lines = File.ReadAllLines(PathFor("2610121304"));
        Assert.AreEqual(1, lines.Length);
        StringAssert.Contains(lines[0], "|recovered|");
    }

    private string PathFor(string stamp) => Path.Combine(_directory, "logs", $"{stamp}.log");
}

[Netor.Madorin.Plugin.Native.Plugin(Id = "diagnostic_test", Name = "Diagnostic test", Version = "1.0.0")]
public static partial class NativeLogFixture
{
    public static void Configure(IServiceCollection services) { }

    internal static void SetDiagnosticContext(string? directory, TimeProvider clock)
    {
        s_dataDirectory = directory;
        s_diagnosticTimeProvider = clock;
    }

    internal static void WriteDiagnostic(string phase) =>
        WriteRuntimeDiagnostic(phase, new InvalidOperationException("test failure"));
}

[Netor.Madorin.Plugin.Native.Tool(Name = "diagnostic")]
public sealed class NativeLogTestTool
{
    [Netor.Madorin.Plugin.Native.Tool(Name = "probe", Description = "Diagnostic generation fixture")]
    public string Probe() => "ok";
}
