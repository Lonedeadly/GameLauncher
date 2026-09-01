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
    InstalledGame? Installed,
    RemoteBuild? Remote,
    GameState State,
    string? Note)
{
    public static GameStatus Compute(CatalogEntry entry, InstalledGame? installed, RemoteBuild? remote)
    {
        if (installed is null)
            return new GameStatus(entry, null, remote, GameState.NotInstalled,
                remote is null ? "Сборок пока нет." : null);

        if (remote is null)
            return new GameStatus(entry, installed, null, GameState.InstalledUnknown,
                "Не удалось проверить обновления.");

        // Сравнение по отпечатку архива, а не по строке версии: версию можно
        // повторить, забыть обновить или собрать заново из того же коммита.
        // Сумма меняется ровно тогда, когда меняется архив.
        var same = string.Equals(installed.Fingerprint, remote.Fingerprint, StringComparison.Ordinal);

        return same
            ? new GameStatus(entry, installed, remote, GameState.UpToDate, null)
            : new GameStatus(entry, installed, remote, GameState.UpdateAvailable, null);
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

    /// <summary>Показываем version из build.json — это git describe сборки.
    /// У выпущенных раньше сборок поля нет, тогда короткий commit, как было.</summary>
    public string InstalledVersion
    {
        get
        {
            if (Installed is null) return "—";
            if (Installed.Version is { Length: > 0 } version) return version;

            return Installed.Commit is { Length: > 0 } commit ? Short(commit) : "неизвестно";
        }
    }

    /// <summary>Что доступно. Раздача сообщает версию сразу, до скачивания, —
    /// раньше её приходилось угадывать по тегу и тексту релиза.</summary>
    public string AvailableVersion
    {
        get
        {
            if (Remote is null) return "—";
            if (Remote.Version is { Length: > 0 } version) return version;

            return Remote.Commit is { Length: > 0 } ? Remote.ShortCommit : "неизвестно";
        }
    }

    private static string Short(string commit) => commit.Length >= 7 ? commit[..7] : commit;
}
