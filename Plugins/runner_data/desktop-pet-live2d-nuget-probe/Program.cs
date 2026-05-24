using Live2DCSharpSDK;
using Live2DCSharpSDK.Framework.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

Console.WriteLine("DesktopPet Live2D NuGet AOT probe starting.");
Console.WriteLine($"sdk-info: {SDKInfo.Version}");

try
{
    var version = CubismCore.Version();
    Console.WriteLine($"cubism-core-version: {version}");
    RunNativeCoreModelProbe();
}
catch (DllNotFoundException ex)
{
    Console.WriteLine($"cubism-core: missing native dll ({ex.Message.Split(Environment.NewLine)[0]})");
}
catch (Exception ex)
{
    Console.WriteLine($"cubism-core: failed ({ex.GetType().Name}: {ex.Message})");
}

Console.WriteLine("DesktopPet Live2D NuGet AOT probe completed.");

static unsafe void RunNativeCoreModelProbe()
{
    var mocPath = FindMocPath();
    if (mocPath is null)
    {
        Console.WriteLine("native-core-model: skipped (sample moc3 not found)");
        return;
    }

    var mocBytes = File.ReadAllBytes(mocPath);
    using var mocMemory = AlignedBuffer.CopyFrom(mocBytes, 64);

    Console.WriteLine($"native-core-moc: {Path.GetFileName(mocPath)} bytes={mocBytes.Length}");
    Console.WriteLine($"native-core-latest-moc-version: {CubismCoreNative.GetLatestMocVersion()}");
    Console.WriteLine($"native-core-moc-version: {CubismCoreNative.GetMocVersion(mocMemory.Pointer, (uint)mocBytes.Length)}");

    var consistent = CubismCoreNative.HasMocConsistency(mocMemory.Pointer, (uint)mocBytes.Length);
    Console.WriteLine($"native-core-moc-consistency: {consistent}");
    if (consistent == 0)
    {
        return;
    }

    var moc = CubismCoreNative.ReviveMocInPlace(mocMemory.Pointer, (uint)mocBytes.Length);
    Console.WriteLine($"native-core-revive-moc: {(moc == 0 ? "failed" : "ok")}");
    if (moc == 0)
    {
        return;
    }

    var modelSize = CubismCoreNative.GetSizeofModel(moc);
    Console.WriteLine($"native-core-model-size: {modelSize}");
    using var modelMemory = AlignedBuffer.Allocate(modelSize, 16);

    var model = CubismCoreNative.InitializeModelInPlace(moc, modelMemory.Pointer, modelSize);
    Console.WriteLine($"native-core-init-model: {(model == 0 ? "failed" : "ok")}");
    if (model == 0)
    {
        return;
    }

    var parameterCount = CubismCoreNative.GetParameterCount(model);
    var drawableCount = CubismCoreNative.GetDrawableCount(model);
    Console.WriteLine($"native-core-parameter-count: {parameterCount}");
    Console.WriteLine($"native-core-drawable-count: {drawableCount}");

    var mouthIndex = FindParameterIndex(model, parameterCount, "ParamMouthOpenY");
    Console.WriteLine($"native-core-mouth-param-index: {mouthIndex}");
    if (mouthIndex >= 0)
    {
        var values = (float*)CubismCoreNative.GetParameterValues(model);
        var before = values[mouthIndex];
        values[mouthIndex] = 0.85f;
        CubismCoreNative.UpdateModel(model);
        Console.WriteLine($"native-core-mouth-param-write: {before:0.###}-> {values[mouthIndex]:0.###}");
    }
    else
    {
        CubismCoreNative.UpdateModel(model);
        Console.WriteLine("native-core-update-model: ok");
    }
}

static string? FindMocPath()
{
    foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var current = root;
        for (var i = 0; i < 10 && current is not null; i++)
        {
            var candidate = Path.Combine(
                current,
                "runner_data",
                "live2d-cubism-native-sdk-5-r5",
                "CubismSdkForNative-5-r.5",
                "Samples",
                "Resources",
                "Haru",
                "Haru.moc3");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    return null;
}

static unsafe int FindParameterIndex(nint model, int parameterCount, string parameterId)
{
    var ids = (nint*)CubismCoreNative.GetParameterIds(model);
    for (var i = 0; i < parameterCount; i++)
    {
        var id = Marshal.PtrToStringAnsi(ids[i]);
        if (id == parameterId)
        {
            return i;
        }
    }

    return -1;
}

internal unsafe sealed class AlignedBuffer : IDisposable
{
    public void* Pointer { get; }

    private AlignedBuffer(void* pointer)
    {
        Pointer = pointer;
    }

    public static AlignedBuffer Allocate(uint size, nuint alignment)
    {
        var pointer = NativeMemory.AlignedAlloc(size, alignment);
        if (pointer is null)
        {
            throw new OutOfMemoryException();
        }

        NativeMemory.Clear(pointer, size);
        return new AlignedBuffer(pointer);
    }

    public static AlignedBuffer CopyFrom(ReadOnlySpan<byte> source, nuint alignment)
    {
        var buffer = Allocate((uint)source.Length, alignment);
        source.CopyTo(new Span<byte>(buffer.Pointer, source.Length));
        return buffer;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(Pointer);
    }
}

internal static unsafe partial class CubismCoreNative
{
    private const string LibraryName = "Live2DCubismCore";

    [LibraryImport(LibraryName, EntryPoint = "csmGetLatestMocVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetLatestMocVersion();

    [LibraryImport(LibraryName, EntryPoint = "csmGetMocVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetMocVersion(void* address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmHasMocConsistency")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int HasMocConsistency(void* address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmReviveMocInPlace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint ReviveMocInPlace(void* address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmGetSizeofModel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetSizeofModel(nint moc);

    [LibraryImport(LibraryName, EntryPoint = "csmInitializeModelInPlace")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint InitializeModelInPlace(nint moc, void* address, uint size);

    [LibraryImport(LibraryName, EntryPoint = "csmUpdateModel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void UpdateModel(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int GetParameterCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int GetDrawableCount(nint model);
}
