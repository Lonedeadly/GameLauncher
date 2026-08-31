using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Infrastructure;
using GameLauncher.Services;

namespace GameLauncher.Views;

/// <summary>Выбор папки для игр при первом запуске.</summary>
public partial class FirstRunWindow : Window
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x6F, 0xC2, 0x76));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x6A));

    private readonly DispatcherTimer _debounce;

    /// <summary>Выбранный путь. null — пользователь отказался.</summary>
    public string? ChosenPath { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();

        // Проверка лезет на диск (в том числе пробует создать папку), поэтому
        // не на каждую нажатую букву, а спустя паузу после набора.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Validate(); };

        PathBox.Text = LibraryService.SuggestPath();
        WhyText.Text = ExplainSuggestion();
        Validate();

        Loaded += (_, _) => { PathBox.Focus(); PathBox.CaretIndex = PathBox.Text.Length; };
    }

    private static string ExplainSuggestion() =>
        LibraryService.IsBadHome(AppPaths.ExeDirectory)
            ? "Лаунчер лежит в системном месте или прямо на рабочем столе, поэтому " +
              "рядом с ним папку заводить не стоит — предложен профиль пользователя."
            : "Предложена папка рядом с лаунчером: так его можно унести на флешке вместе с играми.";

    private void PathBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
        OkButton.IsEnabled = false;
    }

    private void Validate()
    {
        var check = LibraryService.Check(PathBox.Text);

        VerdictText.Text = check.Message;
        VerdictText.Foreground = check.Verdict == LibraryVerdict.Ok ? Good : Bad;
        OkButton.IsEnabled = check.Verdict == LibraryVerdict.Ok;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog появился в WPF начиная с .NET 8 — сторонних
        // библиотек и ссылки на WinForms не требуется.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка для игр",
            Multiselect = false,
        };

        try
        {
            var start = Path.GetDirectoryName(PathBox.Text);
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                dialog.InitialDirectory = start;
        }
        catch (ArgumentException)
        {
            // Введена ерунда — просто откроем диалог там, где он сам решит.
        }

        if (dialog.ShowDialog(this) == true)
        {
            // Диалог отдаёт существующую папку. Если она чужая и непустая,
            // Validate это скажет — сами ничего не додумываем.
            PathBox.Text = dialog.FolderName;
            _debounce.Stop();
            Validate();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        Validate();
        if (!OkButton.IsEnabled) return;

        ChosenPath = Path.GetFullPath(PathBox.Text);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
