using DesktopPet.Configuration;

namespace DesktopPet.Configuration.Tests;

[TestClass]
public sealed class DesktopPetAppPathsTests
{
    [TestMethod]
    public void Constructor_CreatesConfigurationAndLogsDirectories()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "DesktopPetTests", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new DesktopPetAppPaths(baseDirectory);

            Assert.IsTrue(Directory.Exists(paths.ConfigurationDirectory));
            Assert.IsTrue(Directory.Exists(paths.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }
}
