using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

namespace Netor.Cortana.UI.Tests;

[TestClass]
public sealed class UiServiceRegistrationTests
{
    [TestMethod]
    public void ConfigureServices_RegistersDelegatedAgentServices()
    {
        var configureServices = typeof(App).GetMethod("ConfigureServices", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(configureServices);

        configureServices.Invoke(null, []);

        var servicesProperty = typeof(App).GetProperty("Services", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(servicesProperty);

        var services = (IServiceProvider?)servicesProperty.GetValue(null);
        Assert.IsNotNull(services);

        Assert.IsNotNull(services.GetService<Netor.Cortana.AI.Delegation.SystemToolCatalogService>());
        Assert.IsNotNull(services.GetService<Netor.Cortana.AI.Delegation.DelegatedAgentJobExecutor>());
        Assert.IsNotNull(services.GetService<Netor.Cortana.AI.Delegation.DelegatedAgentJobRunner>());
        Assert.IsNotNull(services.GetService<Netor.Cortana.AI.Delegation.ExpertDelegationTools>());
        Assert.IsNotNull(services.GetService<Netor.Cortana.Entitys.Services.DelegatedAgentJobService>());
    }
}
