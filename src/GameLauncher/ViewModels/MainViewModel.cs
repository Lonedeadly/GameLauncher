using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using GameLauncher.Infrastructure;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    /// <summary>Как часто лаунчер спрашивает раздачу сам, без просьбы.
    ///
    /// Пять минут — не про нагрузку: четыре файла по полкилобайта серверу
    /// безразличны. Это про то, через сколько человек, оставивший окно
    /// открытым, узнаёт о новой сборке, не нажимая ничего.</summary>
    public static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(5);

    private readonly LibraryService _library;
    private readonly CatalogService _catalog;
    private readonly ReleaseService _releases;
    private readonly InstallService _install;
    private readonly SelfUpdateService _selfUpdate;
    private readonly Func<string, bool> _confirm;
    private readonly Action _quit;

    /// <summary>Живёт столько же, сколько окно. Поле, а не локальная
    /// переменная: без ссылки таймер соберёт сборщик мусора, и тиков просто
    /// не будет — молча.</summary>
    private readonly DispatcherTimer _timer;

    public MainViewModel(
        LibraryService library,
        CatalogService catalog,
        ReleaseService releases,
        InstallService install,
        SelfUpdateService selfUpdate,
        Func<string, bool> confirm,
        Action quit)
    {
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
        CancelLauncherCommand = new RelayCommand(
            () => _launcherCancel?.Cancel(), () => LauncherBusy && _launcherCancel is { IsCancellationRequested: false });
        Games.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));

        _timer = new DispatcherTimer { Interval = AutoInterval };
        _timer.Tick += (_, _) => _ = AutoCheckAsync();
        _timer.Start();
    }

    /// <summary>Тик таймера. Молча: полоса загрузки не мигает, выбор не
    /// сбивается, а если ответа нет — в карточке просто останется прежнее.
    ///
    /// Во время установки не лезем. Дело не в сети: подмена статуса под
    /// работающей полосой прогресса — это мигание кнопок ровно там, где
    /// человек смотрит на них не отрываясь.</summary>
    private async Task AutoCheckAsync()
    {
        if (IsLoading || LauncherBusy || Games.Any(g => g.IsBusy)) return;

        await LoadAsync(force: false, quiet: true);
    }

    /// <summary>Открыли вкладку игры — спросили про неё. Нижний предел в
    /// <see cref="ReleaseService.MinInterval"/> не даёт щёлканью по списку
    /// превратиться в поток запросов.</summary>
    private async Task CheckSelectedAsync(GameViewModel game)
    {
        await game.LoadReleasesAsync(force: false);
        if (ReferenceEquals(_selected, game)) UpdateChecked();
    }

    public ObservableCollection<GameViewModel> Games { get; } = [];

    public ICommand RefreshCommand { get; }

    private GameViewModel? _selected;
    public GameViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;

            Raise(nameof(HasSelection));
            if (value is not null) _ = CheckSelectedAsync(value);
        }
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

    private string _checked = "";
    /// <summary>«проверено в 14:32». Занимает строку внизу не ради красоты:
    /// без неё нельзя отличить «обновлений нет» от «спросить не вышло, и я
    /// показываю позавчерашнее».
    ///
    /// Время — самого свежего ответа среди игр, а не момент нажатия: если
    /// ответил кэш, данные старые, и писать «сейчас» значило бы врать. То же
    /// правило, что и у подписи в карточке.</summary>
    public string Checked { get => _checked; private set => Set(ref _checked, value); }

    /// <param name="quiet">Проверка по таймеру: без полосы загрузки. Она
    /// оправданна, когда человек нажал кнопку, и раздражает, когда он ничего
    /// не нажимал.</param>
    public async Task LoadAsync(bool force = false, bool quiet = false)
    {
        if (!quiet) IsLoading = true;
        try
        {
            var result = await _catalog.LoadAsync();

            var previouslySelected = Selected?.Entry.Id;
            SyncGames(result.Catalog.Games);

            Notice = BuildNotice(result);

            // Игры опрашиваем параллельно: их немного, а ждать их подряд
            // означало бы ждать сумму таймаутов, если раздача недоступна.
            // Сам лаунчер — там же: такой же файл на той же раздаче.
            await Task.WhenAll(
                Games.Select(g => g.LoadReleasesAsync(force))
                     .Append(CheckLauncherAsync(force)));

            Selected = Games.FirstOrDefault(g => g.Entry.Id == previouslySelected) ?? Games.FirstOrDefault();
            UpdateChecked();
        }
        catch (Exception ex)
        {
            // Молча проглоченный сбой оставлял бы пустое окно без объяснения —
            // худший вид поломки, потому что выглядит как «просто не работает».
            Notice = $"Сбой при загрузке списка: {ex.Message}";
        }
        finally
        {
            if (!quiet) IsLoading = false;
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
            Games.Add(new GameViewModel(entry, _library, _releases, _install, _confirm));
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
    public ICommand CancelLauncherCommand { get; }

    public ProgressState LauncherProgress { get; } = new();

    private CancellationTokenSource? _launcherCancel;

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
        LauncherProgress.Begin();
        _launcherCancel = new CancellationTokenSource();

        var progress = new Progress<InstallProgress>(p => LauncherProgress.Report(
            p, p.Phase == InstallPhase.Swapping ? "Замена файла и перезапуск…" : null));

        try
        {
            await _selfUpdate.ApplyAsync(build, progress, _launcherCancel.Token);

            // Дальше работает уже новый процесс, и он ждёт нашего выхода,
            // чтобы переписать файл. Задерживаться здесь нельзя.
            _quit();
        }
        catch (InstallException ex)
        {
            LauncherError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Отменил человек. Полоса просто исчезает: об отмене по своей
            // же просьбе сообщать не о чем, а кнопка вернётся на место сама.
        }
        catch (Exception ex)
        {
            LauncherError = $"Обновить лаунчер не удалось: {ex.Message}";
        }
        finally
        {
            _launcherCancel.Dispose();
            _launcherCancel = null;
            LauncherBusy = false;
            LauncherProgress.Clear();
        }
    }

    private void UpdateChecked()
    {
        var latest = Games
            .Select(g => g.CheckedAt)
            .Where(t => t is not null)
            .Max();

        Checked = latest is { } at ? $"проверено в {at.ToLocalTime():HH:mm}" : "";
    }
}
