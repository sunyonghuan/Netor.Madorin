using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class MemoryFileServiceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void ResolvePaths_AlwaysUseTheFixedMemoryFileName()
    {
        var userHome = Path.GetFullPath(Path.Join("home", "user"));
        var workspace = Path.GetFullPath(Path.Join("work", "project"));

        var globalPath = MemoryFileService.ResolveGlobalPath(userHome);
        var projectPath = MemoryFileService.ResolveProjectPath(workspace);

        Assert.AreEqual(
            Path.Combine(userHome, ".madorin", "memory.md"),
            globalPath);
        Assert.AreEqual(
            Path.Combine(workspace, ".madorin", "memory.md"),
            projectPath);
        Assert.AreEqual("memory.md", Path.GetFileName(globalPath));
        Assert.AreEqual("memory.md", Path.GetFileName(projectPath));
    }

    [TestMethod]
    public async Task GlobalMemory_ReplaceAndRead_RoundTripsWithoutBom()
    {
        var userHome = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(userHome);
            const string content = "# Memory\n\n- Prefer maintainable code.\n";

            await service.ReplaceAsync(
                MemoryScope.Global,
                content,
                TestContext.CancellationToken);

            Assert.AreEqual(
                Path.Combine(userHome, ".madorin", "memory.md"),
                service.GlobalPath);
            Assert.AreEqual(
                content,
                await service.ReadAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken));
            var bytes = await File.ReadAllBytesAsync(
                service.GlobalPath,
                TestContext.CancellationToken);
            Assert.IsFalse(
                bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF);
        }
        finally
        {
            Directory.Delete(userHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_Effective_MergesGlobalBeforeProject()
    {
        var root = CreateTempDirectory();
        try
        {
            var userHome = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "workspace");
            var service = new MemoryFileService(userHome, workspace);
            const string global = "# Memory\n\n- Global rule.\n";
            const string project = "# Memory\n\n- Project rule.\n";
            await service.ReplaceAsync(
                MemoryScope.Global,
                global,
                TestContext.CancellationToken);
            await service.ReplaceAsync(
                MemoryScope.Project,
                project,
                TestContext.CancellationToken);

            var effective = await service.ReadAsync(
                MemoryScope.Effective,
                TestContext.CancellationToken);

            Assert.AreEqual($"{global}\n---\n{project}", effective);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplaceAsync_ContentOver32KiB_ThrowsInvalidDataException()
    {
        var userHome = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(userHome);
            var oversized = new string('x', MemoryFileService.MaximumFileSizeBytes + 1);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await service.ReplaceAsync(
                    MemoryScope.Global,
                    oversized,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(userHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_FileOver32KiB_ThrowsInvalidDataException()
    {
        var userHome = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(userHome);
            Directory.CreateDirectory(Path.GetDirectoryName(service.GlobalPath)!);
            await File.WriteAllBytesAsync(
                service.GlobalPath,
                new byte[MemoryFileService.MaximumFileSizeBytes + 1],
                TestContext.CancellationToken);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await service.ReadAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(userHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplaceAsync_FailedWrite_PreservesExistingFile()
    {
        var userHome = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(userHome);
            const string original = "# Memory\n\n- Existing content.\n";
            await service.ReplaceAsync(
                MemoryScope.Global,
                original,
                TestContext.CancellationToken);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await service.ReplaceAsync(
                    MemoryScope.Global,
                    new string('x', MemoryFileService.MaximumFileSizeBytes + 1),
                    TestContext.CancellationToken));

            var actual = await service.ReadAsync(
                MemoryScope.Global,
                TestContext.CancellationToken);
            Assert.AreEqual(original, actual);
            Assert.IsEmpty(Directory.GetFiles(
                Path.GetDirectoryName(service.GlobalPath)!,
                "*.tmp"));
        }
        finally
        {
            Directory.Delete(userHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task AppendAsync_ConcurrentWriters_DoNotLoseCommittedItems()
    {
        var userHome = CreateTempDirectory();
        try
        {
            var first = new MemoryFileService(userHome);
            var second = new MemoryFileService(userHome);
            var writes = Enumerable.Range(0, 20)
                .Select(index => (index & 1) == 0
                    ? first.AppendAsync(
                        MemoryScope.Global,
                        $"item-{index}",
                        TestContext.CancellationToken)
                    : second.AppendAsync(
                        MemoryScope.Global,
                        $"item-{index}",
                        TestContext.CancellationToken));

            await Task.WhenAll(writes);

            var content = await first.ReadAsync(
                MemoryScope.Global,
                TestContext.CancellationToken);
            Assert.IsNotNull(content);
            for (var index = 0; index < 20; index++)
            {
                StringAssert.Contains(content, $"- item-{index}\n");
            }
        }
        finally
        {
            Directory.Delete(userHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_MissingFiles_ReturnsNullForEveryScope()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(
                Path.Join(root, "home"),
                Path.Join(root, "workspace"));

            Assert.IsNull(await service.ReadAsync(
                MemoryScope.Global,
                TestContext.CancellationToken));
            Assert.IsNull(await service.ReadAsync(
                MemoryScope.Project,
                TestContext.CancellationToken));
            Assert.IsNull(await service.ReadAsync(
                MemoryScope.Effective,
                TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_EffectiveWithOneExistingFile_ReturnsThatFileOnly()
    {
        var root = CreateTempDirectory();
        try
        {
            const string globalContent = "# Memory\n\n- Global only.\n";
            const string projectContent = "# Memory\n\n- Project only.\n";
            var globalOnly = new MemoryFileService(
                Path.Join(root, "global-home"),
                Path.Join(root, "global-workspace"));
            await globalOnly.ReplaceAsync(
                MemoryScope.Global,
                globalContent,
                TestContext.CancellationToken);
            var projectOnly = new MemoryFileService(
                Path.Join(root, "project-home"),
                Path.Join(root, "project-workspace"));
            await projectOnly.ReplaceAsync(
                MemoryScope.Project,
                projectContent,
                TestContext.CancellationToken);

            Assert.AreEqual(
                globalContent,
                await globalOnly.ReadAsync(
                    MemoryScope.Effective,
                    TestContext.CancellationToken));
            Assert.AreEqual(
                projectContent,
                await projectOnly.ReadAsync(
                    MemoryScope.Effective,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClearAsync_PreservesFileAndRemovesBody()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(root);
            await service.AppendAsync(
                MemoryScope.Global,
                "Temporary rule.",
                TestContext.CancellationToken);

            await service.ClearAsync(MemoryScope.Global, TestContext.CancellationToken);

            Assert.IsTrue(File.Exists(service.GlobalPath));
            Assert.AreEqual(
                "# Memory\n",
                await service.ReadAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComposeAsync_EmptyFiles_DoesNotInjectPlaceholderSections()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(
                Path.Join(root, "home"),
                Path.Join(root, "workspace"));
            await service.ReplaceAsync(
                MemoryScope.Global,
                string.Empty,
                TestContext.CancellationToken);
            await service.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n",
                TestContext.CancellationToken);

            var snapshot = await new AgentContextComposer(service).ComposeAsync(
                "System prompt.",
                TestContext.CancellationToken);

            Assert.AreEqual("System prompt.", snapshot.Instructions);
            Assert.IsNotNull(snapshot.GlobalMemoryHash);
            Assert.IsNotNull(snapshot.ProjectMemoryHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComposeAsync_FileChanges_DoNotMutateCapturedInvocation()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(
                Path.Join(root, "home"),
                Path.Join(root, "workspace"));
            await service.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Old rule.\n",
                TestContext.CancellationToken);
            var composer = new AgentContextComposer(service);

            var currentInvocation = await composer.ComposeAsync(
                "System prompt.",
                TestContext.CancellationToken);
            await service.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- New rule.\n",
                TestContext.CancellationToken);
            var nextInvocation = await composer.ComposeAsync(
                "System prompt.",
                TestContext.CancellationToken);

            StringAssert.Contains(currentInvocation.Instructions, "Old rule.");
            Assert.IsFalse(currentInvocation.Instructions.Contains(
                "New rule.",
                StringComparison.Ordinal));
            StringAssert.Contains(nextInvocation.Instructions, "New rule.");
            Assert.AreNotEqual(
                currentInvocation.GlobalMemoryHash,
                nextInvocation.GlobalMemoryHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitAsync_ExistingFile_DoesNotOverwriteContent()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(root);
            const string content = "# Memory\n\n- Existing rule.\n";
            await service.ReplaceAsync(
                MemoryScope.Global,
                content,
                TestContext.CancellationToken);

            await service.InitAsync(MemoryScope.Global, TestContext.CancellationToken);

            Assert.AreEqual(
                content,
                await service.ReadAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsync_InvalidUtf8_ThrowsInvalidDataException()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(root);
            Directory.CreateDirectory(Path.GetDirectoryName(service.GlobalPath)!);
            await File.WriteAllBytesAsync(
                service.GlobalPath,
                [0xC3, 0x28],
                TestContext.CancellationToken);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => service.ReadAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComposeAsync_SameGlobalAndProjectPath_InjectsOnlyGlobalOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(root, root);
            await service.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Shared rule.\n",
                TestContext.CancellationToken);

            var snapshot = await new AgentContextComposer(service).ComposeAsync(
                "System prompt.",
                TestContext.CancellationToken);

            Assert.AreEqual(
                1,
                snapshot.Instructions.Split("Shared rule.", StringSplitOptions.None).Length - 1);
            Assert.IsNotNull(snapshot.GlobalMemoryHash);
            Assert.IsNull(snapshot.ProjectMemoryHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProjectOperations_WithoutWorkspace_ThrowInvalidOperationException()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MemoryFileService(root);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.ReadAsync(
                    MemoryScope.Project,
                    TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            $"madorin-memory-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
