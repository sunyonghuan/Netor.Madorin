namespace DesktopPet.Models.Live2D.Tests;

[TestClass]
public sealed class Live2DModelLoaderTests
{
    [TestMethod]
    public void LoadHaruModelInitializesCoreModel()
    {
        var modelJsonPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "live2d",
            "models",
            "Haru",
            "Haru.model3.json");

        using var model = new Live2DModelLoader().Load(modelJsonPath);

        Assert.AreEqual("Haru.model3", model.Info.Name);
        Assert.IsGreaterThan(0, model.Info.ParameterCount);
        Assert.IsGreaterThan(0, model.Info.DrawableCount);
        Assert.IsNotEmpty(model.Info.TexturePaths);
        Assert.IsGreaterThanOrEqualTo(0, model.Info.MouthParameterIndex);
    }

    [TestMethod]
    public void LoadBundledSampleModelsReturnsRenderableGeometry()
    {
        var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "live2d", "models");
        var loader = new Live2DModelLoader();
        var modelJsonFiles = loader.FindModelJsonFiles(modelsDirectory);

        CollectionAssert.IsSubsetOf(
            new[] { "Haru.model3.json", "Hiyori.model3.json", "Mao.model3.json", "Natori.model3.json" },
            modelJsonFiles.Select(Path.GetFileName).ToArray());

        foreach (var modelJsonPath in modelJsonFiles)
        {
            using var model = loader.Load(modelJsonPath);
            var snapshots = model.ReadDrawableSnapshots(maxCount: 8);

            Assert.IsGreaterThan(0, model.Info.ParameterCount, model.Info.Name);
            Assert.IsGreaterThan(0, model.Info.DrawableCount, model.Info.Name);
            Assert.IsNotEmpty(model.Info.TexturePaths, model.Info.Name);
            Assert.IsTrue(snapshots.Any(snapshot => snapshot.VertexCount > 0 && snapshot.IndexCount > 0), model.Info.Name);
        }
    }

    [TestMethod]
    public void SetMouthOpenUpdatesModelWithoutThrowing()
    {
        using var model = LoadHaru();

        model.SetMouthOpen(0.85f);
        model.SetMouthOpen(0.0f);
    }

    [TestMethod]
    public void ReadDrawableSnapshotsReturnsRenderableGeometry()
    {
        using var model = LoadHaru();
        var snapshots = model.ReadDrawableSnapshots(maxCount: 8);

        Assert.IsNotEmpty(snapshots);
        Assert.IsTrue(snapshots.Any(snapshot => snapshot.VertexCount > 0 && snapshot.IndexCount > 0));
        Assert.IsTrue(snapshots.All(snapshot => snapshot.VertexPositions.Count == snapshot.VertexCount * 2));
        Assert.IsTrue(snapshots.All(snapshot => snapshot.VertexUvs.Count == snapshot.VertexCount * 2));
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Indices.Count == snapshot.IndexCount));
    }

    [TestMethod]
    public void ReadDrawableSnapshotsReturnsRenderOrderAndMaskMetadata()
    {
        using var model = LoadHaru();
        var snapshots = model.ReadDrawableSnapshots();
        var drawableIndices = snapshots.Select(snapshot => snapshot.Index).ToHashSet();

        Assert.IsTrue(snapshots.All(snapshot => snapshot.RenderOrder >= 0));
        Assert.IsTrue(snapshots.Any(snapshot => snapshot.MaskIndices.Count > 0));
        Assert.IsTrue(snapshots
            .SelectMany(snapshot => snapshot.MaskIndices)
            .All(drawableIndices.Contains));
    }

    [TestMethod]
    public void AdvanceMotionUpdatesIdleParameters()
    {
        using var model = LoadHaru();

        model.AdvanceMotion(1.0f);

        var angleY = model.TryGetParameterValue("ParamAngleY");
        Assert.IsTrue(angleY is < -0.1f, "Haru idle motion should update ParamAngleY after one second.");
    }

    [TestMethod]
    public void LoadHaruAppliesPosePartOpacity()
    {
        using var model = LoadHaru();
        var snapshots = model.ReadDrawableSnapshots();

        Assert.IsTrue(
            snapshots.Any(snapshot => string.Equals(snapshot.ParentPartId, "Part01ArmRB001", StringComparison.Ordinal)
                && snapshot.ParentPartOpacity < 0.001f
                && snapshot.Opacity < 0.001f),
            "Haru pose should hide the second right-arm part by default.");
        Assert.IsTrue(
            snapshots.Any(snapshot => string.Equals(snapshot.ParentPartId, "Part01ArmLB001", StringComparison.Ordinal)
                && snapshot.ParentPartOpacity < 0.001f
                && snapshot.Opacity < 0.001f),
            "Haru pose should hide the second left-arm part by default.");
    }

    [TestMethod]
    public void SetMouthOpenAndReadDrawableSnapshotsCanRunConcurrently()
    {
        using var model = LoadHaru();
        using var stopped = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (!stopped.IsCancellationRequested)
            {
                model.SetMouthOpen(0.7f);
                model.SetMouthOpen(0.0f);
            }
        });

        try
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.IsNotEmpty(model.ReadDrawableSnapshots(maxCount: 4));
            }
        }
        finally
        {
            stopped.Cancel();
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(2)));
        }
    }

    private static Live2DModel LoadHaru()
    {
        var modelJsonPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "live2d",
            "models",
            "Haru",
            "Haru.model3.json");

        return new Live2DModelLoader().Load(modelJsonPath);
    }
}
