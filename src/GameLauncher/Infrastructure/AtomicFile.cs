namespace GameLauncher.Infrastructure;

/// <summary>Запись файла так, чтобы обрыв на середине не оставил огрызок:
/// пишем во временный рядом, затем заменяем.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, contents, new System.Text.UTF8Encoding(false));

        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* уборка не обязана удаться */ }
    }
}
