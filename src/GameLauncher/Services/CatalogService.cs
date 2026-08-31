using System.Net.Http;
using System.Text.Json;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum CatalogSource
{
    Network,
    /// <summary>Сети нет — показываем последний известный список.</summary>
    Cache,
    /// <summary>Ни сети, ни кэша: первый запуск без интернета.</summary>
    None,
}

public sealed record CatalogResult(
    Catalog Catalog,
    CatalogSource Source,
    string? Warning,
    DateTimeOffset? CachedAt);

/// <summary>Читает catalog.json из публичного репозитория лаунчера.
///
/// Через raw.githubusercontent.com, а не через API: другой хост, лимита
/// 60 запросов в час не касается. Взамен у raw свой CDN-кэш около пяти
/// минут, так что правка каталога доезжает не мгновенно.</summary>
public sealed class CatalogService
{
    public const int SupportedSchema = 1;

    private const string Url =
        "https://raw.githubusercontent.com/Lonedeadly/GameLauncher/main/catalog.json";

    private readonly LibraryService _library;

    public CatalogService(LibraryService library) => _library = library;

    private string CachePath => Path.Combine(_library.CacheDir, "catalog.json");

    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadline = Http.Deadline(TimeSpan.FromSeconds(20), ct);

            // no-cache просит CDN отдать свежее, насколько он может.
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

            using var response = await Http.Client.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(deadline.Token);
            var catalog = Parse(json);

            // В кэш кладём только то, что разобралось: иначе один кривой
            // коммит в каталоге отравил бы и офлайн-режим.
            TrySaveCache(json);

            return new CatalogResult(catalog, CatalogSource.Network, SchemaWarning(catalog), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return FromCache(Describe(ex));
        }
    }

    private CatalogResult FromCache(string reason)
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var catalog = Parse(File.ReadAllText(CachePath));
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero);
                return new CatalogResult(catalog, CatalogSource.Cache,
                    $"{reason} Показан последний известный список.", at);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Кэш нечитаем — ведём себя так, будто его нет.
        }

        return new CatalogResult(new Catalog(), CatalogSource.None,
            $"{reason} Список игр ещё ни разу не загружался.", null);
    }

    private static Catalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(json, Json.Local)
                      ?? throw new JsonException("catalog.json пуст.");

        catalog.Games ??= [];

        // Кривую запись выбрасываем поимённо, а не роняем весь каталог:
        // одна опечатка не должна лишать друга остальных игр.
        catalog.Games = catalog.Games.Where(g => g.IsValid).ToList();
        return catalog;
    }

    private static string? SchemaWarning(Catalog catalog) =>
        catalog.Schema > SupportedSchema
            ? $"Каталог версии {catalog.Schema}, лаунчер понимает {SupportedSchema}. " +
              "Часть игр может не отображаться — обновите лаунчер."
            : null;

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "Список игр не загрузился: истекло время ожидания.",
        HttpRequestException => "Список игр не загрузился: нет связи с GitHub.",
        JsonException => "Список игр не загрузился: файл каталога повреждён.",
        _ => "Список игр не загрузился.",
    };

    private void TrySaveCache(string json)
    {
        try
        {
            Directory.CreateDirectory(_library.CacheDir);
            AtomicFile.WriteAllText(CachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не записался кэш — работать это не мешает.
        }
    }
}
