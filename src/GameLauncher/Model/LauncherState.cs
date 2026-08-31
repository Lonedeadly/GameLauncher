namespace GameLauncher.Model;

/// <summary>state.json в корне папки с играми: что установлено.
/// Живёт рядом с играми, а не рядом с exe, потому что описывает именно
/// содержимое этой папки.</summary>
public sealed class LauncherState
{
    public int Schema { get; set; } = 1;

    /// <summary>Ключ — <see cref="CatalogEntry.Id"/>.</summary>
    public Dictionary<string, InstalledGame> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstalledGame
{
    public string Id { get; set; } = "";

    /// <summary>Канал ЛАУНЧЕРА, из каталога и настроек. Не тот, что написан
    /// в build.json: там на стабильной сборке лежит тег, а не «stable».</summary>
    public string Channel { get; set; } = Channels.Dev;

    /// <summary>Version из build.json установленной сборки. Для показа.
    /// null у сборок, выпущенных до появления этого поля.</summary>
    public string? Version { get; set; }

    /// <summary>Коммит из build.json установленной сборки. Опознаёт сборку
    /// и служит запасным вариантом показа, когда Version нет.</summary>
    public string? Commit { get; set; }

    /// <summary>Отпечаток той сборки, что лежит на диске. Для сравнения.</summary>
    public string Fingerprint { get; set; } = "";

    public string? Tag { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public long SizeOnDisk { get; set; }
}
