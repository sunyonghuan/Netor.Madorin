using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Netor.Cortana.Networks;

/// <summary>
/// Kestrel WebSocket Host 构建和释放辅助基类。
/// </summary>
public abstract class KestrelWebSocketHost
{
    protected static WebApplication BuildLocalhostApp(
        int port,
        Action<WebApplication> mapEndpoints)
    {
        ArgumentNullException.ThrowIfNull(mapEndpoints);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddRouting();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        var app = builder.Build();
        app.UseWebSockets();
        mapEndpoints(app);
        return app;
    }

    protected static async Task DisposeAppAsync(
        WebApplication? app,
        Action clearApp,
        Action<Exception> logIgnoredException,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clearApp);
        ArgumentNullException.ThrowIfNull(logIgnoredException);

        if (app is null)
        {
            return;
        }

        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logIgnoredException(ex);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            clearApp();
        }
    }
}
