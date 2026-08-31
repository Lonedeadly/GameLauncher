using System.Windows;
using GameLauncher.Infrastructure;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Версия в заголовке: при разборе «у меня не работает» это первый
        // вопрос, и друг должен уметь ответить не открывая свойства файла.
        Title = AppVersion.IsDevBuild
            ? "GameLauncher (сборка из исходников)"
            : $"GameLauncher {AppVersion.Display}";
    }
}
