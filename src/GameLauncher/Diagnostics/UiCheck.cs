using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameLauncher.Diagnostics;

/// <summary>Поднимает окно и слушает диагностику привязок WPF:
/// <c>GameLauncher.exe --uicheck &lt;папка&gt;</c>.
///
/// Опечатка в имени свойства внутри XAML не ломает сборку и не роняет
/// приложение — она просто молча оставляет поле пустым. Ловится только так.</summary>
public static class UiCheck
{
    private sealed class Collector : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }
    }

    private static readonly Collector Sink = new();

    public static void Attach()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(Sink);
        source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
    }

    /// <summary>Снимок окна в PNG — чтобы можно было посмотреть на вёрстку,
    /// не запуская лаунчер руками.</summary>
    public static void Snapshot(Window window, string path)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Закрыть окно через паузу и отчитаться о найденном.</summary>
    public static void ScheduleReport(Window window, string workDir, TimeSpan after,
        ViewModels.MainViewModel? vm = null)
    {
        var events = new List<string>();
        if (vm is not null)
            vm.PropertyChanged += (_, e) => events.Add(
                $"{e.PropertyName}@{(System.Windows.Threading.Dispatcher.CurrentDispatcher == window.Dispatcher ? "ui" : "bg")}");

        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                Snapshot(window, Path.Combine(workDir, "main-window.png"));

                // По снимку на каждую игру: состояния видно только глазами.
                if (vm is not null)
                {
                    foreach (var game in vm.Games)
                    {
                        vm.Selected = game;
                        window.UpdateLayout();
                        Snapshot(window, Path.Combine(workDir, $"game-{game.Entry.Id}.png"));
                    }
                }

                ShotFirstRun(workDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Console.WriteLine($"снимок не сделан: {ex.Message}");
            }

            var report = new StringBuilder();
            report.AppendLine($"окно            {window.Title}, {window.ActualWidth:0}x{window.ActualHeight:0}");
            report.AppendLine($"проблем привязок: {Sink.Messages.Count}");

            if (window.FindName("CardPane") is FrameworkElement pane)
                report.AppendLine(
                    $"карточка        Visibility={pane.Visibility}, " +
                    $"{pane.ActualWidth:0}x{pane.ActualHeight:0}, DataContext={pane.DataContext?.GetType().Name ?? "null"}");

            report.AppendLine($"события VM      {string.Join(", ", events)}");

            if (vm is not null)
            {
                report.AppendLine($"сообщение       {vm.Notice ?? "—"}");
                if (vm.Notice?.StartsWith("Сбой") == true) Sink.Messages.Add($"СБОЙ ЗАГРУЗКИ: {vm.Notice}");
                report.AppendLine($"версия          {vm.LauncherVersion}");
                report.AppendLine($"полоса обновл.  {(vm.ShowLauncherBar ? "видна" : "скрыта")} | " +
                                  $"кнопка {(vm.HasLauncherUpdate ? "есть" : "нет")} | {vm.LauncherHeadline}");
                report.AppendLine($"игр в списке    {vm.Games.Count}");
                report.AppendLine($"IsLoading       {vm.IsLoading}");
                report.AppendLine($"IsEmpty         {vm.IsEmpty}");
                report.AppendLine($"HasSelection    {vm.HasSelection}");
                report.AppendLine($"Selected        {vm.Selected?.Name ?? "—"}");
                foreach (var g in vm.Games)
                    report.AppendLine($"  {g.Name}: {g.StateCaption} | уст. {g.InstalledVersion} | " +
                                      $"дост. {g.AvailableVersion} | кнопка {(g.ShowPrimary ? g.PrimaryAction : "нет")}");
            }
            foreach (var m in Sink.Messages) report.AppendLine($"  {m}");

            var text = report.ToString();
            Console.WriteLine(text);

            try
            {
                File.WriteAllText(Path.Combine(workDir, "uicheck.log"), text, new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // Отчёт всё равно ушёл в консоль.
            }

            Environment.Exit(Sink.Messages.Count == 0 ? 0 : 1);
        };
        timer.Start();
    }

    /// <summary>Окно первого запуска показываем отдельно и невидимо для
    /// пользователя — нужен только его снимок.</summary>
    private static void ShotFirstRun(string workDir)
    {
        var window = new Views.FirstRunWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };

        window.Show();
        window.UpdateLayout();
        Snapshot(window, Path.Combine(workDir, "first-run.png"));
        window.Close();
    }
}
