using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum InstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Swapping,
    Done,
}

public readonly record struct InstallProgress(InstallPhase Phase, long Current, long Total)
{
    /// <summary>null, когда размер неизвестен — полоса должна быть бегущей,
    /// а не застрявшей на нуле.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : null;

    public string Describe() => Phase switch
    {
        InstallPhase.Downloading => Total > 0
            ? $"Загрузка… {Mb(Current)} из {Mb(Total)} МБ"
            : $"Загрузка… {Mb(Current)} МБ",
        InstallPhase.Verifying => "Проверка контрольной суммы…",
        InstallPhase.Extracting => Total > 0
            ? $"Распаковка… {Current} из {Total}"
            : "Распаковка…",
        InstallPhase.Swapping => "Замена файлов…",
        _ => "Готово",
    };

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0");
}

/// <summary>Ожидаемая, объяснимая пользователю неудача установки.</summary>
public sealed class InstallException : Exception
{
    public InstallException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Установка, обновление, удаление и запуск игры.
///
/// Обновление — это подмена папки целиком, а не докачка поверх. Поэтому
/// файл, исчезнувший в новой версии, исчезает и на диске сам собой, без
/// сравнения деревьев.</summary>
public sealed class InstallService
{
    private readonly LibraryService _library;

    public InstallService(LibraryService library) => _library = library;

    public async Task<InstalledGame> InstallAsync(
        CatalogEntry entry,
        RemoteBuild remote,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_library.TempDir);

        var id = Guid.NewGuid().ToString("N")[..8];
        var zipPath = Path.Combine(_library.TempDir, $"dl-{id}.zip");
        var stageDir = Path.Combine(_library.TempDir, $"stage-{id}");
        var oldDir = Path.Combine(_library.TempDir, $"old-{id}");

        try
        {
            var actualSha = await DownloadAsync(remote, zipPath, progress, ct);
            Verify(remote, zipPath, actualSha, progress);

            var build = Extract(entry, zipPath, stageDir, progress, ct);
            AtomicFile.TryDelete(zipPath);

            var installed = Swap(entry, remote, build, stageDir, oldDir, progress);

            progress?.Report(new InstallProgress(InstallPhase.Done, 1, 1));
            return installed;
        }
        finally
        {
            // Что бы ни случилось — обрыв, отмена, битая сумма — временное
            // не остаётся на диске, а установленная игра не тронута.
            AtomicFile.TryDelete(zipPath);
            TryDeleteDir(stageDir);
            TryDeleteDir(oldDir);
        }
    }

    // ── скачивание ───────────────────────────────────────────────────────

    private static async Task<string> DownloadAsync(
        RemoteBuild remote, string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await Http.Client.GetAsync(
            remote.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            throw new InstallException($"Не удалось скачать архив: {(int)response.StatusCode} {response.ReasonPhrase}.");

        var total = response.Content.Headers.ContentLength ?? remote.Size;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            // Не дёргаем интерфейс на каждый блок: примерно раз на 256 КБ.
            if (done - lastReport >= 262144)
            {
                progress?.Report(new InstallProgress(InstallPhase.Downloading, done, total));
                lastReport = done;
            }
        }

        progress?.Report(new InstallProgress(InstallPhase.Downloading, done, total));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Verify(RemoteBuild remote, string zipPath, string actualSha,
        IProgress<InstallProgress>? progress)
    {
        progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, 0));

        if (remote.Sha256 is { Length: > 0 })
        {
            if (!string.Equals(actualSha, remote.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException(
                    "Контрольная сумма архива не совпала с заявленной GitHub. Установка отменена.");
            return;
        }

        // Старый релиз без digest: сверяем хотя бы размер — это ловит
        // оборванную закачку, ради чего проверка в основном и нужна.
        var size = new FileInfo(zipPath).Length;
        if (remote.Size > 0 && size != remote.Size)
            throw new InstallException(
                $"Размер архива не совпал: ожидалось {remote.Size} байт, получено {size}.");
    }

    // ── распаковка ───────────────────────────────────────────────────────

    private static BuildInfo Extract(
        CatalogEntry entry, string zipPath, string stageDir,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(stageDir);
        var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageDir))
                        + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        var total = archive.Entries.Count;
        var done = 0;

