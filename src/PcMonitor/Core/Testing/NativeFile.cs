using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMonitor.Core;

/// <summary>
/// Небуферизованный ввод-вывод через WinAPI (FILE_FLAG_NO_BUFFERING): нужен,
/// чтобы тесты диска измеряли реальный накопитель, а не кэш операционной системы.
/// </summary>
internal static class NativeFile
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenAlways = 4;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint FileBegin = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle handle, IntPtr buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle handle, IntPtr buffer, int bytesToWrite, out int bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle handle, long distance, out long newPosition, uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEndOfFile(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    /// <summary>Открывает файл без буферизации (создавая при необходимости).</summary>
    public static SafeFileHandle Open(string path, bool write, bool create)
    {
        uint access = write ? GenericWrite | GenericRead : GenericRead;
        uint disposition = create ? CreateAlways : OpenAlways;
        SafeFileHandle handle = CreateFileW(path, access, FileShareRead, IntPtr.Zero, disposition,
            FileAttributeNormal | FileFlagNoBuffering | FileFlagWriteThrough, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Не удалось открыть файл без буферизации: {Marshal.GetLastWin32Error()}");

        return handle;
    }

    /// <summary>Выделяет выровненную память под блок данных.</summary>
    public static IntPtr Allocate(int bytes) =>
        VirtualAlloc(IntPtr.Zero, (UIntPtr)bytes, MemCommit | MemReserve, PageReadWrite);

    public static void Free(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) VirtualFree(pointer, UIntPtr.Zero, MemRelease);
    }

    public static int Read(SafeFileHandle handle, IntPtr buffer, int count)
    {
        if (!ReadFile(handle, buffer, count, out int read, IntPtr.Zero))
            throw new IOException("Ошибка чтения: " + Marshal.GetLastWin32Error());

        return read;
    }

    public static int Write(SafeFileHandle handle, IntPtr buffer, int count)
    {
        if (!WriteFile(handle, buffer, count, out int written, IntPtr.Zero))
            throw new IOException("Ошибка записи: " + Marshal.GetLastWin32Error());

        return written;
    }

    public static void Seek(SafeFileHandle handle, long offset)
    {
        if (!SetFilePointerEx(handle, offset, out _, FileBegin))
            throw new IOException("Ошибка позиционирования: " + Marshal.GetLastWin32Error());
    }

    public static void Truncate(SafeFileHandle handle) => SetEndOfFile(handle);

    public static void Flush(SafeFileHandle handle) => FlushFileBuffers(handle);
}
