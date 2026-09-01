using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Infrastructure;

/// <summary>Настройки сериализации.
///
/// Она одна: с переездом на свою раздачу чужого JSON не осталось — все
/// файлы, которые читает лаунчер, пишем мы сами.</summary>
public static class Json
{
    /// <summary>Наши файлы: catalog.json, meta/&lt;id&gt;.json, state.json,
    /// launcher.config.json, а также build.json внутри zip — все camelCase.</summary>
    public static readonly JsonSerializerOptions Local = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
