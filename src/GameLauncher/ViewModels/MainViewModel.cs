using System.Collections.ObjectModel;
using System.Windows.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly CatalogService _catalog;
    private readonly ReleaseService _releases;
    private readonly InstallService _install;
    private readonly Func<string, bool> _confirm;

    public MainViewModel(
        SettingsService settings,
        LibraryService library,
        CatalogService catalog,
        ReleaseService releases,
        InstallService install,
        Func<string, bool> confirm)
    {
        _settings = settings;
        _library = library;
        _catalog = catalog;
        _releases = releases;
        _install = install;
        _confirm = confirm;

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsLoading);
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
            await Task.WhenAll(Games.Select(g => g.LoadReleasesAsync(force)));

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

    private void UpdateRateLimit() =>
        RateLimit = _releases.RateLimitRemaining is { } left
            ? $"Запросов к GitHub осталось: {left}/60"
            : "";
}
