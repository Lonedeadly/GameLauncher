using System.Windows;
using GameLauncher.Diagnostics;
using GameLauncher.Services;
using GameLauncher.ViewModels;

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

        // Проверка привязок: поднять окно, послушать WPF, выйти.
        var uiCheck = e.Args.Contains("--uicheck");
        if (uiCheck) UiCheck.Attach();

        // Пока идёт выбор папки, у приложения нет главного окна. Без этого
        // закрытие диалога первого запуска считалось бы закрытием последнего
        // окна, и лаунчер завершился бы, не начавшись.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = new SettingsService();
        settings.Load();

        var libraryPath = uiCheck
            ? e.Args[Array.IndexOf(e.Args, "--uicheck") + 1]
            : ResolveLibraryPath(settings);
        if (libraryPath is null)
        {
            Shutdown();
            return;
        }

        var library = new LibraryService();
        try
        {
            library.Open(libraryPath);
            library.CleanTemp();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Не удалось открыть папку с играми:\n\n{ex.Message}",
                "GameLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var viewModel = new MainViewModel(
            settings,
            library,
            new CatalogService(library),
            new ReleaseService(library),
            new InstallService(library),
            Confirm);

        var window = new Views.MainWindow(viewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        if (uiCheck) UiCheck.ScheduleReport(window, libraryPath, TimeSpan.FromSeconds(8), viewModel);

        // Список и версии подтягиваются уже при видимом окне: ждать сеть
        // с пустым экраном — худшее, что можно сделать на старте.
        _ = viewModel.LoadAsync();
    }

    /// <summary>Путь из конфига, а если его нет или он больше не годится —
    /// окно первого запуска.</summary>
    private static string? ResolveLibraryPath(SettingsService settings)
    {
        var saved = settings.Settings.LibraryPath;
        if (saved is not null && LibraryService.Check(saved).Verdict == LibraryVerdict.Ok)
            return saved;

        var dialog = new Views.FirstRunWindow();
        if (dialog.ShowDialog() != true || dialog.ChosenPath is null)
            return null;

        settings.Settings.LibraryPath = dialog.ChosenPath;
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Папка выбрана, но настройки записать не удалось — при следующем запуске " +
                $"выбор придётся повторить.\n\n{ex.Message}",
                "GameLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return dialog.ChosenPath;
    }

    private static bool Confirm(string question) =>
        MessageBox.Show(question, "GameLauncher", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
}
