namespace GameLauncher.Model;

/// <summary>catalog.json — список игр как данные. Лежит на раздаче рядом
/// с играми, добавление игры не требует пересборки лаунчера.</summary>
public sealed class Catalog
{
    /// <summary>Версия формата. Лаунчер, увидев незнакомую, должен сказать
    /// «обнови лаунчер», а не упасть.
    ///
    /// Второй версией стал переезд с GitHub на свою раздачу: из записи
    /// ушли repo (адресов больше нет, всё выводится из id) и defaultChannel
    /// (каналов больше нет — main и есть текущая версия).</summary>
    public int Schema { get; set; } = 2;

    public List<CatalogEntry> Games { get; set; } = [];
}

public sealed class CatalogEntry
{
    /// <summary>Идентификатор игры. Он же — имя папки на диске и он же —
    /// адрес на раздаче, поэтому ожидается коротким и латиницей.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Имя exe внутри архива, в его корне.</summary>
    public string Exe { get; set; } = "";

    public string Description { get; set; } = "";

    public string? Image { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Exe)
        && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
