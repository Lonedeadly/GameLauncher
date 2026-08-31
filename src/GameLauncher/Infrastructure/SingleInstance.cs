using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GameLauncher.Infrastructure;

/// <summary>Одна копия лаунчера на один exe.
///
/// Второй запуск не открывает второе окно, а разворачивает уже открытое.
/// Две копии писали бы в одну папку с играми и в один state.json, и запись
/// об установке из одной затиралась бы другой.
///
/// Ключ — путь к exe: две разные копии лаунчера в разных папках живут своей
/// жизнью, а вот один и тот же файл дважды не запускается.</summary>
public static class SingleInstance
{
    private const int SW_RESTORE = 9;

    /// <summary>Держится до выхода из процесса — тем и работает.</summary>
    private static Mutex? _held;

    /// <returns>true, если мы первые. false — копия уже работает.</returns>
    public static bool TryAcquire()
    {
        // Local\ — в пределах сеанса пользователя. Global\ дал бы одну копию
        // на всю машину, а разные пользователи друг другу не мешают.
        var name = @"Local\GameLauncher-" + KeyOf(Environment.ProcessPath ?? "");

        try
        {
            _held = new Mutex(initiallyOwned: false, name);
            return _held.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Прошлая копия умерла, не отпустив мьютекс. Значит, он наш.
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Проверить не вышло. Запретить запуск из-за этого было бы
            // хуже, чем допустить вторую копию.
            return true;
        }
    }

    /// <summary>Найти окно уже работающей копии и вывести его вперёд.</summary>
    /// <param name="timeout">Первая копия могла ещё не успеть показать окно —
    /// например, стоит на выборе папки. Ждём её недолго.</param>
    public static bool TryActivateExisting(TimeSpan timeout)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return false;

        var deadline = DateTime.UtcNow + timeout;

        do
        {
            foreach (var window in WindowsOfSiblings(exePath))
            {
                // Свёрнутое окно сначала восстанавливаем: иначе оно получит
                // фокус, оставшись значком в панели задач.
                if (IsIconic(window)) ShowWindow(window, SW_RESTORE);

                // Право вывести чужое окно вперёд есть именно у нас: этот
                // процесс только что запустил сам пользователь.
                if (SetForegroundWindow(window)) return true;
            }

            Thread.Sleep(150);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static List<IntPtr> WindowsOfSiblings(string exePath)
    {
        var result = new List<IntPtr>();
        var me = Environment.ProcessId;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (process)
            {
                if (process.Id == me) continue;

                // Сверяем именно путь: чужую программу с похожим именем
                // трогать нельзя.
                if (!IsSameFile(process, exePath)) continue;

                var window = process.MainWindowHandle;
                if (window != IntPtr.Zero) result.Add(window);
            }
        }

        return result;
    }

    private static bool IsSameFile(Process process, string exePath)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, exePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // Чужой процесс мог уже завершиться или быть недоступен.
            return false;
        }
    }

    /// <summary>Короткий устойчивый ключ из пути: имя мьютекса ограничено
    /// по длине, а путь бывает каким угодно.</summary>
    private static string KeyOf(string path)
    {
        var normalized = path.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
