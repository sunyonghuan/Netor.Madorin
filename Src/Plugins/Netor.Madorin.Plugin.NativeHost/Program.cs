using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Netor.Madorin.Plugin.NativeHost;

/// <summary>
/// NativeHost 子进程入口。
/// 接收宿主通过 stdin 发送的 JSON 命令，
/// 通过 NativeLibrary 加载原生 DLL 并调用其导出函数，
/// 将结果通过 stdout 返回给宿主。
///
/// 此进程完全隔离运行，原生 DLL 的崩溃只会导致本进程退出，
/// 不会影响宿主进程。
/// </summary>
internal static class Program
{
    // ──────── 函数指针委托 ────────

    private delegate IntPtr GetInfoDelegate();

    private delegate int InitDelegate(IntPtr configJson);

    private delegate IntPtr InvokeDelegate(IntPtr toolName, IntPtr argsJson);

    private delegate void FreeDelegate(IntPtr ptr);

    private delegate void DestroyDelegate();

    // ──────── 状态 ────────

    private static IntPtr s_libraryHandle;
    private static GetInfoDelegate? s_getInfo;
    private static InitDelegate? s_init;
    private static InvokeDelegate? s_invoke;
    private static FreeDelegate? s_free;
    private static DestroyDelegate? s_destroy;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine($"用法: {typeof(Program).Assembly.GetName().Name} <library-path>");
            return 1;
        }

        var libraryPath = args[0];

        try
        {
            LoadLibrary(libraryPath);
            RunMessageLoop();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NativeHost 致命错误: {ex}");
            return 2;
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// 加载原生 DLL 并绑定所有导出函数。
    /// </summary>
    private static void LoadLibrary(string libraryPath)
    {
        s_libraryHandle = NativeLibrary.Load(libraryPath);

        s_getInfo = GetExportedDelegate<GetInfoDelegate>("cortana_plugin_get_info");
        s_init = GetExportedDelegate<InitDelegate>("cortana_plugin_init", required: false);
        s_invoke = GetExportedDelegate<InvokeDelegate>("cortana_plugin_invoke");
        s_free = GetExportedDelegate<FreeDelegate>("cortana_plugin_free");
        s_destroy = GetExportedDelegate<DestroyDelegate>("cortana_plugin_destroy", required: false);

        Console.Error.WriteLine($"已加载原生库: {libraryPath}");
    }

    /// <summary>
    /// stdin/stdout 消息循环。
    /// </summary>
    private static void RunMessageLoop()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string? line;

        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            HostResponse response;

            try
            {
                var request = JsonSerializer.Deserialize(line, NativeHostJsonContext.Default.HostRequest);

                if (request is null)
                {
                    response = HostResponse.Fail("请求反序列化失败");
                }
                else
                {
                    response = HandleRequest(request);
                }
            }
            catch (JsonException ex)
            {
                response = HostResponse.Fail($"JSON 解析错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                response = HostResponse.Fail($"执行异常: {ex.Message}");
                Console.Error.WriteLine($"请求处理异常: {ex}");
            }

            var json = JsonSerializer.Serialize(response, NativeHostJsonContext.Default.HostResponse);
            Console.WriteLine(json);
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// 分发请求到对应处理方法。
    /// </summary>
    private static HostResponse HandleRequest(HostRequest request) => request.Method switch
    {
        "get_info" => HandleGetInfo(),
        "init" => HandleInit(request.Args),
        "invoke" => HandleInvoke(request.ToolName, request.Args),
        "destroy" => HandleDestroy(),
        _ => HostResponse.Fail($"未知方法: {request.Method}")
    };

    /// <summary>
    /// 调用 cortana_plugin_get_info() 获取插件信息。
    /// </summary>
    private static HostResponse HandleGetInfo()
    {
        if (s_getInfo is null)
            return HostResponse.Fail("原生库未导出 cortana_plugin_get_info");

        IntPtr resultPtr = IntPtr.Zero;

        try
        {
            resultPtr = s_getInfo();

            if (resultPtr == IntPtr.Zero)
                return HostResponse.Fail("cortana_plugin_get_info 返回空指针");

            var json = Marshal.PtrToStringUTF8(resultPtr);
            return HostResponse.Ok(json);
        }
        finally
        {
            if (resultPtr != IntPtr.Zero)
                s_free?.Invoke(resultPtr);
        }
    }

    /// <summary>
    /// 调用 cortana_plugin_init(configJson) 初始化插件。
    /// </summary>
    private static HostResponse HandleInit(string? configJson)
    {
        if (s_init is null)
        {
            // init 是可选的，未导出视为成功
            return HostResponse.Ok("1");
        }

        IntPtr configPtr = IntPtr.Zero;

        try
        {
            configPtr = Marshal.StringToCoTaskMemUTF8(configJson ?? "{}");
            int result = s_init(configPtr);
            return result != 0
                ? HostResponse.Ok(result.ToString())
                : HostResponse.Fail("cortana_plugin_init 返回 0（初始化失败）");
        }
        finally
        {
            if (configPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(configPtr);
        }
    }

    /// <summary>
    /// 调用 cortana_plugin_invoke(toolName, argsJson) 执行工具。
    /// </summary>
    private static HostResponse HandleInvoke(string? toolName, string? argsJson)
    {
        if (s_invoke is null)
            return HostResponse.Fail("原生库未导出 cortana_plugin_invoke");

        if (string.IsNullOrWhiteSpace(toolName))
            return HostResponse.Fail("缺少 toolName");

        IntPtr toolNamePtr = IntPtr.Zero;
        IntPtr argsPtr = IntPtr.Zero;
        IntPtr resultPtr = IntPtr.Zero;

        try
        {
            toolNamePtr = Marshal.StringToCoTaskMemUTF8(toolName);
            argsPtr = Marshal.StringToCoTaskMemUTF8(argsJson ?? "{}");
            resultPtr = s_invoke(toolNamePtr, argsPtr);

            if (resultPtr == IntPtr.Zero)
                return HostResponse.Fail($"cortana_plugin_invoke({toolName}) 返回空指针");

            var result = Marshal.PtrToStringUTF8(resultPtr);
            return HostResponse.Ok(result);
        }
        finally
        {
            if (toolNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(toolNamePtr);
            if (argsPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(argsPtr);
            if (resultPtr != IntPtr.Zero) s_free?.Invoke(resultPtr);
        }
    }

    /// <summary>
    /// 调用 cortana_plugin_destroy() 并退出消息循环。
    /// </summary>
    private static HostResponse HandleDestroy()
    {
        try
        {
            s_destroy?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cortana_plugin_destroy 异常: {ex.Message}");
        }

        // 返回成功后，消息循环将因 stdin 关闭而自然结束
        var response = HostResponse.Ok("destroyed");

        // 安排延迟退出，让响应先发回
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Environment.Exit(0);
        });

        return response;
    }

    /// <summary>
    /// 清理原生库句柄。
    /// </summary>
    private static void Cleanup()
    {
        try
        {
            s_destroy?.Invoke();
        }
        catch
        {
            // 忽略
        }

        if (s_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(s_libraryHandle);
            s_libraryHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 从原生库获取导出函数的委托。
    /// </summary>
    private static T? GetExportedDelegate<T>(string entryPoint, bool required = true) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(s_libraryHandle, entryPoint, out IntPtr funcPtr))
        {
            if (required)
                throw new EntryPointNotFoundException($"原生库未导出必需函数: {entryPoint}");

            Console.Error.WriteLine($"原生库未导出可选函数: {entryPoint}");
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
    }
}

// ──────── 协议消息类型（顶级声明，供 source generator 使用）────────

internal sealed record HostRequest
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("args")]
    public string? Args { get; init; }
}

internal sealed record HostResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    internal static HostResponse Ok(string? data) => new() { Success = true, Data = data };
    internal static HostResponse Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// AOT 兼容的 JSON 序列化上下文（source generator）。
/// </summary>
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
internal sealed partial class NativeHostJsonContext : JsonSerializerContext;
