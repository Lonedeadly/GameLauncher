namespace GameLauncher.Model;

/// <summary>launcher.config.json рядом с exe (или в %LOCALAPPDATA%, если
/// рядом с exe писать нельзя).</summary>
public sealed class LauncherSettings
{
    public int Schema { get; set; } = 1;

    /// <summary>Папка с играми. null — первый запуск, путь ещё не выбран.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>Выбранный канал по каждой игре. Канал уже установленной
    /// сборки хранится отдельно, в state.json.</summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
