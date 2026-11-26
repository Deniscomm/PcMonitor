using System.IO;
using System.Runtime.InteropServices;

namespace PcMonitor.Core;

/// <summary>
/// Подключает консоль родительского процесса: приложение собрано как WinExe,
/// но режим «--dump» должен печатать отчёт в терминал.
/// </summary>
internal static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>Возвращает true, если удалось получить консоль для вывода.</summary>
    public static bool Attach()
    {
        try
        {
            // Вывод уже перенаправлен в файл или конвейер — используем его как есть.
            if (Console.IsOutputRedirected)
            {
                try
                {
                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                }
                catch
                {
                    // Кодировку может не удаться изменить — это не критично.
                }

                return true;
            }

            if (GetConsoleWindow() != IntPtr.Zero) return true;

            if (!AttachConsole(AttachParentProcess))
                AllocConsole();

            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // Кодировку консоли может не удаться изменить — это не критично.
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
