using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum ReleaseSource { Network, Cache, None }

public sealed record ReleaseLookup(
    IReadOnlyDictionary<string, RemoteBuild> ByChannel,
    ReleaseSource Source,
    DateTimeOffset FetchedAt,
    string? Warning)
{
    public RemoteBuild? For(string channel) =>
        ByChannel.TryGetValue(Channels.Normalize(channel), out var b) ? b : null;
}

/// <summary>Что витрина показывает по GitHub API.
///
/// Анонимный лимит — 60 запросов в час на IP, и ETag его НЕ экономит:
/// проверено, что ответ 304 всё равно увеличивает X-RateLimit-Used.
/// Поэтому защита здесь — кэш с временем жизни на диске, а не условные
/// запросы.
///
/// Запрос ровно один на витрину: /releases отдаёт все каналы сразу, так
/// что отдельно дёргать /releases/tags/dev не нужно.</summary>
public sealed class ReleaseService
{
    /// <summary>Сколько ответ считается свежим. При двух играх это 8 запросов
    /// в час из 60 — с большим запасом.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>Нижний предел даже для ручного «обновить»: залипшая кнопка
    /// не должна сжечь лимит.</summary>
    public static readonly TimeSpan MinForcedInterval = TimeSpan.FromSeconds(60);

    private readonly LibraryService _library;
    private readonly Dictionary<string, CachedLookup> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ReleaseService(LibraryService library) => _library = library;

    /// <summary>Остаток лимита из последнего ответа GitHub. null — ещё не спрашивали.</summary>
    public int? RateLimitRemaining { get; private set; }
    public DateTimeOffset? RateLimitReset { get; private set; }

    public async Task<ReleaseLookup> GetAsync(string repo, bool force = false, CancellationToken ct = default)
    {
        var cached = ReadCache(repo);
        var age = cached is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cached.FetchedAt;

        var tooSoon = force ? age < MinForcedInterval : age < Ttl;
        if (cached is not null && tooSoon)
            return new ReleaseLookup(cached.ByChannel, ReleaseSource.Cache, cached.FetchedAt, null);

        try
        {
            var builds = await FetchAsync(repo, ct);
            var lookup = new CachedLookup(DateTimeOffset.UtcNow, builds);
            WriteCache(repo, lookup);
            return new ReleaseLookup(lookup.ByChannel, ReleaseSource.Network, lookup.FetchedAt, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            var reason = Describe(ex);
            return cached is not null
                ? new ReleaseLookup(cached.ByChannel, ReleaseSource.Cache, cached.FetchedAt,
                    $"{reason} Показаны последние известные версии.")
                : new ReleaseLookup(new Dictionary<string, RemoteBuild>(), ReleaseSource.None,
                    DateTimeOffset.MinValue, reason);
        }
    }

    // ── сеть ─────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, RemoteBuild>> FetchAsync(string repo, CancellationToken ct)
    {
        using var deadline = Http.Deadline(TimeSpan.FromSeconds(20), ct);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repo}/releases?per_page=30");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await Http.Client.SendAsync(request, deadline.Token);
        RecordRateLimit(response);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && RateLimitRemaining == 0)
        {
            var until = RateLimitReset?.ToLocalTime().ToString("HH:mm") ?? "неизвестно когда";
            throw new HttpRequestException(
                $"Лимит запросов к GitHub исчерпан, восстановится в {until}.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException($"Витрина {repo} не найдена.");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(deadline.Token);
        var releases = JsonSerializer.Deserialize<List<GhRelease>>(json, Json.GitHub) ?? [];

        return Select(releases);
    }

    private void RecordRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var r)
            && int.TryParse(r.FirstOrDefault(), out var remaining))
            RateLimitRemaining = remaining;

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var s)
            && long.TryParse(s.FirstOrDefault(), out var reset))
            RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(reset);
    }

    /// <summary>Разложить релизы по каналам. Тег dev — движущаяся сборка,
    /// всё остальное — стабильный канал, где берётся самый свежий.</summary>
    private static Dictionary<string, RemoteBuild> Select(List<GhRelease> releases)
    {
        var result = new Dictionary<string, RemoteBuild>(StringComparer.OrdinalIgnoreCase);

        var usable = releases
            .Where(r => !r.Draft && r.TagName is { Length: > 0 })
            .OrderByDescending(r => r.PublishedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        foreach (var release in usable)
        {
            var channel = string.Equals(release.TagName, Channels.Dev, StringComparison.OrdinalIgnoreCase)
                ? Channels.Dev
                : Channels.Stable;

            if (result.ContainsKey(channel)) continue;   // уже взяли более свежий

            var asset = PickAsset(release);
            if (asset is null) continue;

            result[channel] = new RemoteBuild(
                Channel: channel,
                Tag: release.TagName!,
                AssetName: asset.Name!,
                DownloadUrl: asset.BrowserDownloadUrl!,
                Size: asset.Size,
                Sha256: StripDigest(asset.Digest),
                UpdatedAt: asset.UpdatedAt ?? release.PublishedAt ?? DateTimeOffset.MinValue,
                PublishedAt: release.PublishedAt,
                CommitHint: ExtractCommit(release.Body));
        }

        return result;
    }

    /// <summary>По контракту в релизе ровно один asset вида
    /// «Игра-канал-win64.zip». Проверяем это, а не берём вслепую первый:
    /// чужой файл, случайно попавший в релиз, не должен уехать другу.</summary>
    private static GhAsset? PickAsset(GhRelease release)
    {
        var assets = release.Assets ?? [];

        return assets.FirstOrDefault(a =>
            a.State is null or "uploaded"
            && !string.IsNullOrEmpty(a.BrowserDownloadUrl)
            && a.Name is { Length: > 0 }
            && a.Name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase));
    }

    private static string? StripDigest(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;

    /// <summary>Тело релиза выглядит как «Собрано из 496ad05 (main).».
    /// Достаём коммит только чтобы было что показать до установки; сравнение
    /// версий на эту прозу не опирается.</summary>
    private static string? ExtractCommit(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var m = Regex.Match(body, @"\b[0-9a-f]{7,40}\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "GitHub не ответил вовремя.",
        HttpRequestException http => http.Message.StartsWith("Лимит") || http.Message.StartsWith("Витрина")
            ? http.Message
            : "Нет связи с GitHub.",
        JsonException => "GitHub вернул неожиданный ответ.",
        _ => "Не удалось получить список релизов.",
    };

    // ── кэш ──────────────────────────────────────────────────────────────

    private sealed record CachedLookup(
        DateTimeOffset FetchedAt,
        Dictionary<string, RemoteBuild> ByChannel);

    private string CachePath(string repo) =>
        Path.Combine(_library.CacheDir, "releases", repo.Replace('/', '_') + ".json");

    private CachedLookup? ReadCache(string repo)
    {
        if (_memory.TryGetValue(repo, out var hit)) return hit;

        try
        {
            var path = CachePath(repo);
            if (!File.Exists(path)) return null;

            var cached = JsonSerializer.Deserialize<CachedLookup>(File.ReadAllText(path), Json.Local);
            if (cached is null) return null;

            _memory[repo] = cached;
            return cached;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteCache(string repo, CachedLookup lookup)
    {
        _memory[repo] = lookup;
        try
        {
            AtomicFile.WriteAllText(CachePath(repo), JsonSerializer.Serialize(lookup, Json.Local));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Кэш на диск не лёг — в памяти он всё равно есть.
        }
    }

    // ── форма ответа GitHub ──────────────────────────────────────────────

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        public string? Name { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public string? Body { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? Digest { get; set; }
        public string? State { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
