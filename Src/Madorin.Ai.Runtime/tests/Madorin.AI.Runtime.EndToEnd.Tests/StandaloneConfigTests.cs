using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class StandaloneConfigTests
{
    [TestMethod]
    public async Task LoadAsync_WhenConfigDoesNotExist_ReturnsNull()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(directory);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.IsNull(config);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsConfiguration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(directory);
            var expected = CreateConfig("gpt-5");

            await loader.SaveAsync(expected, TestContext.CancellationToken);
            var actual = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.DefaultProvider, actual.DefaultProvider);
            Assert.AreEqual(expected.DefaultModel, actual.DefaultModel);
            Assert.AreEqual(expected.DefaultAgent, actual.DefaultAgent);
            Assert.HasCount(1, actual.Providers);
            Assert.AreEqual(expected.Providers[0].Name, actual.Providers[0].Name);
            Assert.AreEqual(expected.Providers[0].Protocol, actual.Providers[0].Protocol);
            Assert.AreEqual(expected.Providers[0].BaseUrl, actual.Providers[0].BaseUrl);
            Assert.AreEqual(expected.Providers[0].ApiKey, actual.Providers[0].ApiKey);
            Assert.AreEqual(
                expected.Providers[0].AuthenticationHeader,
                actual.Providers[0].AuthenticationHeader);
            Assert.AreEqual(
                expected.Providers[0].AuthenticationScheme,
                actual.Providers[0].AuthenticationScheme);
            CollectionAssert.AreEqual(expected.Providers[0].Models, actual.Providers[0].Models);

            var json = await File.ReadAllTextAsync(loader.ConfigPath, TestContext.CancellationToken);
            StringAssert.Contains(json, "\"defaultProvider\"");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WhileAnotherProcessStyleReaderIsOpen_Succeeds()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(directory);
            await loader.SaveAsync(CreateConfig("gpt-5"), TestContext.CancellationToken);
            await using var firstReader = new FileStream(
                loader.ConfigPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);

            var config = await loader.LoadAsync(TestContext.CancellationToken);

            Assert.IsNotNull(config);
            Assert.AreEqual("gpt-5", config.DefaultModel);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void MaskApiKey_WithConfiguredKey_ReturnsRedactedValue()
    {
        var masked = StandaloneConfigLoader.MaskApiKey("sk-test-secret");

        Assert.AreEqual("sk-t***", masked);
        Assert.DoesNotContain("secret", masked, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SaveAsync_WhenReplacementIsCancelled_PreservesExistingFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var loader = new StandaloneConfigLoader(directory);
            await loader.SaveAsync(CreateConfig("gpt-5"), TestContext.CancellationToken);
            var originalJson = await File.ReadAllTextAsync(
                loader.ConfigPath,
                TestContext.CancellationToken);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => loader.SaveAsync(CreateConfig("gpt-5.1"), cancellation.Token));

            var preservedJson = await File.ReadAllTextAsync(
                loader.ConfigPath,
                TestContext.CancellationToken);
            Assert.AreEqual(originalJson, preservedJson);

            var config = await loader.LoadAsync(TestContext.CancellationToken);
            Assert.IsNotNull(config);
            Assert.AreEqual("gpt-5", config.DefaultModel);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static StandaloneConfig CreateConfig(string model)
    {
        return new StandaloneConfig
        {
            DefaultProvider = "openai",
            DefaultModel = model,
            DefaultAgent = "default",
            Providers =
            [
                new ProviderEntry
                {
                    Name = "openai",
                    Protocol = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "sk-test-secret",
                    AuthenticationHeader = "X-Provider-Key",
                    AuthenticationScheme = "Token",
                    Models = [model]
                }
            ]
        };
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-config-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
