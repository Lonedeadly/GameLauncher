using System.Text;
using System.Text.Json;
using GameLauncher.Infrastructure;
using GameLauncher.Model;
using GameLauncher.Services;

namespace GameLauncher.Diagnostics;

/// <summary>Прогон сервисов без интерфейса: <c>GameLauncher.exe --selftest &lt;папка&gt;</c>.
///
/// Нужен, чтобы проверять работу с живыми витринами до того, как появится
/// окно, и чтобы потом было чем диагностировать чужую машину.</summary>
public static class SelfTest
{
    private static StreamWriter? _log;
    private static int _failures;

    public static async Task<int> RunAsync(string[] args)
    {
        var workDir = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault()
                      ?? Path.Combine(Path.GetTempPath(), "GameLauncher-selftest");

        Directory.CreateDirectory(workDir);

        // Свежая консоль Windows живёт в кодовой странице 866, и русский
        // вывод в ней превращается в кашу. Может не получиться, если поток
        // перенаправлен — тогда и не нужно.
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        _log = new StreamWriter(Path.Combine(workDir, "selftest.log"), append: false, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        try
        {
            Head("Окружение");
            Line($"exe            {AppPaths.ExeDirectory}");
            Line($"рабочая папка  {workDir}");

            var settings = new SettingsService();
            Line($"конфиг         {settings.ConfigPath}");
            Line($"портативный    {(settings.IsPortable ? "да" : "нет — рядом с exe писать нельзя")}");

            TestSuggestedPaths();

            var library = new LibraryService();
            await TestOwnership(workDir, library);

            var catalog = await TestCatalog(library, workDir);
            if (catalog.Games.Count == 0)
            {
                Fail("Каталог пуст — дальше проверять нечего.");
                return Finish();
            }

            var releases = new ReleaseService(library);
            var lookups = await TestReleases(releases, catalog);

            await TestInstallCycle(library, releases, catalog, lookups);

            return Finish();
        }
        catch (Exception ex)
        {
            Fail($"Необработанное исключение: {ex}");
            return Finish();
        }
        finally
        {
            _log?.Dispose();
        }
    }

    // ── 1. предлагаемый путь ─────────────────────────────────────────────

    private static void TestSuggestedPaths()
    {
        Head("Предлагаемый путь для игр");
        Line($"для нас        {LibraryService.SuggestPath()}");

        var cases = new (string Dir, bool Bad, string Why)[]
        {
            (@"C:\", true, "корень диска"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true, "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), true, "Program Files (x86)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), true, "Windows"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true, "рабочий стол"),
            (@"D:\Games\Launcher", false, "обычная папка"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                false, "Загрузки"),
        };

        foreach (var (dir, bad, why) in cases)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var actual = LibraryService.IsBadHome(dir);
            Check(actual == bad, $"{why,-22} {dir}  →  {(actual ? "%LOCALAPPDATA%" : "рядом с exe")}");
        }
    }

    // ── 2. владение папкой ───────────────────────────────────────────────

