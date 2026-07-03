using System.Text.Json;

namespace Netor.Cortana.Plugin.Process.Hosting;

/// <summary>
/// 工具调用委托。由 Generator 为每个 <c>[Tool]</c> 方法生成一个实例，
/// 填入 <see cref="ProcessPluginHost"/> 的路由字典。
/// <para>
/// 委托签名：
/// <list type="number">
///   <item><paramref name="services"/>：当前请求解析到的 DI ServiceProvider</item>
///   <item><paramref name="args"/>：已解析的 JSON 根元素（工具参数）</item>
///   <item>返回值：工具返回的字符串（可以是 JSON 或纯文本）</item>
/// </list>
/// </para>
/// </summary>
public delegate ValueTask<string> ToolInvoker(IServiceProvider services, JsonElement args);
