using DesktopPet.Abstractions;

namespace DesktopPet.Configuration.Tests;

[TestClass]
public sealed class WindowPlacementServiceTests
{
    [TestMethod]
    public void ResolveStartupPlacement_RestoresSavedPlacement_WhenDisplayStillExists()
    {
        var saved = new PetWindowPlacement
        {
            MonitorDeviceName = @"\\.\DISPLAY2",
            X = 2100,
            Y = 600,
            Width = 420,
            Height = 560,
            RememberPlacement = true
        };

        var displays = new[]
        {
            new DisplayInfo(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
            new DisplayInfo(@"\\.\DISPLAY2", 1920, 0, 1920, 1080, false)
        };

        var result = WindowPlacementService.ResolveStartupPlacement(saved, displays);

        Assert.AreEqual(saved.X, result.X);
        Assert.AreEqual(saved.Y, result.Y);
        Assert.AreEqual(saved.Width, result.Width);
        Assert.AreEqual(saved.Height, result.Height);
        Assert.AreEqual(saved.MonitorDeviceName, result.MonitorDeviceName);
    }

    [TestMethod]
    public void ResolveStartupPlacement_FallsBackToPrimaryBottomRight_WhenSavedDisplayIsMissing()
    {
        var saved = new PetWindowPlacement
        {
            MonitorDeviceName = @"\\.\DISPLAY9",
            X = 5000,
            Y = 5000,
            Width = 420,
            Height = 560,
            RememberPlacement = true
        };

        var displays = new[]
        {
            new DisplayInfo(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true)
        };

        var result = WindowPlacementService.ResolveStartupPlacement(saved, displays);

        Assert.AreEqual(@"\\.\DISPLAY1", result.MonitorDeviceName);
        Assert.AreEqual(1920 - 420 - 24, result.X);
        Assert.AreEqual(1080 - 560 - 48, result.Y);
    }

    [TestMethod]
    public void ResolveStartupPlacement_NormalizesInvalidSize()
    {
        var saved = new PetWindowPlacement
        {
            Width = -1,
            Height = 0
        };

        var displays = new[]
        {
            new DisplayInfo(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true)
        };

        var result = WindowPlacementService.ResolveStartupPlacement(saved, displays);

        Assert.AreEqual(420, result.Width);
        Assert.AreEqual(560, result.Height);
    }
}
