using System.Runtime.InteropServices;
using System.Text;

using Vortice.Direct3D;

namespace DesktopPet.Rendering.D3D11;

internal static partial class D3D11ShaderCompiler
{
    public static Blob Compile(string source, string entryPoint, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var sourceBytes = Encoding.UTF8.GetBytes(source);
        int result;
        nint shaderBlob;
        nint errorBlob;
        try
        {
            result = D3DCompile(
                sourceBytes,
                (nuint)sourceBytes.Length,
                null,
                0,
                0,
                entryPoint,
                target,
                0,
                0,
                out shaderBlob,
                out errorBlob);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("d3dcompiler_47.dll is missing. Install the DirectX shader compiler runtime or place d3dcompiler_47.dll next to DesktopPet.App.exe.", ex);
        }

        using var error = errorBlob == 0 ? null : new Blob(errorBlob);
        if (result < 0)
        {
            var message = error is null
                ? $"D3DCompile failed: 0x{result:X8}"
                : Marshal.PtrToStringAnsi(error.BufferPointer) ?? $"D3DCompile failed: 0x{result:X8}";
            throw new InvalidOperationException(message);
        }

        return new Blob(shaderBlob);
    }

    [DllImport("d3dcompiler_47", EntryPoint = "D3DCompile", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(
        byte[] sourceData,
        nuint sourceDataSize,
        string? sourceName,
        nint defines,
        nint include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errorMessages);
}