        foreach (var zipEntry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.GetFullPath(Path.Combine(stageDir, zipEntry.FullName));

            // Архив свой, но правило дешёвое: запись не имеет права указывать
            // наружу распаковочной папки.
            if (!destination.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                throw new InstallException($"Архив содержит недопустимый путь: {zipEntry.FullName}");

            if (zipEntry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                zipEntry.ExtractToFile(destination, overwrite: true);
            }

            done++;
            if (done % 16 == 0 || done == total)
                progress?.Report(new InstallProgress(InstallPhase.Extracting, done, total));
        }

        return ValidateStage(entry, stageDir);
    }

    /// <summary>Проверяем то, что распаковали, ДО подмены. Установленная игра
    /// не должна пострадать от кривого архива.</summary>
    private static BuildInfo ValidateStage(CatalogEntry entry, string stageDir)
    {
        var exePath = Path.Combine(stageDir, entry.Exe);
        if (!File.Exists(exePath))
            throw new InstallException(
                $"В архиве нет {entry.Exe} в корне. Установка отменена.");

        var buildPath = Path.Combine(stageDir, "build.json");
        if (!File.Exists(buildPath))
            throw new InstallException("В архиве нет build.json. Установка отменена.");

        try
        {
            return JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(buildPath), Json.Local)
                   ?? throw new InstallException("build.json пуст.");
        }
        catch (JsonException ex)
        {
            throw new InstallException("build.json повреждён.", ex);
        }
    }

    // ── подмена ──────────────────────────────────────────────────────────

    /// <summary>Меняем местами распакованное и установленное. Move в пределах
    /// одного тома — операция на копейки, поэтому .tmp и лежит внутри папки
    /// с играми, а не в системном %TEMP% на другом диске.</summary>
    private InstalledGame Swap(
        CatalogEntry entry, RemoteBuild remote, BuildInfo build,
        string stageDir, string oldDir, IProgress<InstallProgress>? progress)
    {
        progress?.Report(new InstallProgress(InstallPhase.Swapping, 0, 0));

        var gameDir = _library.GameDir(entry.Id);
        var hadPrevious = Directory.Exists(gameDir);

        if (hadPrevious)
        {
            try
            {
                Directory.Move(gameDir, oldDir);
            }
            catch (IOException ex)
            {
                throw new InstallException(
                    "Не удалось освободить папку игры. Скорее всего игра запущена — " +
                    "закройте её и повторите.", ex);
            }
        }

        try
        {
            Directory.Move(stageDir, gameDir);
        }
        catch (IOException ex)
        {
            // Новое не встало — возвращаем старое на место, чтобы не остаться
            // вообще без игры.
            if (hadPrevious && Directory.Exists(oldDir) && !Directory.Exists(gameDir))
                Directory.Move(oldDir, gameDir);

            throw new InstallException("Не удалось заменить папку игры.", ex);
        }

        var installed = new InstalledGame
        {
            Id = entry.Id,
            Channel = remote.Channel,
            Commit = string.IsNullOrWhiteSpace(build.Commit) ? remote.CommitHint : build.Commit,
            Fingerprint = remote.Fingerprint,
            Tag = remote.Tag,
            InstalledAt = DateTimeOffset.UtcNow,
            SizeOnDisk = MeasureDirectory(gameDir),
        };

        _library.SetInstalled(installed);
        return installed;
    }

    // ── удаление и запуск ────────────────────────────────────────────────

    /// <summary>Сносит папку игры и запись о ней. И ничего кроме.</summary>
    public void Uninstall(CatalogEntry entry)
    {
        var gameDir = _library.GameDir(entry.Id);

        if (Directory.Exists(gameDir))
        {
            try
            {
                Directory.Delete(gameDir, recursive: true);
            }
            catch (IOException ex)
            {
                throw new InstallException(
                    "Не удалось удалить папку игры. Скорее всего игра запущена — " +
                    "закройте её и повторите.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InstallException("Нет прав на удаление папки игры.", ex);
            }
        }

        _library.ForgetInstalled(entry.Id);
    }

    public Process Launch(CatalogEntry entry)
    {
        var gameDir = _library.GameDir(entry.Id);
        var exePath = Path.Combine(gameDir, entry.Exe);

        if (!File.Exists(exePath))
            throw new InstallException($"{entry.Exe} не найден. Переустановите игру.");

        // Рабочая папка — папка игры: игра ищет ассеты рядом с exe.
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = gameDir,
            UseShellExecute = false,
        };

        try
        {
            return Process.Start(info) ?? throw new InstallException("Не удалось запустить игру.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InstallException($"Не удалось запустить {entry.Exe}.", ex);
        }
    }

    // ── мелочи ───────────────────────────────────────────────────────────

    private static long MeasureDirectory(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
