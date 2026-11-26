using System.Runtime.InteropServices;

namespace PcMonitor.Core.Gpu;

/// <summary>
/// Компиляция HLSL в рантайме через d3dcompiler_47.dll.
/// Своя обёртка нужна, чтобы не зависеть от версии внешней библиотеки-обёртки.
/// </summary>
internal static class ShaderCompiler
{
    private const uint D3DCompileEnableStrictness = 1 << 11;   // D3DCOMPILE_ENABLE_STRICTNESS
    private const uint D3DCompileOptimizationLevel3 = 1 << 15; // D3DCOMPILE_OPTIMIZATION_LEVEL3

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int D3DCompile(
        byte[] srcData,
        nuint srcDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entrypoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DDisassemble", SetLastError = true)]
    private static extern int D3DDisassemble(IntPtr data, nuint size, uint flags, string? comments, out IntPtr disassembly);

    /// <summary>Компилирует шейдер и возвращает байт-код. При ошибке бросает исключение с текстом компилятора.</summary>
    public static byte[] Compile(string source, string entryPoint, string profile)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(source);
        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;

        int hr = D3DCompile(bytes, (nuint)bytes.Length, "PcMonitor.hlsl", IntPtr.Zero, IntPtr.Zero,
            entryPoint, profile, D3DCompileEnableStrictness | D3DCompileOptimizationLevel3, 0, out code, out errors);

        try
        {
            if (hr != 0)
            {
                string message = errors != IntPtr.Zero ? ReadBlobString(errors) : $"HRESULT 0x{hr:X8}";
                throw new InvalidOperationException(
                    $"Не удалось скомпилировать шейдер «{entryPoint}» ({profile}):\n{message}");
            }

            if (code == IntPtr.Zero)
                throw new InvalidOperationException($"Компилятор вернул пустой байт-код для «{entryPoint}».");

            return ReadBlob(code);
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }

    private static byte[] ReadBlob(IntPtr blob)
    {
        // ID3DBlob: vtable = QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize
        IntPtr vtable = Marshal.ReadIntPtr(blob);
        IntPtr getBufferPointer = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        IntPtr getBufferSize = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);

        var pointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(getBufferPointer);
        var size = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(getBufferSize);

        IntPtr data = pointer(blob);
        nuint length = size(blob);

        var result = new byte[(int)length];
        Marshal.Copy(data, result, 0, result.Length);
        return result;
    }

    private static string ReadBlobString(IntPtr blob) =>
        System.Text.Encoding.ASCII.GetString(ReadBlob(blob)).TrimEnd('\0');

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr thisPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBufferSizeDelegate(IntPtr thisPointer);
}
