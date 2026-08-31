namespace GameLauncher.Model;

/// <summary>catalog.json — список игр как данные. Читается из публичного
/// репозитория лаунчера, добавление игры не требует пересборки.</summary>
public sealed class Catalog
{
    /// <summary>Версия формата. Лаунчер, увидев незнакомую, должен сказать
    /// «обнови лаунчер», а не упасть.</summary>
    public int Schema { get; set; } = 1;

    public List<CatalogEntry> Games { get; set; } = [];
}

public sealed class CatalogEntry
{
    /// <summary>Идентификатор игры. Он же — имя папки на диске, поэтому
    /// ожидается коротким и латиницей.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Витрина в виде «owner/repo».</summary>
    public string Repo { get; set; } = "";

    /// <summary>Имя exe внутри архива, в его корне.</summary>
    public string Exe { get; set; } = "";

    public string Description { get; set; } = "";

    public string DefaultChannel { get; set; } = Channels.Dev;

    public string? Image { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Repo)
        && Repo.Count(c => c == '/') == 1
        && !string.IsNullOrWhiteSpace(Exe)
        && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class Channels
{
    public const string Dev = "dev";
    public const string Stable = "stable";

    public static readonly string[] All = [Stable, Dev];

    public static string Normalize(string? channel) =>
        string.Equals(channel, Dev, StringComparison.OrdinalIgnoreCase) ? Dev : Stable;

    public static string Display(string channel) =>
        channel == Dev ? "dev (тестовая)" : "стабильный";
}
