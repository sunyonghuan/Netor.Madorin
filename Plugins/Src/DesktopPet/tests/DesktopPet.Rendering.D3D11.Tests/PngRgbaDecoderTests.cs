namespace DesktopPet.Rendering.D3D11.Tests;

[TestClass]
public sealed class PngRgbaDecoderTests
{
    [TestMethod]
    public void DecodeHaruTexturesReturnsRgbaPixels()
    {
        foreach (var texturePath in FindHaruTextures())
        {
            var image = PngRgbaDecoder.Decode(texturePath);

            Assert.AreEqual(2048, image.Width);
            Assert.AreEqual(2048, image.Height);
            Assert.HasCount(image.Width * image.Height * 4, image.RgbaPixels);
            Assert.IsTrue(image.RgbaPixels.Any(channel => channel != 0));
        }
    }

    private static IEnumerable<string> FindHaruTextures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var assetsDirectory = Path.Combine(
                directory.FullName,
                "Src",
                "DesktopPet",
                "assets",
                "live2d",
                "models",
                "Haru",
                "Haru.2048");

            if (Directory.Exists(assetsDirectory))
            {
                return Directory.GetFiles(assetsDirectory, "texture_*.png").Order(StringComparer.Ordinal);
            }

            directory = directory.Parent;
        }

        Assert.Fail("Haru texture directory was not found.");
        return [];
    }
}
