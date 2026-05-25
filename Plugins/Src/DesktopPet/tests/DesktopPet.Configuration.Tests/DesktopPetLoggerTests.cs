using DesktopPet.Configuration;

namespace DesktopPet.Configuration.Tests;

[TestClass]
public sealed class DesktopPetLoggerTests
{
    [TestMethod]
    public void Info_WritesLogLine()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "DesktopPetTests", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new DesktopPetAppPaths(baseDirectory);
            var logger = new DesktopPetLogger(paths);

            logger.Info("hello");

            var text = File.ReadAllText(logger.LogPath);
            StringAssert.Contains(text, "[INFO] hello");
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
