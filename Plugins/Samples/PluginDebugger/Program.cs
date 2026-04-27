using NativeTestPlugin;

using Netor.Cortana.Plugin.Native.Debugger;

var assembly = typeof(Startup).Assembly;

//Console.WriteLine("SocialPlugin Native plugin debugger started.");
//Console.WriteLine("Input format: <tool_name> {json_args}");
//Console.WriteLine("Examples:");
//Console.WriteLine("  get_web_endpoint {}");
//Console.WriteLine("  list_accounts {\"page\":1,\"pageSize\":20}");
//Console.WriteLine("  create_task {\"title\":\"test\",\"contentType\":\"text\",\"resourcePath\":\"demo\",\"targetAccountIds\":\"1,2\",\"scheduledAt\":\"\"}");
//Console.WriteLine("  exit");
Console.WriteLine();

await PluginDebugRunner.RunAsync(assembly);