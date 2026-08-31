namespace GameLauncher.Model;

/// <summary>build.json из корня распакованной игры. Лежит ВНУТРИ архива,
/// поэтому доступен только после скачивания — сравнивать версии по нему
/// нельзя, для этого есть <see cref="RemoteBuild.Fingerprint"/>.</summary>
public sealed class BuildInfo
{
    public string Game { get; set; } = "";
    public string Channel { get; set; } = "";

    /// <summary>Полный sha коммита игры. Показывается пользователю как версия.</summary>
    public string Commit { get; set; } = "";

    public DateTimeOffset? Built { get; set; }

    public string ShortCommit =>
        Commit.Length >= 7 ? Commit[..7] : Commit;
}
