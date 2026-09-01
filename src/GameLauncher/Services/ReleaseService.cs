using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum ReleaseSource { Network, Cache, None }

/// <summary>Что раздача говорит про одну вещь — игру или сам лаунчер.
/// <paramref name="Build"/> = null и Source = Network означает честное
/// «сборок ещё нет», а не поломку.</summary>
public sealed record ReleaseLookup(
    RemoteBuild? Build,
    ReleaseSource Source,
    DateTimeOffset FetchedAt,
    string? Warning);

/// <summary>Читает meta/&lt;id&gt;.json с раздачи.
///
/// Раньше здесь жил клиент GitHub API со всей вознёй вокруг каналов, тегов,
/// черновиков и лимита в 60 запросов в час. Ничего этого больше нет: один
/// файл на одну вещь, и он либо есть, либо нет.
///
/// Кэш остался, но не ради лимита, а ради связи: без сети лаунчер должен
/// показывать последнее, что знал, а не пустой список.</summary>
public sealed class ReleaseService
{
    /// <summary>Нижний предел для автоматических проверок. Не ради сервера —
    /// четыре запроса по полкилобайта ему безразличны, — а чтобы дёрганый
    /// alt-tab не превращался в поток запросов.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private readonly LibraryService _library;
    private readonly Dictionary<string, Cached> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ReleaseService(LibraryService library) => _library = library;

    /// <param name="force">Нажатие кнопки человеком. Обходит нижний предел:
    /// на осознанное «проверить» отвечать кэшем — значит врать.</param>
    public async Task<ReleaseLookup> GetAsync(string id, bool force = false, CancellationToken ct = default)
    {
        var cached = ReadCache(id);
        var age = cached is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.FetchedAt;

        if (cached is not null && !force && age < MinInterval)
            return new ReleaseLookup(cached.Build, ReleaseSource.Cache, cached.FetchedAt, null);

        try
        {
            var build = await FetchAsync(id, ct);
            var fresh = new Cached { FetchedAt = DateTimeOffset.UtcNow, Build = build };
            WriteCache(id, fresh);
            return new ReleaseLookup(build, ReleaseSource.Network, fresh.FetchedAt, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            var reason = Describe(ex);
            return cached is not null
                ? new ReleaseLookup(cached.Build, ReleaseSource.Cache, cached.FetchedAt,
                    $"{reason} Показано последнее известное.")
                : new ReleaseLookup(null, ReleaseSource.None, DateTimeOffset.MinValue, reason);
        }
    }

    // ── сеть ─────────────────────────────────────────────────────────────

    private static async Task<RemoteBuild?> FetchAsync(string id, CancellationToken ct)
    {
        using var deadline = Http.Deadline(TimeSpan.FromSeconds(20), ct);

        using var response = await Origin.GetAsync($"meta/{id}.json", deadline.Token);

        // Файла нет — значит эту вещь ещё ни разу не собирали. Это ответ,
        // а не ошибка: игра должна остаться в списке с честным «сборок нет».
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(deadline.Token);
        var meta = JsonSerializer.Deserialize<Meta>(json, Json.Local);

        if (meta is null
            || string.IsNullOrWhiteSpace(meta.File)
            || string.IsNullOrWhiteSpace(meta.Sha256)
            || meta.Size <= 0)
            throw new JsonException($"meta/{id}.json без обязательных полей.");

        return new RemoteBuild(
            Version: meta.Version ?? "",
            Commit: meta.Commit ?? "",
            RelativePath: meta.File,
            Size: meta.Size,
            Sha256: meta.Sha256,
            Published: meta.Published);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => $"{Origin.Host} не ответил вовремя.",
        HttpRequestException => $"Нет связи с {Origin.Host}.",
        JsonException => "Раздача вернула неожиданный ответ.",
        _ => "Не удалось узнать доступную версию.",
    };

    // ── кэш ──────────────────────────────────────────────────────────────

    /// <summary>Форма кэша на диске — то, что переживает перезапуск.
    /// Обычный класс с сеттерами, чтобы формат можно было расширять, не
    /// ломая чтение старых файлов.</summary>
    public sealed class Cached
    {
        public DateTimeOffset FetchedAt { get; set; }
        public RemoteBuild? Build { get; set; }
    }

    private string CachePath(string id) =>
        Path.Combine(_library.CacheDir, "meta", id + ".json");

    private Cached? ReadCache(string id)
    {
        if (_memory.TryGetValue(id, out var hit)) return hit;

        try
        {
            var path = CachePath(id);
            if (!File.Exists(path)) return null;

            var cached = JsonSerializer.Deserialize<Cached>(File.ReadAllText(path), Json.Local);
            if (cached is null) return null;

            _memory[id] = cached;
            return cached;
        }
        catch (Exception)
        {
            // Ловим всё намеренно: кэш — вспомогательный файл, испортить его
            // может что угодно, а цена промаха — один лишний запрос к сети.
            // Ронять из-за него запуск нельзя.
            return null;
        }
    }

    private void WriteCache(string id, Cached cached)
    {
        _memory[id] = cached;
        try
        {
            AtomicFile.WriteAllText(CachePath(id), JsonSerializer.Serialize(cached, Json.Local));
        }
        catch (Exception)
        {
            // На диск не легло — в памяти всё равно есть.
        }
    }

    // ── форма файла на раздаче ───────────────────────────────────────────

    /// <summary>То, что пишет publish.yml. Отдельно от <see cref="RemoteBuild"/>:
    /// это форма на проводе, и меняться она может независимо от того, чем
    /// внутри лаунчера удобно пользоваться.</summary>
    private sealed class Meta
    {
        public int Schema { get; set; } = 1;
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? Commit { get; set; }
        public string? File { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
        public DateTimeOffset? Published { get; set; }
    }
}
