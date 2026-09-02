using System.Windows;
using GameLauncher.Infrastructure;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class MainWindow : Window
{
    // Значки из шрифта Segoe: «развернуть» и «вернуть прежний размер».
    private const string GlyphMaximize = "\uE922";
    private const string GlyphRestore = "\uE923";

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Заголовок нужен и со своей шапкой: его показывают панель задач и
        // переключатель окон, а рисовать их мы не можем.
        Title = AppVersion.IsDevBuild
            ? "GameLauncher (сборка из исходников)"
            : $"GameLauncher {AppVersion.Display}";

        SyncWindowState();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object sender, EventArgs e) => SyncWindowState();

    /// <summary>Приводит окно в порядок после смены состояния.
    ///
    /// Развернув окно, Windows делает его больше экрана — на невидимую рамку,
    /// за которую тянут края. У окна с системным заголовком это не видно: та
    /// же рамка съедается при расчёте клиентской области. У окна со своей
    /// шапкой клиентская область — всё окно целиком, и за край уезжают
    /// кнопки, «Закрыть» в том числе. Возвращаем отступом.
    ///
    /// Величина — сумма двух метрик: рамки захвата и добавочной каймы.
    /// По отдельности каждая даёт 4 точки, а окно вылезает на 8, и обе
    /// поодиночке проверку заваливали. Пробовался и разговор с Windows
    /// напрямую (WM_GETMINMAXINFO): сообщение приходит, ответ принимается,
    /// но WPF в паре с WindowChrome ставит своё поверх — окно оставалось
    /// прежнего размера.
    ///
    /// Формула стандартная, но всё же формула, поэтому за ней присматривает
    /// --uicheck: он разворачивает окно и сверяет содержимое с рабочей
    /// областью экрана. Разойдётся на другой машине — скажет вслух.</summary>
    private void SyncWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;

        var frame = SystemParameters.WindowResizeBorderThickness.Left
                    + SystemParameters.WindowNonClientFrameThickness.Left;

        Root.Margin = maximized ? new Thickness(frame) : default;

        MaxButton.Content = maximized ? GlyphRestore : GlyphMaximize;
        MaxButton.ToolTip = maximized ? "Свернуть в окно" : "Развернуть";
    }
}
