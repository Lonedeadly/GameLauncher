using System.Diagnostics;
using System.Net.Http;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum SelfUpdateState
{
    /// <summary>Ещё не спрашивали.</summary>
    Unknown,

    /// <summary>Сборка из исходников (0.0.0). Подменять её релизной — не то,
    /// чего ждёт человек, который её собрал.</summary>
    DevBuild,

    UpToDate,
    Available,

    /// <summary>Спросить не вышло. Работе с играми это мешать не должно.</summary>
    Failed,
}

public sealed record SelfUpdateInfo(
    SelfUpdateState State,
    string Current,
    string? Latest,
    RemoteBuild? Build,
    string? Message);

/// <summary>Обновление самого лаунчера.
///
/// Windows не даёт переписать работающий exe, поэтому обновление идёт в две
/// половины и в двух процессах:
///
///   1. текущий лаунчер качает новый файл, кладёт его рядом как
///      «GameLauncher.new.exe» и запускает с «--replace [старый] [pid]»,
///      после чего немедленно выходит;
///   2. новый процесс дожидается выхода старого, отодвигает старый файл
///      в «.old», копирует себя на его место и запускает обратно.
///
/// Старый файл цел в любой момент: он либо на месте, либо переименован и
/// при неудаче возвращается. Остаться совсем без лаунчера нельзя.
///
/// В отличие от игр, здесь сравниваются номера версий, а не отпечатки: у
/// лаунчера теги нормальные (v0.2.0), а у игр тег dev не меняется.</summary>
public sealed class SelfUpdateService
{
    /// <summary>Свой же публичный репозиторий. Токен не нужен.</summary>
    public const string Repo = "Lonedeadly/GameLauncher";

    private const string ReplaceFlag = "--replace";

    private readonly LibraryService _library;
    private readonly ReleaseService _releases;

    public SelfUpdateService(LibraryService library, ReleaseService releases)
    {
        _library = library;
        _releases = releases;
    }

    // ── проверка ─────────────────────────────────────────────────────────

    public async Task<SelfUpdateInfo> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        var current = AppVersion.Display;

        // Локальную сборку не трогаем и запрос на неё не тратим.
        if (AppVersion.IsDevBuild)
            return new SelfUpdateInfo(SelfUpdateState.DevBuild, current, null, null, null);

