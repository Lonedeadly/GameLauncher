using System.Net;
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

/// <summary>Читает catalog.json с раздачи.
///
/// Файл кладёт туда sync-catalog.yml при каждом изменении catalog.json в
/// репозитории лаунчера. Раздача отдаёт его с no-cache, так что правка
/// списка доезжает сразу — в отличие от прежнего raw.githubusercontent.com
/// с его пятиминутным CDN.</summary>
public sealed class CatalogService
{
    /// <summary>Вторая версия формата: из записей ушли repo и defaultChannel.
    /// Каталог с бо́льшим числом лаунчер прочитает, но честно скажет, что
    /// понимает не всё.</summary>
    public const int SupportedSchema = 2;

    public const string RemotePath = "catalog.json";

    private readonly LibraryService _library;
    private readonly string? _url;

    /// <param name="url">Подменяется только самопроверкой, чтобы можно было
    /// прогнать ветку «сети нет» не отключая сеть.</param>
    public CatalogService(LibraryService library, string? url = null)
    {
        _library = library;
        _url = url;
    }

    private string CachePath => Path.Combine(_library.CacheDir, "catalog.json");

    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadline = Http.Deadline(TimeSpan.FromSeconds(20), ct);

            using var response = _url is null
                ? await Origin.GetAsync(RemotePath, deadline.Token)
                : await Http.Client.GetAsync(_url, deadline.Token);

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

    /// <summary>Причина отказа своими словами.
    ///
    /// Разделять «не достучались» и «достучались, а ответ плохой»
    /// обязательно: 404 из-за опечатки в пути и оборванная сеть лечатся
    /// совершенно по-разному, а сообщение «нет связи» увело бы не туда.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => $"{Origin.Host} не ответил вовремя.",

        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "Файл каталога не найден на раздаче.",

        HttpRequestException { StatusCode: { } code } =>
            $"Раздача ответила ошибкой {(int)code}.",

        HttpRequestException => $"Нет связи с {Origin.Host}.",

        JsonException => "Файл каталога повреждён.",

        _ => "Список игр загрузить не удалось.",
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