    private static async Task TestOwnership(string workDir, LibraryService library)
    {
        Head("Владение папкой");

        var foreign = Path.Combine(workDir, "foreign");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "важный-файл.txt"), "не трогать");

        var verdict = LibraryService.Check(foreign);
        Check(verdict.Verdict == LibraryVerdict.NotOwned,
            $"чужая непустая папка → {verdict.Verdict}");
        Check(File.Exists(Path.Combine(foreign, "важный-файл.txt")), "чужой файл не тронут");
        Check(!File.Exists(Path.Combine(foreign, LibraryService.MarkerName)), "метка в чужую папку не записана");

        // Проба несуществующего пути в несколько уровней не должна
        // оставить после себя ни одной созданной папки.
        var deep = Path.Combine(workDir, "проба", "вложенная", "ещё");
        Check(LibraryService.Check(deep).Verdict == LibraryVerdict.Ok, "глубокий несуществующий путь годится");
        Check(!Directory.Exists(Path.Combine(workDir, "проба")), "проба пути не оставила следов на диске");

        var lib = Path.Combine(workDir, "library");
        Check(LibraryService.Check(lib).Verdict == LibraryVerdict.Ok, "новая папка годится");

        library.Open(lib);
        Check(File.Exists(library.MarkerPath), "метка поставлена");
        Check(LibraryService.Check(lib).Verdict == LibraryVerdict.Ok, "своя папка опознана как своя");

        library.CleanTemp();
    }

    // ── 3. каталог ───────────────────────────────────────────────────────

    private static async Task<Catalog> TestCatalog(LibraryService library, string workDir)
    {
        Head("Каталог");

        var service = new CatalogService(library);
        var result = await service.LoadAsync();
        Line($"источник       {result.Source}");
        if (result.Warning is not null) Line($"предупреждение {result.Warning}");

        if (result.Source == CatalogSource.Network)
        {
            Check(result.Catalog.Games.Count > 0, $"из сети получено игр: {result.Catalog.Games.Count}");
            return result.Catalog;
        }

        // Репозиторий лаунчера ещё не опубликован, поэтому сетевой путь
        // ожидаемо не сработал. Это и есть проверка деградации: упасть было
        // нельзя, и не упали. Дальше берём каталог из рабочей копии.
        Check(result.Source is CatalogSource.None or CatalogSource.Cache,
            "недоступный каталог не уронил лаунчер");

        var localPath = FindRepoCatalog();
        if (localPath is null)
        {
            Fail("Локальный catalog.json не найден.");
            return new Catalog();
        }

        Line($"локальный      {localPath}");
        var local = JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(localPath), Json.Local)!;
        Check(local.Games.Count > 0, $"в локальном каталоге игр: {local.Games.Count}");
        Check(local.Games.All(g => g.IsValid), "все записи каталога валидны");
        return local;
    }

    private static string? FindRepoCatalog()
    {
        var dir = new DirectoryInfo(AppPaths.ExeDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "catalog.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ── 4. релизы ────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, ReleaseLookup>> TestReleases(
        ReleaseService releases, Catalog catalog)
    {
        Head("Релизы");
        var lookups = new Dictionary<string, ReleaseLookup>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in catalog.Games)
        {
            var lookup = await releases.GetAsync(game.Repo);
            lookups[game.Id] = lookup;

            Line($"{game.Repo}");
            Line($"  источник     {lookup.Source}");
            if (lookup.Warning is not null) Line($"  замечание    {lookup.Warning}");

            foreach (var channel in Channels.All)
            {
                var build = lookup.For(channel);
                if (build is null)
                {
                    Line($"  {channel,-8}     сборок нет");
                    continue;
                }

                Line($"  {channel,-8}     тег {build.Tag}, {build.AssetName}, {build.Size / 1024} КБ");
                Line($"               отпечаток {build.Fingerprint}");
                Line($"               коммит из описания: {build.CommitHint ?? "—"}");
                Check(build.HasStrongFingerprint, $"{channel}: digest получен без скачивания архива");
                Check(build.AssetName.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase),
                    $"{channel}: имя ассета по контракту");
            }
        }

        Line($"остаток лимита GitHub: {releases.RateLimitRemaining?.ToString() ?? "неизвестно"}" +
             (releases.RateLimitReset is { } r ? $", сброс в {r.ToLocalTime():HH:mm}" : ""));

        // Второй заход обязан прийти из кэша и не потратить ни одного запроса.
        var before = releases.RateLimitRemaining;
        foreach (var game in catalog.Games) await releases.GetAsync(game.Repo);
        Check(releases.RateLimitRemaining == before, "повторный опрос обслужен кэшем, лимит не потрачен");

        return lookups;
    }

    // ── 5. установка, обновление, удаление ───────────────────────────────

    private static async Task TestInstallCycle(
        LibraryService library, ReleaseService releases, Catalog catalog,
        Dictionary<string, ReleaseLookup> lookups)
    {
        Head("Установка");

        var pick = catalog.Games.FirstOrDefault(g =>
            lookups.TryGetValue(g.Id, out var l) && l.For(Channels.Dev) is not null);

        if (pick is null)
        {
            Fail("Ни у одной игры нет dev-сборки — цикл установки не проверить.");
            return;
        }

        var remote = lookups[pick.Id].For(Channels.Dev)!;
        Line($"игра           {pick.Name} ({pick.Id}), канал dev, тег {remote.Tag}");

        var status = GameStatus.Compute(pick, Channels.Dev, library.GetInstalled(pick.Id), remote);
        Check(status.State == GameState.NotInstalled, $"до установки: {status.StateCaption} / кнопка «{status.PrimaryAction}»");

        var install = new InstallService(library);
        var phases = new List<InstallPhase>();
        var progress = new Progress<InstallProgress>(p =>
        {
            if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase);
        });

        var installed = await install.InstallAsync(pick, remote, progress);

        Line($"поставлено     коммит {installed.Commit?[..7]}, {installed.SizeOnDisk / 1024} КБ на диске");
        Line($"фазы прогресса {string.Join(" → ", phases)}");

        var gameDir = library.GameDir(pick.Id);
        Check(File.Exists(Path.Combine(gameDir, pick.Exe)), $"{pick.Exe} на месте");
        Check(File.Exists(Path.Combine(gameDir, "build.json")), "build.json на месте");
        Check(installed.Fingerprint == remote.Fingerprint, "отпечаток записан в state.json");
        Check(File.Exists(library.StatePath), "state.json создан");

        Line("содержимое папки игры:");
        foreach (var e in Directory.EnumerateFileSystemEntries(gameDir).OrderBy(x => x))
            Line($"  {(Directory.Exists(e) ? "[папка] " : "        ")}{Path.GetFileName(e)}");

        // Коммит из build.json обязан совпасть с тем, что GitHub написал в
        // описании релиза — это перекрёстная проверка контракта.
        if (remote.CommitHint is { Length: >= 7 } hint)
            Check(installed.Commit?.StartsWith(hint, StringComparison.OrdinalIgnoreCase) == true,
                $"коммит из build.json ({installed.Commit?[..7]}) совпал с описанием релиза ({hint})");

        status = GameStatus.Compute(pick, Channels.Dev, library.GetInstalled(pick.Id), remote);
        Check(status.State == GameState.UpToDate, $"после установки: {status.StateCaption} / кнопка «{status.PrimaryAction}»");

        Check(library.CacheDir.StartsWith(library.Root), "кэш лежит внутри папки библиотеки");
        Check(!Directory.EnumerateFileSystemEntries(library.TempDir).Any(), "временная папка убрана за собой");

        await TestReplaceSemantics(library, install, pick, remote);
        TestUninstall(library, install, pick, gameDir);
    }

    /// <summary>Главное свойство обновления: это замена папки, а не докачка
    /// поверх. Файл, которого нет в новой версии, обязан исчезнуть.</summary>
    private static async Task TestReplaceSemantics(
        LibraryService library, InstallService install, CatalogEntry pick, RemoteBuild remote)
    {
        Head("Обновление заменяет, а не докачивает");

        var gameDir = library.GameDir(pick.Id);
        var stray = Path.Combine(gameDir, "лишний-файл-из-старой-версии.txt");
        var strayDir = Path.Combine(gameDir, "лишняя-папка");

        await File.WriteAllTextAsync(stray, "остаток прошлой сборки");
        Directory.CreateDirectory(strayDir);
        await File.WriteAllTextAsync(Path.Combine(strayDir, "мусор.dat"), "x");

        Check(File.Exists(stray), "подложили лишний файл в папку игры");

        await install.InstallAsync(pick, remote);

        Check(!File.Exists(stray), "лишний файл исчез после переустановки");
        Check(!Directory.Exists(strayDir), "лишняя папка исчезла после переустановки");
        Check(File.Exists(Path.Combine(gameDir, pick.Exe)), $"{pick.Exe} при этом на месте");
    }

    private static void TestUninstall(
        LibraryService library, InstallService install, CatalogEntry pick, string gameDir)
    {
        Head("Удаление");

        var marker = library.MarkerPath;
        install.Uninstall(pick);

        Check(!Directory.Exists(gameDir), "папка игры удалена");
        Check(library.GetInstalled(pick.Id) is null, "запись из state.json убрана");
        Check(File.Exists(marker), "метка библиотеки на месте");
        Check(File.Exists(library.StatePath), "state.json на месте");
        Check(Directory.Exists(library.Root), "сама библиотека не тронута");
    }

    // ── вывод ────────────────────────────────────────────────────────────

    private static void Head(string title)
    {
        Line("");
        Line($"── {title} " + new string('─', Math.Max(0, 60 - title.Length)));
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Line($"  [{(ok ? "ok" : "ПРОВАЛ")}] {what}");
    }

    private static void Fail(string what)
    {
        _failures++;
        Line($"  [ПРОВАЛ] {what}");
    }

    private static void Line(string text)
    {
        Console.WriteLine(text);
        _log?.WriteLine(text);
    }

    private static int Finish()
    {
        Line("");
        Line(_failures == 0 ? "ВСЁ ПРОШЛО" : $"ПРОВАЛОВ: {_failures}");
        return _failures == 0 ? 0 : 1;
    }
}
