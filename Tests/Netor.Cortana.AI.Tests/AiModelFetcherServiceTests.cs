using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Tests;

[TestClass]
public sealed class AiModelFetcherServiceTests
{
    [TestMethod]
    public async Task FetchAndSaveModelsAsync_RemoteFailure_LeavesLocalModelsUnchanged()
    {
        using var db = CreateDatabase();
        var modelService = new AiModelService(db);
        modelService.Add(CreateModel("local-id", "local-model"));
        var driver = new TestProviderDriver { Failure = new InvalidOperationException("remote failure") };
        var fetcher = new AiModelFetcherService(new AiProviderDriverRegistry([driver]), modelService);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => fetcher.FetchAndSaveModelsAsync(CreateProvider()));

        var model = modelService.GetById("local-id");
        Assert.IsNotNull(model);
        Assert.AreEqual("local-model", model!.Name);
    }

    [TestMethod]
    public async Task FetchAndSaveModelsAsync_EmptySuccessfulResult_RemovesProviderModels()
    {
        using var db = CreateDatabase();
        var modelService = new AiModelService(db);
        modelService.Add(CreateModel("local-id", "local-model"));
        var fetcher = new AiModelFetcherService(
            new AiProviderDriverRegistry([new TestProviderDriver()]),
            modelService);

        var result = await fetcher.FetchAndSaveModelsAsync(CreateProvider());

        Assert.IsEmpty(result);
        Assert.IsEmpty(modelService.GetAllByProviderId("provider-1"));
    }

    private static CortanaDbContext CreateDatabase()
        => new(Path.Combine(Path.GetTempPath(), "cortana-ai-model-tests", $"{Guid.NewGuid():N}.db"));

    private static AiProviderEntity CreateProvider() => new()
    {
        Id = "provider-1",
        Name = "Test Provider",
        ProviderType = "TestProvider",
    };

    private static AiModelEntity CreateModel(string id, string name) => new()
    {
        Id = id,
        Name = name,
        DisplayName = name,
        ProviderId = "provider-1",
        IsEnabled = true,
    };

    private sealed class TestProviderDriver : AiProviderDriverBase
    {
        public Exception? Failure { get; init; }

        public override AiProviderDriverDefinition Definition { get; } =
            new("TestProvider", "Test Provider", true);

        public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
            => throw new NotSupportedException();

        public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
            => new();

        public override Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
            AiProviderEntity provider,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<RemoteModelDescriptor>>(Failure);

            return Task.FromResult<IReadOnlyList<RemoteModelDescriptor>>([]);
        }
    }
}
