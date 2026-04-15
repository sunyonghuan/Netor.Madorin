using Netor.Cortana.Plugin.Native.Debugger;

await PluginDebugRunner.RunAsync(options =>
{
    options.WsPort = 12841;
});
