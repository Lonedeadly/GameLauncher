using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Infrastructure;

/// <summary>Настройки сериализации. Их две, потому что чужой JSON и свой
/// живут по разным правилам именования.</summary>
public static class Json
{
    /// <summary>Ответы GitHub API: tag_name, browser_download_url и т.п.</summary>
    public static readonly JsonSerializerOptions GitHub = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Наши файлы: catalog.json, state.json, launcher.config.json,
    /// а также build.json внутри zip — они все в camelCase.</summary>
    public static readonly JsonSerializerOptions Local = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
