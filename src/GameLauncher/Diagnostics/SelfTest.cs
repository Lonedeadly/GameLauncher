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
    private static bool _keepInstalled;

    public static async Task<int> RunAsync(string[] args)
    {
        var workDir = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault()
                      ?? Path.Combine(Path.GetTempPath(), "GameLauncher-selftest");

        _keepInstalled = args.Contains("--keep");

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
            var lookups = await TestReleases(releases, catalog, library);

            await TestSelfUpdate(library, releases, args);

            TestBuildInfoForms();
            TestEmptyShowcase(library, catalog, lookups);
            await TestCatalogOffline(library, workDir);

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

    private static LibraryService? _libraryForCacheProbe;

    private static async Task<Dictionary<string, ReleaseLookup>> TestReleases(
        ReleaseService releases, Catalog catalog, LibraryService library)
    {
        _libraryForCacheProbe = library;
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

        // Ноль в остатке — это не поломка лаунчера, а исчерпанный анонимный
        // лимит GitHub: 60 запросов в час на адрес. Сказать об этом надо
        // громко: иначе прогон выглядит как внезапный отказ всего сразу.
        if (releases.RateLimitRemaining == 0)
            Line("ВНИМАНИЕ: лимит исчерпан. Всё, что ниже опирается на сеть, " +
                 "провалится не из-за кода. Дождитесь сброса и повторите.");

        // Второй заход обязан прийти из кэша и не потратить ни одного запроса.
        var before = releases.RateLimitRemaining;
        foreach (var game in catalog.Games) await releases.GetAsync(game.Repo);
        Check(releases.RateLimitRemaining == before, "повторный опрос обслужен кэшем, лимит не потрачен");

        // А это уже кэш С ДИСКА: новый экземпляр сервиса ничего не помнит,
        // как при следующем запуске лаунчера. Путь через файл иначе вообще
        // не проверялся бы — первый прогон всегда идёт в сеть.
        var fresh = new ReleaseService(_libraryForCacheProbe!);
        var repo = catalog.Games[0].Repo;
        var fromDisk = await fresh.GetAsync(repo);
        Check(fromDisk.Source == ReleaseSource.Cache, $"кэш прочитан с диска: {fromDisk.Source}");
        Check(fresh.RateLimitRemaining is null, "запроса к сети при этом не было");
        Check(fromDisk.ByChannel.Count == lookups[catalog.Games[0].Id].ByChannel.Count,
            "с диска поднялось столько же каналов, сколько было в сети");

        return lookups;
    }

    // ── 4б. пустая витрина ───────────────────────────────────────────────

    /// <summary>Игра, у которой релизов ещё нет, обязана быть в списке —
    /// с внятным состоянием и без кнопки «Установить».</summary>
    private static void TestEmptyShowcase(
        LibraryService library, Catalog catalog, Dictionary<string, ReleaseLookup> lookups)
    {
        Head("Витрина без релизов");

        var empty = catalog.Games.FirstOrDefault(g =>
            lookups.TryGetValue(g.Id, out var l) && l.Source == ReleaseSource.Network && l.ByChannel.Count == 0);

        if (empty is null)
        {
            Fail("Не нашлось витрины без релизов — случай не проверен.");
            return;
        }

        Line($"игра           {empty.Name} ({empty.Id}), витрина {empty.Repo}");
        Check(catalog.Games.Any(g => g.Id == empty.Id), "игра осталась в списке, а не пропала");

        var status = GameStatus.Compute(empty, Channels.Dev, library.GetInstalled(empty.Id), null);
        Check(status.State == GameState.NotInstalled, $"состояние: {status.StateCaption}");
        Check(!status.CanInstallOrUpdate, "кнопки «Установить» нет — ставить нечего");
        Check(!status.CanPlay, "кнопки «Играть» нет");
        Check(status.Note is { Length: > 0 }, $"объяснение показано: {status.Note}");
        Check(status.AvailableVersion == "—", "доступная версия показана прочерком");
    }

    // ── 4в. каталог без сети ─────────────────────────────────────────────

    /// <summary>Каталог недоступен — показываем последний известный список,
    /// а не пустое окно.</summary>
    private static async Task TestCatalogOffline(LibraryService library, string workDir)
    {
        Head("Каталог недоступен");

        // Сохранённая копия появляется штатным путём: кладём её тем же
        // сервисом, только источником служит локальный файл через file-URL
        // мы пользоваться не можем, поэтому пишем кэш напрямую тем же JSON.
        var localPath = FindRepoCatalog()!;
        var cachePath = Path.Combine(library.CacheDir, "catalog.json");
        Directory.CreateDirectory(library.CacheDir);
        File.Copy(localPath, cachePath, overwrite: true);

        // Заведомо несуществующий хост — сеть при этом отключать не нужно.
        var offline = new CatalogService(library, "https://raw.githubusercontent.invalid/nope/catalog.json");
        var result = await offline.LoadAsync();

        Line($"источник       {result.Source}");
        Line($"сообщение      {result.Warning}");
        Check(result.Source == CatalogSource.Cache, "список взят из сохранённой копии");
        Check(result.Catalog.Games.Count > 0, $"игр показано: {result.Catalog.Games.Count}");
        Check(result.Warning is { Length: > 0 }, "пользователю сказано, что список несвежий");

        // А если копии ещё нет — не падаем, просто честно пусто.
        var bare = Path.Combine(workDir, "library-empty");
        var lib2 = new LibraryService();
        lib2.Open(bare);
        var cold = await new CatalogService(lib2, "https://raw.githubusercontent.invalid/nope/catalog.json").LoadAsync();
        Check(cold.Source == CatalogSource.None, "без копии и без сети — состояние None, а не исключение");
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
        TestOfflinePlayability(library, pick);
        TestActuallyRuns(install, pick, gameDir);

        await TestChannelSwitch(library, install, pick, lookups[pick.Id]);

        // --keep оставляет игру установленной: нужно, чтобы потом посмотреть
        // на карточку в состоянии «установлена».
        if (_keepInstalled)
            Line("  [--keep] удаление пропущено, игра осталась на диске");
        else
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

    /// <summary>«Играть» не имеет права зависеть от GitHub: моделируем полное
    /// отсутствие сведений о релизах.</summary>
    private static void TestOfflinePlayability(LibraryService library, CatalogEntry pick)
    {
        Head("Запуск без сети");

        var status = GameStatus.Compute(pick, Channels.Dev, library.GetInstalled(pick.Id), remote: null);

        Check(status.CanPlay, "установленную игру можно запустить без ответа от GitHub");
        Check(status.PrimaryAction == "Играть", $"кнопка: «{status.PrimaryAction}»");
        Check(status.State == GameState.InstalledUnknown, $"состояние: {status.StateCaption}");
        Check(status.Note is { Length: > 0 }, $"честно сказано: {status.Note}");
    }

    /// <summary>Проверка всей цепочки целиком: скачали, распаковали — идёт ли.</summary>
    private static void TestActuallyRuns(InstallService install, CatalogEntry pick, string gameDir)
    {
        Head("Игра запускается");

        var files = Directory.GetFiles(gameDir, "*", SearchOption.AllDirectories);
        Line($"файлов в папке {files.Length}");
        Line($"объём          {files.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0:0.00} МБ");

        System.Diagnostics.Process? process = null;
        try
        {
            process = install.Launch(pick);
            Check(true, $"процесс стартовал, pid {process.Id}");

            // Даём окну подняться и убеждаемся, что она не упала сразу же.
            var died = process.WaitForExit(4000);
            Check(!died, died
                ? $"процесс завершился сам, код {process.ExitCode}"
                : "через 4 секунды процесс жив — игра пошла");

            Check(process.WorkingSetMemory() > 0, "процесс занял память");
        }
        catch (InstallException ex)
        {
            Fail($"запуск не удался: {ex.Message}");
        }
        finally
        {
            try
            {
                if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Уже умер сам — не беда.
            }
            process?.Dispose();
        }
    }

    /// <summary>Разбор build.json во всех формах, которые встречаются в
    /// живых релизах: поля добавлялись со временем, а уже выпущенные архивы
    /// не переписываются и лежат как есть.</summary>
    private static void TestBuildInfoForms()
    {
        Head("Формы build.json");

        // Форма до появления version: только game, channel, commit, built.
        const string oldest = """
            {"game":"Strelalka","channel":"dev",
             "commit":"496ad05f1c2a3b4d5e6f708192a3b4c5d6e7f809",
             "built":"2026-08-31T08:58:30+00:00"}
            """;

        // Стабильный релиз v0.1.0: version уже есть, tag ещё нет,
        // а channel содержит тег вместо названия канала.
        const string stable = """
            {"version":"v0.1.0","game":"Strelalka","channel":"v0.1.0",
             "commit":"70449948e52fffc0efca8c3c9576ca0c2046e1b3",
             "built":"2026-08-31T11:44:49+00:00"}
            """;

        // Сборки после e20a5c4: channel честный, добавился tag.
        const string current = """
            {"game":"Strelalka","channel":"dev","tag":"dev",
             "version":"v0.1.0-1-ge20a5c4",
             "commit":"e20a5c4b9f9577143acc884cdd2d9045043fb437",
             "built":"2026-08-31T11:54:06+00:00"}
            """;

        var a = Parse(oldest);
        Check(a is not null && a.Version is null, "старая форма разбирается, version нет");
        Check(a?.Display == "496ad05", $"без version показан короткий commit: «{a?.Display}»");

        var b = Parse(stable);
        Check(b?.Version == "v0.1.0", $"v0.1.0: version = «{b?.Version}»");
        Check(b?.Display == "v0.1.0", "показывается version, а не commit");

        var c = Parse(current);
        Check(c?.Version == "v0.1.0-1-ge20a5c4", $"новая форма: version = «{c?.Version}»");
        Check(c?.Display == "v0.1.0-1-ge20a5c4", "git describe показывается целиком, без разбора");
        Check(c?.Commit == "e20a5c4b9f9577143acc884cdd2d9045043fb437", "commit прочитан полностью");

        // Лишние поля не должны ронять разбор: их будут добавлять и дальше.
        Check(Parse("""{"commit":"abc1234","чего-то-новое":42}""")?.Display == "abc1234",
            "незнакомые поля не мешают");

        // И то же самое на уровне карточки: показ идёт от version, а без
        // него — от коммита.
        var entry = new CatalogEntry { Id = "x", Name = "X", Repo = "r/r", Exe = "x.exe" };
        Check(Card(entry, version: null, commit: "496ad05f1c2a3b4d").InstalledVersion == "496ad05",
            "в карточке без version показан короткий commit");
        Check(Card(entry, version: "v0.1.0-1-ge20a5c4", commit: "e20a5c4b").InstalledVersion
                  == "v0.1.0-1-ge20a5c4",
            "в карточке с version показана она");

        static BuildInfo? Parse(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<BuildInfo>(json, Json.Local);

        static GameStatus Card(CatalogEntry entry, string? version, string commit) =>
            GameStatus.Compute(entry, Channels.Dev,
                new InstalledGame { Id = entry.Id, Channel = Channels.Dev, Version = version, Commit = commit },
                null);
    }

    /// <summary>Переключение канала. Проверять его стало на чём: у витрины
    /// появился стабильный релиз рядом с движущимся dev.</summary>
    private static async Task TestChannelSwitch(
        LibraryService library, InstallService install, CatalogEntry pick, ReleaseLookup lookup)
    {
        Head("Переключение канала");

        var stable = lookup.For(Channels.Stable);
        var dev = lookup.For(Channels.Dev);

        if (stable is null)
        {
            Line("стабильных сборок нет — переключать не на что");
            return;
        }

        Line($"stable         тег {stable.Tag}, {stable.AssetName}");
        Line($"dev            тег {dev?.Tag ?? "—"}");

        // Сейчас на диске dev. Смена канала обязана читаться как обновление,
        // а не как «уже свежее»: это тоже замена содержимого папки.
        var status = GameStatus.Compute(pick, Channels.Stable, library.GetInstalled(pick.Id), stable);
        Check(status.State == GameState.UpdateAvailable,
            $"выбран другой канал → {status.StateCaption} / кнопка «{status.PrimaryAction}»");
        Check(status.Note is not null, $"причина названа: {status.Note}");

        var installed = await install.InstallAsync(pick, stable);
        Line($"поставлено     version «{installed.Version ?? "—"}», коммит {installed.Commit?[..7]}");

        Check(installed.Channel == Channels.Stable, "в state.json записан канал лаунчера");
        Check(installed.Version is { Length: > 0 }, "в build.json есть поле version");

        status = GameStatus.Compute(pick, Channels.Stable, library.GetInstalled(pick.Id), stable);

        // Главная проверка этого раздела. В build.json стабильной сборки
        // channel равен тегу («v0.1.0»). Если бы лаунчер брал канал оттуда,
        // он решил бы, что канал сменился, сразу после установки.
        Check(status.State == GameState.UpToDate,
            $"после установки stable: {status.StateCaption} — channel из build.json не подхвачен");

        Check(status.InstalledVersion == installed.Version,
            $"установленная версия показана как version: «{status.InstalledVersion}»");
        Check(status.AvailableVersion == stable.Tag,
            $"доступная версия показана тегом: «{status.AvailableVersion}»");
        Check(File.Exists(Path.Combine(library.GameDir(pick.Id), pick.Exe)),
            $"{pick.Exe} на месте после смены канала");

        if (dev is null) return;

        // И обратно, чтобы дальше всё шло от того же состояния, что и раньше.
        var back = GameStatus.Compute(pick, Channels.Dev, library.GetInstalled(pick.Id), dev);
        Check(back.State == GameState.UpdateAvailable,
            $"возврат на dev → {back.StateCaption} / кнопка «{back.PrimaryAction}»");

        var again = await install.InstallAsync(pick, dev);
        Line($"вернули        version «{again.Version ?? "—"}»");

        var final = GameStatus.Compute(pick, Channels.Dev, library.GetInstalled(pick.Id), dev);
        Check(final.State == GameState.UpToDate, $"снова dev: {final.StateCaption}");
        Check(final.InstalledVersion == again.Version,
            $"показана version dev-сборки: «{final.InstalledVersion}»");
        Check(final.AvailableVersion != Channels.Dev,
            $"в dev показан не тег, а коммит: «{final.AvailableVersion}»");
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

    // ── обновление самого лаунчера ───────────────────────────────────────

    private static async Task TestSelfUpdate(
        LibraryService library, ReleaseService releases, string[] args)
    {
        Head("Обновление лаунчера");

        Line($"своя версия    {AppVersion.Display}" +
             (AppVersion.IsDevBuild ? " (сборка из исходников)" : ""));

        // Сравнение версий таблицей: пересобирать лаунчер под каждый номер,
        // чтобы проверить «новее или нет», было бы издевательством.
        var cases = new (string Current, string Tag, bool Expected, string Why)[]
        {
            ("0.1.0", "v0.2.0",      true,  "следующая минорная новее"),
            ("0.1.0", "v0.1.1",      true,  "следующая патч новее"),
            ("0.1.0", "v1.0.0",      true,  "следующая мажорная новее"),
            ("0.1.0", "v0.1.0",      false, "та же самая не новее"),
            ("0.2.0", "v0.1.9",      false, "предыдущая не новее"),
            ("0.1.0", "0.2.0",       true,  "тег без «v» тоже понимается"),
            ("0.1.0", "v0.2.0-beta", true,  "суффикс не мешает сравнению"),
            ("0.1.0", "dev",         false, "движущийся тег не версия"),
            ("0.1.0", "",            false, "пустой тег не версия"),
        };

        foreach (var (current, tag, expected, why) in cases)
            Check(SelfUpdateService.IsNewer(current, tag) == expected,
                $"{current} против «{tag}» → {(expected ? "новее" : "не новее")}: {why}");

        // Имена соседей: от них зависит, что мы удалим при уборке.
        var sample = Path.Combine("C:", "игры", "GameLauncher.exe");
        Check(SelfUpdateService.Sibling(sample, ".new")
                  .EndsWith("GameLauncher.new.exe", StringComparison.Ordinal),
            "имя временного файла собирается от текущего exe");
        Check(SelfUpdateService.Sibling(Path.Combine("C:", "игры", "Мой лаунчер.exe"), ".old")
                  .EndsWith("Мой лаунчер.old.exe", StringComparison.Ordinal),
            "переименованный exe тоже обслуживается");

        // Живой запрос к своему же репозиторию.
        var service = new SelfUpdateService(library, releases);
        var info = await service.CheckAsync();

        Line($"состояние      {info.State}");
        if (info.Message is not null) Line($"сообщение      {info.Message}");

        if (AppVersion.IsDevBuild)
        {
            Check(info.State == SelfUpdateState.DevBuild,
                "сборка из исходников не предлагает подменить себя релизной");
            Check(info.Build is null, "и запроса на неё не тратит");
        }
        else
        {
            Check(info.State is SelfUpdateState.Available or SelfUpdateState.UpToDate,
                $"релиз лаунчера найден, последний {info.Latest ?? "—"}");
        }

        // Отдельно — то, что видно и на dev-сборке: как выглядит наш релиз.
        var lookup = await releases.GetAsync(SelfUpdateService.Repo);
        var build = lookup.For(Channels.Stable);

        if (build is null)
        {
            Fail("В своём репозитории не видно ни одного релиза.");
            return;
        }

        Line($"последний      {build.Tag}, {build.AssetName}, {build.Size / 1048576.0:0.0} МБ");
        Check(SelfUpdateService.TryParseTag(build.Tag) is not null,
            $"тег «{build.Tag}» разбирается в номер версии");
        Check(build.HasStrongFingerprint,
            "у файла есть sha256 — скачанное будет с чем сверить");

        if (args.Contains("--selfupdate")) await ApplySelfUpdate(service, build);
    }

    /// <summary>Настоящая подмена файла — только по явному «--selfupdate».
    /// Процесс после неё завершается: заменять себя, продолжая работать,
    /// Windows не даёт, в этом вся суть механизма.</summary>
    private static async Task ApplySelfUpdate(SelfUpdateService service, Model.RemoteBuild build)
    {
        Head("Подмена файла лаунчера");
        Line($"текущий exe    {Environment.ProcessPath}");
        Line($"ставим         {build.Tag}");

        try
        {
            var replacer = await service.ApplyAsync(build);
            Check(!replacer.HasExited || replacer.ExitCode == 0, "процесс подмены запущен");
            Line("");
            Line("Выхожу, чтобы он мог переписать файл.");

            _log?.Flush();
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        catch (InstallException ex)
        {
            Fail($"Подмена не начата: {ex.Message}");
        }
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

internal static class ProcessExtensions
{
    /// <summary>Занятая процессом память; 0, если он уже успел завершиться.</summary>
    public static long WorkingSetMemory(this System.Diagnostics.Process process)
    {
        try { process.Refresh(); return process.WorkingSet64; }
        catch (InvalidOperationException) { return 0; }
    }
}
