namespace GameLauncher.Infrastructure;

/// <summary>Где мы лежим и куда нам можно писать.</summary>
public static class AppPaths
{
    /// <summary>Папка с exe. Именно <c>Environment.ProcessPath</c>, а не
    /// <c>Assembly.Location</c>: у single-file сборки последний пуст.</summary>
    public static string ExeDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public static string LocalAppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameLauncher");

    /// <summary>Проверка записи делом, а не рассуждением о правах: создаём и
    /// удаляем файл. В Program Files с нашим манифестом UAC-виртуализация
    /// выключена, поэтому проба честно провалится.</summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, $".gl-probe-{Guid.NewGuid():N}");
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSameDirectory(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string child, string parent)
    {
        var p = Normalize(parent);
        var c = Normalize(child);
        return c.Equals(p, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
