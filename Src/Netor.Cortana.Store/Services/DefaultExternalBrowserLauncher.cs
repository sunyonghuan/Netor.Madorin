using System.Diagnostics;
using Netor.Cortana.Store.Abstractions;

namespace Netor.Cortana.Store.Services;

public sealed class DefaultExternalBrowserLauncher : IExternalBrowserLauncher
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }
}
