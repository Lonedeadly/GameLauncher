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
    }

    public CatalogEntry Entry { get; }

    public string Name => Entry.Name;
    public string Description => Entry.Description;

    /// <summary>Пока картинок нет — заглушка из первой буквы названия.</summary>
    public string ImagePlaceholder => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public ICommand PrimaryCommand { get; }
    public ICommand UninstallCommand { get; }

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

    // ── занятость и прогресс ─────────────────────────────────────────────

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy;

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

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
            nameof(CanUninstall), nameof(ShowPrimary), nameof(HasUpdate), nameof(IsInstalled));
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
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressText = "Подготовка…";

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressText = p.Describe();
            if (p.Fraction is { } f)
            {
                ProgressIndeterminate = false;
                ProgressValue = f * 100;
            }
            else
            {
                ProgressIndeterminate = true;
            }
        });

        try
        {
            await _install.InstallAsync(Entry, remote, progress);
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
            IsBusy = false;
            ProgressText = "";
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
