using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netor.Madorin.Plugin.Process.Logging;
using Netor.Madorin.Plugin.Process.Settings;

namespace Netor.Madorin.Plugin.Process.Tests;

[TestClass]
public sealed class FileLoggerProviderTests
{
    private string _directory = null!;
    private readonly TestLogClock _clock = new();
    private PluginSettingsAccessor _settings = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("process-logs-").FullName;
        _settings = new PluginSettingsAccessor();
        _settings.Set(PluginSettings.FromJson(JsonSerializer.Serialize(new { dataDirectory = _directory })));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void SameMinute_AppendsExistingFileAndReopenedProvider()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
        File.WriteAllText(PathFor("2610121304"), "existing\n");
        using (var provider = CreateProvider())
        {
            provider.CreateLogger("test").LogWarning("first");
            _clock.Advance(TimeSpan.FromSeconds(20));
            provider.CreateLogger("test").LogWarning("second");
        }
        using (var provider = CreateProvider()) provider.CreateLogger("test").LogWarning("third");
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_directory, "logs")).Length);
        var lines = ReadLines(PathFor("2610121304"));
        Assert.AreEqual(4, lines.Length);
        Assert.AreEqual("existing", lines[0]);
        StringAssert.Contains(lines[1], "first");
        StringAssert.Contains(lines[2], "second");
        StringAssert.Contains(lines[3], "third");
    }

    [TestMethod]
    public void NextMinute_RotatesAndReleasesPreviousFile()
    {
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("test");
        logger.LogWarning("before");
        _clock.Advance(TimeSpan.FromMinutes(1));
        logger.LogWarning("after");
        using var oldFile = File.Open(PathFor("2610121304"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(_directory, "logs")).Length);
        var current = string.Join(Environment.NewLine, ReadLines(PathFor("2610121305")));
        StringAssert.Contains(current, "after");
        Assert.IsFalse(current.Contains("before", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Dispose_IsIdempotentReleasesHandleAndIgnoresFurtherWrites()
    {
        var provider = CreateProvider();
        var logger = provider.CreateLogger("test");
        logger.LogWarning("before-dispose");
        provider.Dispose();
        provider.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(1));
        logger.LogWarning("after-dispose");
        using var file = File.Open(PathFor("2610121304"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsFalse(File.Exists(PathFor("2610121305")));
    }

    [TestMethod]
    public void FailedOpen_RetriesInSameMinuteAfterPathBecomesAvailable()
    {
        var logs = Path.Combine(_directory, "logs");
        File.WriteAllText(logs, "blocking-file");
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("test");
        logger.LogWarning("lost");
        File.Delete(logs);
        logger.LogWarning("recovered");
        var lines = ReadLines(PathFor("2610121304"));
        Assert.AreEqual(1, lines.Length);
        StringAssert.Contains(lines[0], "recovered");
    }

    [TestMethod]
    public void FailedRotation_RetriesWithoutWritingToPreviousMinute()
    {
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("test");
        logger.LogWarning("before");
        _clock.Advance(TimeSpan.FromMinutes(1));
        using (var blocked = File.Open(PathFor("2610121305"), FileMode.Create, FileAccess.Write, FileShare.None))
            logger.LogWarning("blocked");
        logger.LogWarning("recovered");
        Assert.AreEqual(1, ReadLines(PathFor("2610121304")).Length);
        var lines = ReadLines(PathFor("2610121305"));
        Assert.AreEqual(1, lines.Length);
        StringAssert.Contains(lines[0], "recovered");
    }

    [TestMethod]
    public void UninitializedSettings_RetriesAfterInitialization()
    {
        var accessor = new PluginSettingsAccessor();
        using var provider = new FileLoggerProvider(accessor, LogLevel.Warning, _clock);
        var logger = provider.CreateLogger("test");
        logger.LogWarning("not-ready");
        accessor.Set(_settings.Value);
        logger.LogWarning("ready");
        Assert.AreEqual(1, ReadLines(PathFor("2610121304")).Length);
    }

    private static string[] ReadLines(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    private FileLoggerProvider CreateProvider() => new(_settings, LogLevel.Warning, _clock);
    private string PathFor(string stamp) => Path.Combine(_directory, "logs", $"{stamp}.log");
}

internal sealed class TestLogClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 13, 4, 5, TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}
