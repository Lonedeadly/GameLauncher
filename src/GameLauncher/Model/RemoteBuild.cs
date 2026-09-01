using GameLauncher.Infrastructure;

namespace GameLauncher.Model;

/// <summary>Сборка, какой её видно из meta/&lt;id&gt;.json — до скачивания.</summary>
public sealed record RemoteBuild(
    // Строка для человека: «v0.1.0-49-gbc52f3b», из git describe.
    string Version,
    // Полный sha коммита, из которого собрано. Показывается редко, но
    // отвечает на вопрос «а из чего вообще эта сборка».
    string Commit,
    // Путь относительно раздачи: files/<id>/<коммит>/<файл>. Относительный
    // намеренно — переезд раздачи на другой адрес не потребует переписывать
    // метаданные.
    string RelativePath,
    long Size,
    string Sha256,
    DateTimeOffset? Published)
{
    public string DownloadUrl => Origin.Url(RelativePath);

    public string FileName =>
        RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>Отпечаток сборки: то, по чему решается «есть обновление».
    ///
    /// Сравниваем именно по нему, а не по версии: версия — строка для глаз,
    /// её можно повторить, забыть обновить или собрать заново из того же
    /// коммита. Сумма архива меняется ровно тогда, когда меняется архив.</summary>
    public string Fingerprint => $"sha256:{Sha256}";

    public string ShortCommit =>
        Commit.Length >= 7 ? Commit[..7] : Commit;
}