        try
        {
            var lookup = await _releases.GetAsync(Repo, force, ct);
            var build = lookup.For(Channels.Stable);

            if (build is null)
                return new SelfUpdateInfo(SelfUpdateState.Failed, current, null, null,
                    lookup.Warning ?? "Релизов лаунчера не найдено.");

            var latest = TryParseTag(build.Tag);
            if (latest is null)
                return new SelfUpdateInfo(SelfUpdateState.Failed, current, build.Tag, null,
                    $"Непонятный номер версии в теге «{build.Tag}».");

            return IsNewer(current, build.Tag)
                ? new SelfUpdateInfo(SelfUpdateState.Available, current, latest.ToString(), build, null)
                : new SelfUpdateInfo(SelfUpdateState.UpToDate, current, latest.ToString(), null, null);
        }
        catch (Exception ex)
        {
            return new SelfUpdateInfo(SelfUpdateState.Failed, current, null, null,
                $"Проверить обновления лаунчера не удалось: {ex.Message}");
        }
    }

    /// <summary>Новее ли релиз с тегом <paramref name="tag"/>, чем версия
    /// <paramref name="current"/>. Отдельным методом, чтобы это можно было
    /// проверить таблицей, не пересобирая лаунчер под каждый номер.</summary>
    public static bool IsNewer(string current, string tag)
    {
        var mine = TryParseTag(current);
        var theirs = TryParseTag(tag);
        return mine is not null && theirs is not null && theirs > mine;
    }

    /// <summary>«v0.2.0» и «0.2.0» — одно и то же. Суффикс вроде «-beta»
    /// отбрасывается: System.Version его не понимает, а на «новее или нет»
    /// он для нас не влияет.</summary>
    public static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        return Version.TryParse(text, out var version) ? version : null;
    }

    // ── первая половина: скачать и передать эстафету ─────────────────────

    /// <summary>Качает новую версию и запускает её заменять текущую.
    /// После успеха вызывающий обязан немедленно завершить приложение —
    /// новый процесс ждёт именно этого.</summary>
    public async Task<Process> ApplyAsync(
        RemoteBuild build,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InstallException("Не удалось определить путь к самому лаунчеру.");

        var exeDir = Path.GetDirectoryName(exePath)
            ?? throw new InstallException("Не удалось определить папку лаунчера.");

        // Право на запись проверяем ДО закачки: узнать про Program Files
        // после пятидесяти мегабайт было бы издевательством.
        if (!AppPaths.IsWritable(exeDir))
            throw new InstallException(
                $"В папку с лаунчером нельзя писать:\n{exeDir}\n\n" +
                "Перенесите файл туда, где у вас есть права, и повторите.");

        Directory.CreateDirectory(_library.TempDir);

        var id = Guid.NewGuid().ToString("N")[..8];
        var staged = Path.Combine(_library.TempDir, $"launcher-{id}.exe");
        var newExe = Sibling(exePath, ".new");

        try
        {
            string actual;
            try
            {
                actual = await Downloader.ToFileAsync(
                    build.DownloadUrl, staged, build.Size,
                    (done, total) => progress?.Report(
                        new InstallProgress(InstallPhase.Downloading, done, total)),
                    ct);
            }
            catch (HttpRequestException ex)
            {
                throw new InstallException($"Не удалось скачать новую версию: {ex.Message}.", ex);
            }

            progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, 0));
            Verify(build, staged, actual);

            progress?.Report(new InstallProgress(InstallPhase.Swapping, 0, 0));

            // Копируем рядом со старым: подменять придётся в этой же папке,
            // и лучше упереться в запрет сейчас, чем после выхода из процесса.
            File.Copy(staged, newExe, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AtomicFile.TryDelete(newExe);
            throw new InstallException($"Не удалось подготовить обновление: {ex.Message}", ex);
        }
        finally
        {
            AtomicFile.TryDelete(staged);
        }

        var info = new ProcessStartInfo
        {
            FileName = newExe,
            WorkingDirectory = exeDir,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(ReplaceFlag);
        info.ArgumentList.Add(exePath);
        info.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            return Process.Start(info)
                   ?? throw new InstallException("Не удалось запустить обновление.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AtomicFile.TryDelete(newExe);
            throw new InstallException("Не удалось запустить обновление.", ex);
        }
    }

    /// <summary>Скачанное сверяем так же строго, как архивы игр: подменять
    /// сам лаунчер непроверенным файлом — худшее, что можно придумать.</summary>
    private static void Verify(RemoteBuild build, string path, string actualSha)
    {
        if (build.Sha256 is { Length: > 0 })
        {
            if (!string.Equals(actualSha, build.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException(
                    "Контрольная сумма скачанного лаунчера не совпала с заявленной GitHub. " +
                    "Обновление отменено.");
            return;
        }

        var size = new FileInfo(path).Length;
        if (build.Size > 0 && size != build.Size)
            throw new InstallException(
                $"Размер скачанного не совпал: ожидалось {build.Size} байт, получено {size}.");
    }

    // ── вторая половина: подменить и вернуть управление ──────────────────

    public static bool IsReplaceRun(string[] args) => args.Contains(ReplaceFlag);

    /// <summary>Выполняется уже в НОВОМ файле. Возвращает описание неудачи
    /// или null. Старый лаунчер запускается в любом случае — даже если
    /// подмена не удалась, человек не должен остаться ни с чем.</summary>
    public static string? RunReplace(string[] args)
    {
        var i = Array.IndexOf(args, ReplaceFlag);
        var target = args.ElementAtOrDefault(i + 1);
        var pidText = args.ElementAtOrDefault(i + 2);

        if (string.IsNullOrEmpty(target) || !int.TryParse(pidText, out var pid))
            return "Обновление запущено с неполными аргументами.";

        var self = Environment.ProcessPath;
        if (self is null) return "Не удалось определить путь к новому файлу.";

        WaitForExit(pid, TimeSpan.FromSeconds(30));

        var failure = TrySwap(self, target, Sibling(target, ".old"));

        TryStart(target);
        return failure;
    }

    private static string? TrySwap(string self, string target, string backup)
    {
        Exception? last = null;

        // Windows отпускает файл вышедшего процесса не мгновенно, поэтому
        // пробуем несколько раз, а не сдаёмся на первой же ошибке.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                AtomicFile.TryDelete(backup);

                // Старый уступает имя, но остаётся на диске: если следующий
                // шаг сорвётся, будет что вернуть.
                if (File.Exists(target)) File.Move(target, backup);

                try
                {
                    File.Copy(self, target, overwrite: true);
                }
                catch
                {
                    if (File.Exists(backup) && !File.Exists(target)) File.Move(backup, target);
                    throw;
                }

                AtomicFile.TryDelete(backup);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }

        return $"Заменить файл лаунчера не удалось: {last?.Message}";
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Процесса уже нет — ровно то, чего мы ждали.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryStart(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // ── уборка ───────────────────────────────────────────────────────────

    /// <summary>Убрать «.new» и «.old» рядом с exe. Именно на старте: сразу
    /// после подмены удалить их нельзя — один из двух в этот момент ещё
    /// выполняется. Отсюда и повторные попытки.</summary>
    public static void CleanLeftovers()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var leftovers = new[] { Sibling(exePath, ".new"), Sibling(exePath, ".old") }
            .Where(p => !string.Equals(p, exePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (leftovers.All(p => !File.Exists(p))) return;
                foreach (var path in leftovers) AtomicFile.TryDelete(path);
                await Task.Delay(500);
            }
        });
    }

    /// <summary>«GameLauncher.exe» + «.new» → «GameLauncher.new.exe».
    /// Имя берётся от текущего файла, а не из константы: exe могли
    /// переименовать, и обновление обязано это пережить.</summary>
    public static string Sibling(string path, string suffix) =>
        Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));
}
