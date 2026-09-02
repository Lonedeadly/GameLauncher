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
            await TestGameWithoutBuilds(library, releases);
            await TestCatalogOffline(library, workDir, catalog);

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

        // Раздача не ответила. Это тоже проверка: упасть было нельзя, и не
        // упали. Дальше берём каталог из рабочей копии репозитория — если
        // она рядом. У друга её нет, и тогда проверять нечего: об этом
        // говорим прямо, а не притворяемся, что прогон удался.
        Check(result.Source is CatalogSource.None or CatalogSource.Cache,
            "недоступный каталог не уронил лаунчер");

        var localPath = FindRepoCatalog();
        if (localPath is null)
        {
            Fail("Раздача не ответила, а рабочей копии репозитория рядом нет — " +
                 "проверять дальше нечего.");
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
        ReleaseService releases, Catalog catalog, LibraryService library)
    {
        Head("Сборки");
        var lookups = new Dictionary<string, ReleaseLookup>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in catalog.Games)
        {
            var lookup = await releases.GetAsync(game.Id);
            lookups[game.Id] = lookup;

            Line($"{game.Id}");
            Line($"  источник     {lookup.Source}");
            if (lookup.Warning is not null) Line($"  замечание    {lookup.Warning}");

            if (lookup.Build is null)
            {
                Line("  сборок нет");
                continue;
            }

            var build = lookup.Build;
            Line($"  версия       {build.Version}");
            Line($"  файл         {build.FileName}, {build.Size / 1024} КБ");
            Line($"  путь         {build.RelativePath}");
            Line($"  отпечаток    {build.Fingerprint}");

            Check(build.Sha256.Length == 64, "sha256 получен до скачивания");
            Check(build.Size > 0, "размер известен до скачивания");
            Check(build.RelativePath.StartsWith($"files/{game.Id}/", StringComparison.Ordinal),
                "путь к архиву лежит в папке своей игры");
            Check(build.FileName.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase),
                "имя архива по контракту");
            Check(build.DownloadUrl.StartsWith($"https://{Origin.Host}", StringComparison.Ordinal),
                $"качать будем с раздачи: {build.DownloadUrl}");
        }

        // Повторный заход в те же полминуты обязан прийти из кэша: щёлканье
        // по списку не должно превращаться в поток запросов.
        foreach (var game in catalog.Games)
        {
            var again = await releases.GetAsync(game.Id);
            Check(again.Source == ReleaseSource.Cache,
                $"{game.Id}: повторный опрос обслужен кэшем ({again.Source})");
        }

        // А нажатие кнопки человеком обязано этот предел обойти: отвечать
        // кэшем на осознанное «проверить» — значит врать.
        var forced = await releases.GetAsync(catalog.Games[0].Id, force: true);
        Check(forced.Source == ReleaseSource.Network,
            $"«проверить» идёт в сеть, минуя предел ({forced.Source})");

        // А это уже кэш С ДИСКА: новый экземпляр сервиса ничего не помнит,
        // как при следующем запуске лаунчера. Путь через файл иначе вообще
        // не проверялся бы — первый прогон всегда идёт в сеть.
        var fresh = new ReleaseService(library);
        var first = catalog.Games[0];
        var fromDisk = await fresh.GetAsync(first.Id);
        Check(fromDisk.Source == ReleaseSource.Cache, $"кэш прочитан с диска: {fromDisk.Source}");
        Check(fromDisk.Build?.Fingerprint == lookups[first.Id].Build?.Fingerprint,
            "с диска поднялось то же самое, что пришло из сети");

        return lookups;
    }

    // ── 4б. игра без сборок ──────────────────────────────────────────────

    /// <summary>Игра, которую ещё ни разу не собирали, обязана остаться в
    /// списке — с внятным состоянием и без кнопки «Установить».
    ///
    /// Раньше для этого искали витрину без релизов среди настоящих. Теперь
    /// проверка честнее: спрашиваем раздачу про заведомо несуществующее имя
    /// и смотрим, что 404 понят как ответ, а не как поломка связи.</summary>
    private static async Task TestGameWithoutBuilds(LibraryService library, ReleaseService releases)
    {
        Head("Игра без сборок");

        var entry = new CatalogEntry
        {
            Id = "no-such-game",
            Name = "Игра, которой нет",
            Exe = "nothing.exe",
        };

        var lookup = await releases.GetAsync(entry.Id);

        Line($"источник       {lookup.Source}");
        if (lookup.Warning is not null) Line($"замечание      {lookup.Warning}");

        Check(lookup.Source == ReleaseSource.Network, "404 — это ответ раздачи, а не обрыв связи");
        Check(lookup.Build is null, "сборки нет, и это не ошибка");

        var status = GameStatus.Compute(entry, library.GetInstalled(entry.Id), lookup.Build);
        Check(status.State == GameState.NotInstalled, $"состояние: {status.StateCaption}");
        Check(!status.CanInstallOrUpdate, "кнопки «Установить» нет — ставить нечего");
        Check(!status.CanPlay, "кнопки «Играть» нет");
        Check(status.Note is { Length: > 0 }, $"объяснение показано: {status.Note}");
        Check(status.AvailableVersion == "—", "доступная версия показана прочерком");
    }

    // ── 4в. каталог без сети ─────────────────────────────────────────────

    /// <summary>Каталог недоступен — показываем последний известный список,
    /// а не пустое окно.</summary>
    private static async Task TestCatalogOffline(
        LibraryService library, string workDir, Catalog known)
    {
        Head("Каталог недоступен");

        // Сохранённую копию пишем из того каталога, который уже подняли
        // выше. Раньше сюда копировался catalog.json из рабочей копии
        // репозитория — и это разваливалось ровно там, где проверка нужнее
        // всего: у друга exe лежит сам по себе, никакого репозитория рядом
        // нет, и самопроверка падала вместо того, чтобы что-то проверить.
        var cachePath = Path.Combine(library.CacheDir, "catalog.json");
        Directory.CreateDirectory(library.CacheDir);
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(known, Json.Local));

        // Заведомо несуществующий хост — сеть при этом отключать не нужно.
        var offline = new CatalogService(library, "https://game.lonedeadly.invalid/catalog.json");
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
        var cold = await new CatalogService(lib2, "https://game.lonedeadly.invalid/catalog.json").LoadAsync();
        Check(cold.Source == CatalogSource.None, "без копии и без сети — состояние None, а не исключение");
    }

    // ── 5. установка, обновление, удаление ───────────────────────────────

    private static async Task TestInstallCycle(
        LibraryService library, ReleaseService releases, Catalog catalog,
        Dictionary<string, ReleaseLookup> lookups)
    {
        Head("Установка");

        var pick = catalog.Games.FirstOrDefault(g =>
            lookups.TryGetValue(g.Id, out var l) && l.Build is not null);

        if (pick is null)
        {
            Fail("Ни у одной игры нет сборки — цикл установки не проверить.");
            return;
        }

        var remote = lookups[pick.Id].Build!;
        Line($"игра           {pick.Name} ({pick.Id}), версия {remote.Version}");

        var status = GameStatus.Compute(pick, library.GetInstalled(pick.Id), remote);
        Check(status.State == GameState.NotInstalled, $"до установки: {status.StateCaption} / кнопка «{status.PrimaryAction}»");

        var install = new InstallService(library);
        var phases = new List<InstallPhase>();
        var progress = new Progress<InstallProgress>(p =>
        {
            if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase);
        });

        var installed = await install.InstallAsync(pick, remote, progress);

        Line($"поставлено     version «{installed.Version ?? "—"}», коммит {Short(installed.Commit)}, " +
             $"{installed.SizeOnDisk / 1024} КБ на диске");
        Line($"фазы прогресса {string.Join(" → ", phases)}");

        var gameDir = library.GameDir(pick.Id);
        Check(File.Exists(Path.Combine(gameDir, pick.Exe)), $"{pick.Exe} на месте");
        Check(File.Exists(Path.Combine(gameDir, "build.json")), "build.json на месте");
        Check(installed.Fingerprint == remote.Fingerprint, "отпечаток записан в state.json");
        Check(File.Exists(library.StatePath), "state.json создан");

        Line("содержимое папки игры:");
        foreach (var e in Directory.EnumerateFileSystemEntries(gameDir).OrderBy(x => x))
            Line($"  {(Directory.Exists(e) ? "[папка] " : "        ")}{Path.GetFileName(e)}");

        // Коммит из build.json обязан совпасть с тем, что раздача написала в
        // meta — это перекрёстная проверка контракта: один и тот же прогон
        // сборки писал оба файла, и разойтись они не имеют права.
        if (remote.Commit is { Length: >= 7 })
            Check(string.Equals(installed.Commit, remote.Commit, StringComparison.OrdinalIgnoreCase),
                $"коммит из build.json ({Short(installed.Commit)}) совпал с meta ({remote.ShortCommit})");

        Check(installed.Version == remote.Version,
            $"version из build.json совпала с meta: «{installed.Version}»");

        status = GameStatus.Compute(pick, library.GetInstalled(pick.Id), remote);
        Check(status.State == GameState.UpToDate, $"после установки: {status.StateCaption} / кнопка «{status.PrimaryAction}»");
        Check(status.InstalledVersion == status.AvailableVersion,
            $"установленная и доступная версии сошлись: «{status.InstalledVersion}»");

        Check(library.CacheDir.StartsWith(library.Root), "кэш лежит внутри папки библиотеки");
        Check(!Directory.EnumerateFileSystemEntries(library.TempDir).Any(), "временная папка убрана за собой");

        await TestReplaceSemantics(library, install, pick, remote);
        TestOfflinePlayability(library, pick);
        TestActuallyRuns(install, pick, gameDir);

        TestUpdateDetection(library, pick, remote);

        // --keep оставляет игру установленной: нужно, чтобы потом посмотреть
        // на карточку в состоянии «установлена».
        if (_keepInstalled)
            Line("  [--keep] удаление пропущено, игра осталась на диске");
        else
            TestUninstall(library, install, pick, gameDir);
    }

    private static string Short(string? commit) =>
        commit is { Length: >= 7 } ? commit[..7] : commit ?? "—";

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

    /// <summary>«Играть» не имеет права зависеть от раздачи: моделируем
    /// полное отсутствие сведений о сборках.</summary>
    private static void TestOfflinePlayability(LibraryService library, CatalogEntry pick)
    {
        Head("Запуск без сети");

        var status = GameStatus.Compute(pick, library.GetInstalled(pick.Id), remote: null);

        Check(status.CanPlay, "установленную игру можно запустить без ответа от раздачи");
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

            // Признак «игра пошла» — появилось окно, а не «процесс жив через
            // N секунд». Секундомер срывался: игра, которая открыла окно и
            // сама же закрылась за три секунды, считалась провалом, хотя
            // лаунчер своё дело сделал.
            var window = WaitForWindow(process, TimeSpan.FromSeconds(8));
            if (window)
                Check(true, "окно игры появилось");
            else if (process.HasExited)
                Fail($"процесс завершился, не показав окна, код {process.ExitCode}");
            else
                Check(true, "окна не видно, но процесс жив через 8 секунд — считаем запущенной");
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

    /// <summary>Ждать главное окно процесса. Опрос, а не WaitForInputIdle:
    /// тот возвращается по первому простою очереди сообщений, что бывает и
    /// до окна, и не бывает вовсе у игры без обычного цикла сообщений.</summary>
    private static bool WaitForWindow(System.Diagnostics.Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited) return false;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            Thread.Sleep(100);
        }
        return false;
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
        var entry = new CatalogEntry { Id = "x", Name = "X", Exe = "x.exe" };
        Check(Card(entry, version: null, commit: "496ad05f1c2a3b4d").InstalledVersion == "496ad05",
            "в карточке без version показан короткий commit");
        Check(Card(entry, version: "v0.1.0-1-ge20a5c4", commit: "e20a5c4b").InstalledVersion
                  == "v0.1.0-1-ge20a5c4",
            "в карточке с version показана она");

        static BuildInfo? Parse(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<BuildInfo>(json, Json.Local);

        static GameStatus Card(CatalogEntry entry, string? version, string commit) =>
            GameStatus.Compute(entry,
                new InstalledGame { Id = entry.Id, Version = version, Commit = commit },
                null);
    }

    /// <summary>Как лаунчер узнаёт, что вышло обновление.
    ///
    /// Сравнение идёт по сумме архива, а не по строке версии, и проверить
    /// это надо именно так: подсунуть сборку с ТОЙ ЖЕ версией, но другим
    /// содержимым, и убедиться, что обновление всё равно замечено. Ждать
    /// ради этого настоящего пуша в main нельзя — метаданные подделываем,
    /// ничего при этом не скачивая.</summary>
    private static void TestUpdateDetection(
        LibraryService library, CatalogEntry pick, RemoteBuild current)
    {
        Head("Обнаружение обновления");

        var installed = library.GetInstalled(pick.Id);
        Check(installed is not null, "игра на диске — есть от чего отталкиваться");
        if (installed is null) return;

        // То же самое, что стоит.
        var same = GameStatus.Compute(pick, installed, current);
        Check(same.State == GameState.UpToDate, $"та же сборка: {same.StateCaption}");

        // Новый коммит: и версия другая, и сумма другая — обычный случай.
        var next = current with
        {
            Version = current.Version + "-next",
            Commit = new string('a', 40),
            Sha256 = new string('b', 64),
            RelativePath = $"files/{pick.Id}/aaaaaaa/{current.FileName}",
        };

        var update = GameStatus.Compute(pick, installed, next);
        Check(update.State == GameState.UpdateAvailable,
            $"новая сборка: {update.StateCaption} / кнопка «{update.PrimaryAction}»");
        Check(update.CanInstallOrUpdate, "кнопка обновления доступна");
        Check(update.CanPlay, "и играть в старую версию по-прежнему можно");
        Check(update.AvailableVersion == next.Version,
            $"доступная версия показана: «{update.AvailableVersion}»");

        // А это то, ради чего сравнение по сумме и делалось: версия та же,
        // содержимое другое. По строке версии обновление было бы пропущено.
        var rebuilt = current with { Sha256 = new string('c', 64) };
        Check(GameStatus.Compute(pick, installed, rebuilt).State == GameState.UpdateAvailable,
            "пересборка с той же версией, но другим архивом — тоже обновление");

        // И обратное: сумма та же, версия в meta переписана. Скачивать
        // заново нечего, файл на диске уже этот.
        var relabeled = current with { Version = "v9.9.9" };
        Check(GameStatus.Compute(pick, installed, relabeled).State == GameState.UpToDate,
            "другая надпись версии при той же сумме обновлением не считается");
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

        // Имена соседей: от них зависит, что мы удалим при уборке.
        var sample = Path.Combine("C:", "игры", "GameLauncher.exe");
        Check(SelfUpdateService.Sibling(sample, ".new")
                  .EndsWith("GameLauncher.new.exe", StringComparison.Ordinal),
            "имя временного файла собирается от текущего exe");
        Check(SelfUpdateService.Sibling(Path.Combine("C:", "игры", "Мой лаунчер.exe"), ".old")
                  .EndsWith("Мой лаунчер.old.exe", StringComparison.Ordinal),
            "переименованный exe тоже обслуживается");

        // Живой запрос к своей же раздаче.
        var service = new SelfUpdateService(library, releases);
        var info = await service.CheckAsync();

        Line($"состояние      {info.State}");
        if (info.Message is not null) Line($"сообщение      {info.Message}");

        if (AppVersion.IsDevBuild)
        {
            Check(info.State == SelfUpdateState.DevBuild,
                "сборка из исходников не предлагает подменить себя раздачной");
            Check(info.Build is null, "и в сеть за этим не ходит");
        }
        else
        {
            Check(info.State is SelfUpdateState.Available or SelfUpdateState.UpToDate,
                $"сборка лаунчера найдена, последняя {info.Latest ?? "—"}");
        }

        // Отдельно — то, что видно и на сборке из исходников: как выглядит
        // наш собственный файл на раздаче.
        var lookup = await releases.GetAsync(SelfUpdateService.Id);
        var build = lookup.Build;

        if (build is null)
        {
            Fail($"На раздаче не видно сборки лаунчера (meta/{SelfUpdateService.Id}.json).");
            return;
        }

        Line($"последняя      {build.Version}, {build.FileName}, {build.Size / 1048576.0:0.0} МБ");
        Check(build.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            "раздаётся один exe, а не архив");
        Check(build.Sha256.Length == 64,
            "у файла есть sha256 — скачанное будет с чем сверить");
        Check(build.Size > 20 * 1024 * 1024,
            $"размер похож на self-contained сборку: {build.Size / 1048576.0:0.0} МБ");

        if (args.Contains("--selfupdate")) await ApplySelfUpdate(service, build);
    }

    /// <summary>Настоящая подмена файла — только по явному «--selfupdate».
    /// Процесс после неё завершается: заменять себя, продолжая работать,
    /// Windows не даёт, в этом вся суть механизма.</summary>
    private static async Task ApplySelfUpdate(SelfUpdateService service, Model.RemoteBuild build)
    {
        Head("Подмена файла лаунчера");
        Line($"текущий exe    {Environment.ProcessPath}");
        Line($"ставим         {build.Version}");

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
