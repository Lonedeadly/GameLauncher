using System.Collections.ObjectModel;
using System.Windows.Input;
using GameLauncher.Infrastructure;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly CatalogService _catalog;
    private readonly ReleaseService _releases;
    private readonly InstallService _install;
    private readonly SelfUpdateService _selfUpdate;
    private readonly Func<string, bool> _confirm;
    private readonly Action _quit;

    public MainViewModel(
        SettingsService settings,
        LibraryService library,
        CatalogService catalog,
        ReleaseService releases,
        InstallService install,
        SelfUpdateService selfUpdate,
        Func<string, bool> confirm,
        Action quit)
    {
        _settings = settings;
        _library = library;
        _catalog = catalog;
        _releases = releases;
        _install = install;
        _selfUpdate = selfUpdate;
        _confirm = confirm;
        _quit = quit;

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsLoading);
        UpdateLauncherCommand = new AsyncRelayCommand(
            UpdateLauncherAsync, () => !LauncherBusy && _launcher.Build is not null);
        Games.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));
    }

    public ObservableCollection<GameViewModel> Games { get; } = [];

    public ICommand RefreshCommand { get; }

    private GameViewModel? _selected;
    public GameViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Raise(nameof(HasSelection)); }
    }

    public bool HasSelection => _selected is not null;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (Set(ref _isLoading, value)) Raise(nameof(IsEmpty)); }
    }

    /// <summary>Ни одной игры и загрузка окончена — окно не должно быть
    /// просто пустым, надо сказать почему.</summary>
    public bool IsEmpty => !_isLoading && Games.Count == 0;

    private string? _notice;
    /// <summary>Полоска сверху: откуда взят список, что пошло не так.</summary>
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string LibraryPath => _library.Root;

    private string _rateLimit = "";
    public string RateLimit { get => _rateLimit; private set => Set(ref _rateLimit, value); }

    public async Task LoadAsync(bool force = false)
    {
        IsLoading = true;
        try
        {
            var result = await _catalog.LoadAsync();

            var previouslySelected = Selected?.Entry.Id;
            SyncGames(result.Catalog.Games);

            Notice = BuildNotice(result);

            // Витрины опрашиваем параллельно: их немного, а ждать их подряд
            // означало бы ждать сумму таймаутов, если GitHub недоступен.
            // Свой репозиторий — там же: он такая же витрина, просто наша.
            await Task.WhenAll(
                Games.Select(g => g.LoadReleasesAsync(force))
                     .Append(CheckLauncherAsync(force)));

            Selected = Games.FirstOrDefault(g => g.Entry.Id == previouslySelected) ?? Games.FirstOrDefault();
            UpdateRateLimit();
        }
        catch (Exception ex)
        {
            // Молча проглоченный сбой оставлял бы пустое окно без объяснения —
            // худший вид поломки, потому что выглядит как «просто не работает».
            Notice = $"Сбой при загрузке списка: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Обновляем коллекцию на месте, а не пересоздаём: иначе после
    /// каждого обновления слетал бы выбор и терялся прогресс установки.</summary>
    private void SyncGames(List<Model.CatalogEntry> entries)
    {
        for (var i = Games.Count - 1; i >= 0; i--)
            if (entries.All(e => e.Id != Games[i].Entry.Id))
                Games.RemoveAt(i);

        foreach (var entry in entries)
        {
            if (Games.Any(g => g.Entry.Id == entry.Id)) continue;
            Games.Add(new GameViewModel(entry, _settings, _library, _releases, _install, _confirm));
        }
    }

    /// <summary>Сообщение от сервиса уже самодостаточно — своё сверху не
    /// нагромождаем, добавляем только возраст копии.</summary>
    private static string? BuildNotice(CatalogResult result)
    {
        if (result.Warning is null) return null;

        return result.Source == CatalogSource.Cache && result.CachedAt is { } at
            ? $"{result.Warning} Копия от {at.ToLocalTime():dd.MM HH:mm}."
            : result.Warning;
    }

    // ── обновление самого лаунчера ───────────────────────────────────────

    private SelfUpdateInfo _launcher =
        new(SelfUpdateState.Unknown, AppVersion.Display, null, null, null);

    public ICommand UpdateLauncherCommand { get; }

    /// <summary>Есть что ставить. Во время установки кнопка убирается —
    /// нажать второй раз на «обновить и перезапустить» нечему.</summary>
    public bool HasLauncherUpdate => _launcher.State == SelfUpdateState.Available && !LauncherBusy;

    /// <summary>Полоса видна, только когда есть что сказать: «у вас всё
    /// свежее» — не новость, ради которой стоит занимать место.</summary>
    public bool ShowLauncherBar => HasLauncherUpdate || LauncherBusy || HasLauncherError;

    public string LauncherHeadline => _launcher.State == SelfUpdateState.Available
        ? $"Доступна новая версия лаунчера: {_launcher.Latest} (у вас {_launcher.Current})"
        : "Обновление лаунчера";

    private bool _launcherBusy;
    public bool LauncherBusy
    {
        get => _launcherBusy;
        private set
        {
            if (Set(ref _launcherBusy, value))
                RaiseAll(nameof(HasLauncherUpdate), nameof(ShowLauncherBar));
        }
    }

    private double _launcherProgressValue;
    public double LauncherProgressValue
    {
        get => _launcherProgressValue;
        private set => Set(ref _launcherProgressValue, value);
    }

    private bool _launcherProgressIndeterminate;
    public bool LauncherProgressIndeterminate
    {
        get => _launcherProgressIndeterminate;
        private set => Set(ref _launcherProgressIndeterminate, value);
    }

    private string _launcherProgressText = "";
    public string LauncherProgressText
    {
        get => _launcherProgressText;
        private set => Set(ref _launcherProgressText, value);
    }

    private string? _launcherError;
    public string? LauncherError
    {
        get => _launcherError;
        private set
        {
            if (Set(ref _launcherError, value))
                RaiseAll(nameof(HasLauncherError), nameof(ShowLauncherBar));
        }
    }

    public bool HasLauncherError => !string.IsNullOrEmpty(_launcherError);

    /// <summary>Своя версия — то, что первым спросят при разборе жалобы.</summary>
    public string LauncherVersion => AppVersion.IsDevBuild
        ? "сборка из исходников"
        : $"версия {AppVersion.Display}";

    private async Task CheckLauncherAsync(bool force)
    {
        _launcher = await _selfUpdate.CheckAsync(force);
        RaiseAll(nameof(HasLauncherUpdate), nameof(ShowLauncherBar), nameof(LauncherHeadline));

        // Полоса создаётся один раз, когда обновления ещё не нашли, и её
        // кнопка так и осталась бы серой: доступность команды сама собой
        // не пересчитывается, WPF делает это только по действиям мышью или
        // клавиатурой. У кнопок игр этой беды нет — их карточка целиком
        // пересоздаётся при выборе игры.
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task UpdateLauncherAsync()
    {
        var build = _launcher.Build;
        if (build is null) return;

        LauncherError = null;
        LauncherBusy = true;
        LauncherProgressIndeterminate = true;
        LauncherProgressValue = 0;
        LauncherProgressText = "Подготовка…";

        var progress = new Progress<InstallProgress>(p =>
        {
            LauncherProgressText = p.Phase == InstallPhase.Swapping
                ? "Замена файла и перезапуск…"
                : p.Describe();

            if (p.Fraction is { } fraction)
            {
                LauncherProgressIndeterminate = false;
                LauncherProgressValue = fraction * 100;
            }
            else
            {
                LauncherProgressIndeterminate = true;
            }
        });

        try
        {
            await _selfUpdate.ApplyAsync(build, progress);

            // Дальше работает уже новый процесс, и он ждёт нашего выхода,
            // чтобы переписать файл. Задерживаться здесь нельзя.
            _quit();
        }
        catch (InstallException ex)
        {
            LauncherError = ex.Message;
        }
        catch (Exception ex)
        {
            LauncherError = $"Обновить лаунчер не удалось: {ex.Message}";
        }
        finally
        {
            LauncherBusy = false;
            LauncherProgressText = "";
        }
    }

    private void UpdateRateLimit() =>
        RateLimit = _releases.RateLimitRemaining is { } left
            ? $"Запросов к GitHub осталось: {left}/60"
            : "";
}
