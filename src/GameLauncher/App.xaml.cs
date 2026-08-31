using System.Windows;
using GameLauncher.Diagnostics;
using GameLauncher.Infrastructure;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Вторая половина самообновления. Идёт самой первой: этот процесс —
        // временный, окна у него нет и быть не должно.
        if (SelfUpdateService.IsReplaceRun(e.Args))
        {
            var failure = SelfUpdateService.RunReplace(e.Args);
            if (failure is not null)
                MessageBox.Show(
                    "Обновление не завершилось:" + Environment.NewLine + Environment.NewLine +
                    failure + Environment.NewLine + Environment.NewLine +
                    "Прежняя версия осталась на месте.",
                    "GameLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);

            Environment.Exit(failure is null ? 0 : 1);
            return;
        }

        // «.new» и «.old» от прошлого обновления: удалить их в тот момент
        // было нельзя — один из двух ещё выполнялся.
        SelfUpdateService.CleanLeftovers();

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

        // Вторая копия не нужна: обе писали бы в одну папку с играми и
        // в один state.json, и запись об установке из одной затиралась бы
        // другой. Разворачиваем уже открытое окно и уходим.
        //
        // Проверку привязок это не касается: она поднимает окно нарочно,
        // и наткнуться на живой лаунчер значило бы её сорвать.
        if (!uiCheck && !SingleInstance.TryAcquire())
        {
            SingleInstance.TryActivateExisting(TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        // Пока идёт выбор папки, у приложения нет главного окна. Без этого
        // закрытие диалога первого запуска считалось бы закрытием последнего
        // окна, и лаунчер завершился бы, не начавшись.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = new SettingsService();
        settings.Load();

        // FirstOrDefault, а не индекс: «--uicheck» без пути — это опечатка
        // в командной строке, а не повод падать с IndexOutOfRange.
        var libraryPath = uiCheck
            ? e.Args.SkipWhile(a => a != "--uicheck").Skip(1).FirstOrDefault()
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

        var releases = new ReleaseService(library);

        var viewModel = new MainViewModel(
            settings,
            library,
            new CatalogService(library),
            releases,
            new InstallService(library),
            new SelfUpdateService(library, releases),
            Confirm,
            Shutdown);

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
