using System.Text.Json;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

public enum LibraryVerdict
{
    /// <summary>Наша папка либо пустая, либо ещё не существует.</summary>
    Ok,
    /// <summary>Существует, непуста, метки нет — чужая. Не трогаем.</summary>
    NotOwned,
    /// <summary>Путь негоден: некорректен, или создать/писать нельзя.</summary>
    Unusable,
}

public sealed record LibraryCheck(LibraryVerdict Verdict, string Message);

/// <summary>Папка с играми: где она, наша ли она, и что в ней установлено.
///
/// Главное правило: лаунчер не пишет за пределы папки, которой владеет.
/// Владение подтверждается файлом-меткой в её корне.</summary>
public sealed class LibraryService
{
    public const string MarkerName = ".gamelauncher";
    private const string StateName = "state.json";
    public const int SupportedSchema = 1;

    public string Root { get; private set; } = "";
    public LauncherState State { get; private set; } = new();

    public string TempDir => Path.Combine(Root, ".tmp");
    public string CacheDir => Path.Combine(Root, "cache");
    public string StatePath => Path.Combine(Root, StateName);
    public string MarkerPath => Path.Combine(Root, MarkerName);

    public string GameDir(string gameId) => Path.Combine(Root, gameId);

    // ── выбор пути при первом запуске ────────────────────────────────────

    /// <summary>Предлагаемый путь. Рядом с exe — но не тогда, когда лаунчер
    /// лежит там, где ему не место.</summary>
    public static string SuggestPath()
    {
        var exeDir = AppPaths.ExeDirectory;
        return IsBadHome(exeDir)
            ? Path.Combine(AppPaths.LocalAppData, "Games")
            : Path.Combine(exeDir, "GameLauncher-Games");
    }

    /// <summary>Места, рядом с которыми нельзя разворачивать папку с играми:
    /// корень диска, Program Files, Windows и сам рабочий стол.</summary>
    public static bool IsBadHome(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);

            var root = Path.GetPathRoot(full);
            if (root is not null && AppPaths.IsSameDirectory(root, full)) return true;

            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.Windows,
                     })
            {
                var p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) && AppPaths.IsUnder(full, p)) return true;
            }

            foreach (var desktop in DesktopDirectories())
                if (AppPaths.IsSameDirectory(desktop, full)) return true;

            return false;
        }
        catch (ArgumentException) { return true; }
        catch (IOException) { return true; }
    }

    private static IEnumerable<string> DesktopDirectories()
    {
        // GetFolderPath уже учитывает перенос рабочего стола в OneDrive, но
        // подстрахуемся: у перенесённого профиля бывают оба варианта сразу.
        foreach (var f in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) yield return p;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, "Desktop");

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive)) yield return Path.Combine(oneDrive, "Desktop");
    }

    /// <summary>Можно ли делать эту папку библиотекой. Ничего не создаёт
    /// насовсем: пробная папка, если её пришлось завести, убирается.</summary>
    public static LibraryCheck Check(string path)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return new(LibraryVerdict.Unusable, "Путь не указан.");
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(LibraryVerdict.Unusable, "Путь некорректен.");
        }

        if (!Path.IsPathRooted(full))
            return new(LibraryVerdict.Unusable, "Нужен абсолютный путь.");

        if (Directory.Exists(full))
        {
            var owned = File.Exists(Path.Combine(full, MarkerName));
            var empty = !Directory.EnumerateFileSystemEntries(full).Any();

            if (!owned && !empty)
                return new(LibraryVerdict.NotOwned,
                    "Папка не пуста и не создана лаунчером. Чтобы ничего в ней не задеть, " +
                    "лаунчер туда писать не станет — выберите другой путь.");

            return AppPaths.IsWritable(full)
                ? new(LibraryVerdict.Ok, owned ? "Папка лаунчера." : "Пустая папка, годится.")
                : new(LibraryVerdict.Unusable, "В эту папку нет прав на запись.");
        }

        // Папки нет — проверяем, что её реально можно создать, а не гадаем
        // по пути. Заодно это ловит недоступные сетевые и съёмные диски.
        //
        // Проба обязана не оставить следов: путь может быть вида D:\a\b\c,
        // где не существует ни одного уровня, и убрать надо все созданные,
        // а не только последний.
        var created = MissingAncestors(full);
        try
        {
            Directory.CreateDirectory(full);
            var writable = AppPaths.IsWritable(full);
            return writable
                ? new(LibraryVerdict.Ok, "Папка будет создана.")
                : new(LibraryVerdict.Unusable, "Папку удалось создать, но писать в неё нельзя.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(LibraryVerdict.Unusable, "Не удалось создать папку по этому пути.");
        }
        finally
        {
            // Снизу вверх: сначала самая глубокая.
            foreach (var dir in created) TryRemoveIfEmpty(dir);
        }
    }

    /// <summary>Уровни пути, которых сейчас нет, от самого глубокого к
    /// верхнему — то есть ровно то, что создаст CreateDirectory.</summary>
    private static List<string> MissingAncestors(string full)
    {
        var missing = new List<string>();
        for (var d = new DirectoryInfo(full); d is not null && !d.Exists; d = d.Parent)
            missing.Add(d.FullName);
        return missing;
    }

    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Осталась — не беда, но и не наша забота её доламывать.
        }
    }

    // ── открытие библиотеки ──────────────────────────────────────────────

    /// <summary>Создаёт при необходимости папку, ставит метку, читает state.json.</summary>
    public void Open(string path)
    {
        var check = Check(path);
        if (check.Verdict != LibraryVerdict.Ok)
            throw new InvalidOperationException(check.Message);

        Root = Path.GetFullPath(path);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(TempDir);
        WriteMarkerIfMissing();
        LoadState();
    }

    private void WriteMarkerIfMissing()
    {
        if (File.Exists(MarkerPath)) return;

        var marker = new
        {
            schema = SupportedSchema,
            created = DateTimeOffset.UtcNow,
            note = "Папка создана GameLauncher. Метка нужна, чтобы лаунчер не писал в чужие папки.",
        };
        AtomicFile.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, Json.Local));
    }

    private void LoadState()
    {
        try
        {
            State = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(StatePath), Json.Local) ?? new()
                : new LauncherState();
            State.Games ??= new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Битый state.json не должен ронять лаунчер: считаем, что ничего
            // не установлено. Папки игр на диске при этом целы, переустановка
            // их просто перезапишет.
            State = new LauncherState();
        }
    }

    public void SaveState() =>
        AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(State, Json.Local));

    /// <summary>Запись об установке — но только если папка игры и правда на
    /// месте. Иначе state врёт, а кнопка предлагает «Играть» в пустоту.</summary>
    public InstalledGame? GetInstalled(string gameId) =>
        State.Games.TryGetValue(gameId, out var g) && Directory.Exists(GameDir(gameId)) ? g : null;

    public void SetInstalled(InstalledGame game)
    {
        State.Games[game.Id] = game;
        SaveState();
    }

    public void ForgetInstalled(string gameId)
    {
        State.Games.Remove(gameId);
        SaveState();
    }

    /// <summary>Подобрать мусор от прерванных установок. Вызывается при
    /// старте: оборванная закачка не должна копить хлам.</summary>
    public void CleanTemp()
    {
        if (!Directory.Exists(TempDir)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(TempDir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занят другим процессом — оставим до следующего запуска.
            }
        }
    }
}
