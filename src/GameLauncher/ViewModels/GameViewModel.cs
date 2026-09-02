using System.Windows.Input;
using GameLauncher.Model;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Одна игра в списке и её карточка справа.</summary>
public sealed class GameViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly ReleaseService _releases;
    private readonly InstallService _install;
    private readonly Func<string, bool> _confirm;

    private ReleaseLookup? _lookup;
    private GameStatus _status;

    public GameViewModel(
        CatalogEntry entry,
        LibraryService library,
        ReleaseService releases,
        InstallService install,
        Func<string, bool> confirm)
    {
        Entry = entry;
        _library = library;
        _releases = releases;
        _install = install;
        _confirm = confirm;

        _status = GameStatus.Compute(entry, library.GetInstalled(entry.Id), null);

        PrimaryCommand = new AsyncRelayCommand(PrimaryAsync, () => !IsBusy && (CanPlay || CanInstallOrUpdate));
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy && _status.CanUninstall);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy && _cancel is { IsCancellationRequested: false });
    }

    public CatalogEntry Entry { get; }

    public string Name => Entry.Name;
    public string Description => Entry.Description;

    /// <summary>Пока картинок нет — заглушка из первой буквы названия.</summary>
    public string ImagePlaceholder => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public ICommand PrimaryCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }

    // ── состояние, видное без нажатий ────────────────────────────────────

    public string StateCaption => _status.StateCaption;
    public string InstalledVersion => _status.InstalledVersion;
    public string AvailableVersion => _status.AvailableVersion;
    public string PrimaryAction => _status.PrimaryAction;
    public string? Note => _status.Note ?? Warning;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool CanPlay => _status.CanPlay;
    public bool CanInstallOrUpdate => _status.CanInstallOrUpdate;
    public bool CanUninstall => _status.CanUninstall;

    /// <summary>Когда сборок нет вовсе, кнопки быть не должно —
    /// не «Установить», которая ничего не сделает, а просто ничего.</summary>
    public bool ShowPrimary => CanPlay || CanInstallOrUpdate;

    public bool HasUpdate => _status.State == GameState.UpdateAvailable;
    public bool IsInstalled => _status.Installed is not null;

    public string? Warning { get; private set; }

    // ── «проверить обновление» ───────────────────────────────────────────

    private bool _isChecking;

    /// <summary>Что показать рядом с кнопкой.
    ///
    /// Время берётся из самого ответа, а не запоминается при нажатии: на
    /// повторное нажатие в те же полминуты ответит кэш, и написать «сейчас»
    /// значило бы соврать — данные остались прежними. Пусть лучше время не
    /// сдвинется: это и есть правда.</summary>
    public string CheckStatus =>
        _isChecking
            ? "Проверяю…"
            : _lookup is { Source: not ReleaseSource.None } lookup
                ? $"Проверено в {lookup.FetchedAt.ToLocalTime():HH:mm}"
                : "";

    public bool HasCheckStatus => CheckStatus.Length > 0;

    /// <summary>Когда данные о сборке были получены на самом деле.</summary>
    public DateTimeOffset? CheckedAt =>
        _lookup is { Source: not ReleaseSource.None } lookup ? lookup.FetchedAt : null;

    private async Task CheckAsync()
    {
        Error = null;
        _isChecking = true;
        RaiseAll(nameof(CheckStatus), nameof(HasCheckStatus));

        try
        {
            // force: человек нажал сам. Отвечать ему кэшем — значит врать
            // ровно там, где он спрашивает всерьёз.
            await LoadReleasesAsync(force: true);
        }
        finally
        {
            _isChecking = false;
            RaiseAll(nameof(CheckStatus), nameof(HasCheckStatus));
        }
    }

    // ── занятость и прогресс ─────────────────────────────────────────────

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy;

    public ProgressState Progress { get; } = new();

    /// <summary>Живёт ровно одну установку. Отмена — это его Cancel, и
    /// дальше всё делает сама установка: временное убирает, старую игру
    /// не трогает.</summary>
    private CancellationTokenSource? _cancel;

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ── загрузка сведений о релизах ──────────────────────────────────────

    public async Task LoadReleasesAsync(bool force, CancellationToken ct = default)
    {
        try
        {
            var lookup = await _releases.GetAsync(Entry.Id, force, ct);
            _lookup = lookup;
            Warning = lookup.Warning;
        }
        catch (Exception ex)
        {
            // Одна витрина не должна утаскивать за собой остальные игры.
            Warning = $"Не удалось получить сведения о сборках: {ex.Message}";
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        _status = GameStatus.Compute(Entry, _library.GetInstalled(Entry.Id), _lookup?.Build);

        RaiseAll(
            nameof(StateCaption), nameof(InstalledVersion), nameof(AvailableVersion),
            nameof(PrimaryAction), nameof(Note), nameof(HasNote), nameof(CanPlay), nameof(CanInstallOrUpdate),
            nameof(CanUninstall), nameof(ShowPrimary), nameof(HasUpdate), nameof(IsInstalled),
            nameof(CheckStatus), nameof(HasCheckStatus));
    }

    // ── действия ─────────────────────────────────────────────────────────

    private async Task PrimaryAsync()
    {
        Error = null;

        // Играть можно всегда, когда игра на диске: запуск не должен
        // зависеть от того, ответила ли раздача.
        if (!CanInstallOrUpdate)
        {
            Play();
            return;
        }

        await InstallAsync();
    }

    private void Play()
    {
        try
        {
            _install.Launch(Entry);
        }
        catch (InstallException ex)
        {
            Error = ex.Message;
            return;
        }

        // Проверка после запуска, а не до: игра уже открывается, и ждать
        // ответа сети было бы задержкой ровно там, где человек её меньше
        // всего готов терпеть. Ответ придёт через секунду и обновит
        // карточку — к следующему разу «Обновить» уже будет на месте.
        _ = LoadReleasesAsync(force: false);
    }

    private async Task InstallAsync()
    {
        var remote = _status.Remote;
        if (remote is null) return;

        IsBusy = true;
        Progress.Begin();
        _cancel = new CancellationTokenSource();

        var progress = new Progress<InstallProgress>(p => Progress.Report(p));

        try
        {
            await _install.InstallAsync(Entry, remote, progress, _cancel.Token);
        }
        catch (InstallException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Error = "Установка отменена.";
        }
        catch (Exception ex)
        {
            Error = $"Не удалось установить: {ex.Message}";
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsBusy = false;
            Progress.Clear();
            RefreshStatus();
        }
    }

    private Task UninstallAsync()
    {
        Error = null;

        if (!_confirm($"Удалить «{Name}»? Папка игры будет стёрта."))
            return Task.CompletedTask;

        try
        {
            _install.Uninstall(Entry);
        }
        catch (InstallException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            RefreshStatus();
        }

        return Task.CompletedTask;
    }
}
