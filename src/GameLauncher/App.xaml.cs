using System.Windows;
using GameLauncher.Diagnostics;

namespace GameLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Прогон сервисов без окна: GameLauncher.exe --selftest <папка>
        if (e.Args.Contains("--selftest"))
        {
            // Через Task.Run намеренно: продолжения await иначе просились бы
            // обратно в диспетчер WPF, который мы тут же и заблокировали бы
            // ожиданием — классический взаимный клинч.
            var code = Task.Run(() => SelfTest.RunAsync(e.Args)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        new Views.MainWindow().Show();
    }
}
