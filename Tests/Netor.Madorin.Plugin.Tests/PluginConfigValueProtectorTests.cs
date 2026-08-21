using System.Text.Json;
using System.Security.Cryptography;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

[assembly: DoNotParallelize]

namespace Netor.Madorin.Plugin.Tests;

[TestClass]
public sealed class PluginConfigValueProtectorTests
{
    private string _root = string.Empty;
    private TestAppPaths _appPaths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "cortana-plugin-tests", Guid.NewGuid().ToString("N"));
        _appPaths = new TestAppPaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [TestMethod]
    public void Protect_Should_ReturnProtectedValue_AndRecoverPlainText()
    {
        var protector = new PluginConfigValueProtector(_appPaths);

        var protectedValue = protector.Protect("secret-value");
        var recoveredValue = protector.GetEffectiveValue(protectedValue);

        Assert.IsTrue(protector.IsProtectedValue(protectedValue));
        Assert.AreNotEqual("secret-value", protectedValue);
        Assert.AreEqual("secret-value", recoveredValue);
    }

    [TestMethod]
    public void GetEffectiveValue_WithPlainText_Should_ReturnOriginalValue()
    {
        var protector = new PluginConfigValueProtector(_appPaths);

        var value = protector.GetEffectiveValue("legacy-plain-value");

        Assert.AreEqual("legacy-plain-value", value);
    }

    [TestMethod]
    public void BuildPluginConfigJson_WithProtectedValue_Should_InjectPlainTextValue()
    {
        using var db = new CortanaDbContext(Path.Combine(_root, "cortana.db"));
        var settingsService = new SystemSettingsService(db);
        var protector = new PluginConfigValueProtector(_appPaths);
        var provider = new PluginRuntimeConfigProvider(settingsService, protector);
        var protectedValue = protector.Protect("runtime-secret");
        settingsService.SetValue("Plugin:sample.plugin:Config:apiKey", protectedValue);

        var manifest = new PluginManifest
        {
            SettingsSchema =
            [
                new PluginSettingDescriptor
                {
                    Key = "apiKey",
                    Type = "secret",
                    Sensitive = true,
                }
            ]
        };

        var json = provider.BuildPluginConfigJson("sample.plugin", manifest);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("runtime-secret", document.RootElement.GetProperty("apiKey").GetString());
    }

    [TestMethod]
    public void BuildPluginConfigJson_WithNullType_Should_TreatValueAsString()
    {
        using var db = new CortanaDbContext(Path.Combine(_root, "cortana.db"));
        var settingsService = new SystemSettingsService(db);
        var protector = new PluginConfigValueProtector(_appPaths);
        var provider = new PluginRuntimeConfigProvider(settingsService, protector);
        settingsService.SetValue("Plugin:sample.plugin:Config:legacyValue", "raw-value");

        var manifest = new PluginManifest
        {
            SettingsSchema =
            [
                new PluginSettingDescriptor
                {
                    Key = "legacyValue",
                    Type = null!,
                }
            ]
        };

        var json = provider.BuildPluginConfigJson("sample.plugin", manifest);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("raw-value", document.RootElement.GetProperty("legacyValue").GetString());
    }

    [TestMethod]
    public void GetEffectiveValue_WithInvalidProtectedPayload_Should_ThrowCryptographicException()
    {
        var protector = new PluginConfigValueProtector(_appPaths);

        var exception = Assert.ThrowsExactly<CryptographicException>(
            () => protector.GetEffectiveValue("plugin-config-protected:v1:not-base64"));

        Assert.AreEqual("插件配置密文格式无效。", exception.Message);
    }

    [TestMethod]
    public void Protect_AfterInvalidStoredKeyIsFixed_Should_NotReuseInvalidKey()
    {
        var protector = new PluginConfigValueProtector(_appPaths);
        var keyPath = Path.Combine(_appPaths.UserDataDirectory, "security", "plugin-config.key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath, Convert.ToBase64String([1, 2, 3]));

        Assert.ThrowsExactly<CryptographicException>(() => protector.Protect("value"));

        File.Delete(keyPath);
        var protectedValue = protector.Protect("value");

        Assert.AreEqual("value", protector.GetEffectiveValue(protectedValue));
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory { get; } = Path.Combine(root, "workspace");

        public string UserDataDirectory { get; } = Path.Combine(root, "user");

        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

        public string PluginDirectory => UserPluginsDirectory;

        public string WorkspaceResourcesDirectory { get; } = Path.Combine(root, "workspace", ".cortana", "resources");

        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");

        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
