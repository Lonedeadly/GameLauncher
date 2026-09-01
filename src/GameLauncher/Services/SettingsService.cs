using System.Text.Json;
using GameLauncher.Infrastructure;
using GameLauncher.Model;

namespace GameLauncher.Services;

/// <summary>Конфиг рядом с exe; если туда писать нельзя — в %LOCALAPPDATA%.</summary>
public sealed class SettingsService
{
    private const string FileName = "launcher.config.json";
    public const int SupportedSchema = 1;

    public string ConfigPath { get; }
    public bool IsPortable { get; }
    public LauncherSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        if (AppPaths.IsWritable(AppPaths.ExeDirectory))
        {
            ConfigPath = Path.Combine(AppPaths.ExeDirectory, FileName);
            IsPortable = true;
        }
        else
        {
            ConfigPath = Path.Combine(AppPaths.LocalAppData, FileName);
            IsPortable = false;
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Settings = new LauncherSettings();
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            Settings = JsonSerializer.Deserialize<LauncherSettings>(json, Json.Local) ?? new LauncherSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый или недоступный конфиг не должен мешать запуску: считаем,
            // что настроек нет, и пойдём через окно первого запуска.
            Settings = new LauncherSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(Settings, Json.Local));
    }
}
