namespace GameLauncher.Model;

/// <summary>Сборка, какой её видно по GitHub API — без скачивания архива.</summary>
public sealed record RemoteBuild(
    string Channel,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long Size,
    // SHA-256 архива из поля digest ассета, без префикса «sha256:».
    // Отсутствует у релизов, залитых до появления этого поля.
    string? Sha256,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    // Короткий коммит, выуженный из текста релиза. Только чтобы показать
    // что-то осмысленное до первой установки; логика на него не опирается.
    string? CommitHint)
{
    /// <summary>Отпечаток сборки: то, по чему решается «есть обновление».
    ///
    /// Тег dev не двигается никогда, а build.json заперт внутри архива,
    /// поэтому единственный доступный заранее признак — digest ассета.
    /// Если его нет, откатываемся на размер и время заливки: хуже, но
    /// всё же меняется вместе со сборкой.</summary>
    public string Fingerprint => Sha256 is { Length: > 0 }
        ? $"sha256:{Sha256}"
        : $"size-time:{Size}:{UpdatedAt.ToUniversalTime():O}";

    public bool HasStrongFingerprint => Sha256 is { Length: > 0 };
}
