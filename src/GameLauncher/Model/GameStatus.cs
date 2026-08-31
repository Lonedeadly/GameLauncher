namespace GameLauncher.Model;

public enum GameState
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    /// <summary>Установлена, но проверить обновления не вышло — нет сети
    /// и нет кэша. Играть можно, о свежести молчим.</summary>
    InstalledUnknown,
}

/// <summary>Всё, что нужно карточке игры, посчитанное заранее: состояние
/// должно быть видно сразу, без нажатий.</summary>
public sealed record GameStatus(
    CatalogEntry Entry,
    string Channel,
    InstalledGame? Installed,
    RemoteBuild? Remote,
    GameState State,
    string? Note)
{
    public static GameStatus Compute(
        CatalogEntry entry, string channel, InstalledGame? installed, RemoteBuild? remote)
    {
        channel = Channels.Normalize(channel);

        if (installed is null)
            return new GameStatus(entry, channel, null, remote, GameState.NotInstalled,
                remote is null ? "Сборок в этом канале пока нет." : null);

        if (remote is null)
            return new GameStatus(entry, channel, installed, null, GameState.InstalledUnknown,
                "Не удалось проверить обновления.");

        // Смена канала — тоже замена содержимого папки, а не «уже свежее».
        if (!string.Equals(installed.Channel, channel, StringComparison.OrdinalIgnoreCase))
            return new GameStatus(entry, channel, installed, remote, GameState.UpdateAvailable,
                $"Установлен канал «{Channels.Display(installed.Channel)}», выбран «{Channels.Display(channel)}».");

        // Сравнение по отпечатку архива, а не по тегу: тег dev не двигается
        // никогда, и по нему обновление не увидеть.
        var same = string.Equals(installed.Fingerprint, remote.Fingerprint, StringComparison.Ordinal);

        return same
            ? new GameStatus(entry, channel, installed, remote, GameState.UpToDate, null)
            : new GameStatus(entry, channel, installed, remote, GameState.UpdateAvailable, null);
    }

    /// <summary>Подпись главной кнопки.</summary>
    public string PrimaryAction => State switch
    {
        GameState.NotInstalled => "Установить",
        GameState.UpdateAvailable => "Обновить",
        _ => "Играть",
    };

    public bool CanInstallOrUpdate =>
        Remote is not null && State is GameState.NotInstalled or GameState.UpdateAvailable;

    public bool CanPlay => Installed is not null;
    public bool CanUninstall => Installed is not null;

    public string StateCaption => State switch
    {
        GameState.NotInstalled => "не установлена",
        GameState.UpToDate => "установлена, свежая",
        GameState.UpdateAvailable => "установлена, есть обновление",
        _ => "установлена",
    };

    public string InstalledVersion => Installed?.Commit is { Length: > 0 } c
        ? Short(c)
        : Installed is not null ? "неизвестно" : "—";

    public string AvailableVersion => Remote?.CommitHint is { Length: > 0 } c
        ? Short(c)
        : Remote is not null ? Remote.Tag : "—";

    private static string Short(string commit) => commit.Length >= 7 ? commit[..7] : commit;
}
