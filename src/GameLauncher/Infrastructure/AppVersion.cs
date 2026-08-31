using System.Reflection;

namespace GameLauncher.Infrastructure;

/// <summary>Версия самого лаунчера.
///
/// Проставляется из тега при сборке в GitHub Actions; при локальной сборке
/// остаётся 0.0.0 — это и есть признак «собрано не релизом».
///
/// Нужна самообновлению: сравнить себя с последним релизом можно, только
/// если есть что сравнивать. Игры так не проверяются — у них отпечаток
/// ассета, потому что тег dev не меняется никогда.</summary>
public static class AppVersion
{
    /// <summary>«0.1.0». SDK дописывает к информационной версии «+хэш» —
    /// для показа он лишний.</summary>
    public static string Display { get; } = Read();

    /// <summary>Собрано вручную, а не по тегу.</summary>
    public static bool IsDevBuild => Display == "0.0.0";

    private static string Read()
    {
        var raw = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";

        var plus = raw.IndexOf('+');
        return plus > 0 ? raw[..plus] : raw;
    }
}
