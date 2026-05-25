using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;

namespace DesktopPet.Models.Live2D;

internal static unsafe partial class CubismCoreNative
{
    private const string LibraryName = "Live2DCubismCore";

    static CubismCoreNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(CubismCoreNative).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)
            && !string.Equals(libraryName, "Live2DCubismCore.dll", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        foreach (var candidate in GetCandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "Live2DCubismCore.dll");
        yield return Path.Combine(baseDirectory, "runtimes", "win-x64", "native", "Live2DCubismCore.dll");
    }

    [LibraryImport(LibraryName, EntryPoint = "csmGetVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetVersion();

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

    [LibraryImport(LibraryName, EntryPoint = "csmGetRenderOrders")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetRenderOrders(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableDrawOrders")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableDrawOrders(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int GetParameterCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterMinimumValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterMinimumValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterMaximumValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterMaximumValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetParameterDefaultValues")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetParameterDefaultValues(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int GetPartCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetPartIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartOpacities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetPartOpacities(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetPartParentPartIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetPartParentPartIndices(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableCount")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int GetDrawableCount(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableIds(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableTextureIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableTextureIndices(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableConstantFlags")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableConstantFlags(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableDynamicFlags")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableDynamicFlags(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableBlendModes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableBlendModes(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableOpacities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableOpacities(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableParentPartIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableParentPartIndices(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableMaskCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableMaskCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableMasks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableMasks(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableVertexCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexPositions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableVertexPositions(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableVertexUvs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableVertexUvs(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIndexCounts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableIndexCounts(nint model);

    [LibraryImport(LibraryName, EntryPoint = "csmGetDrawableIndices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint GetDrawableIndices(nint model);
}
