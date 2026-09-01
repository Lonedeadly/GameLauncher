using System.Net.Http;

namespace GameLauncher.Infrastructure;

/// <summary>Откуда лаунчер берёт всё: список игр, версии и сами сборки.
///
/// Раньше это был GitHub, и упирались мы в анонимный лимит — 60 запросов
/// в час на адрес, которых на отладке хватало на полчаса. Здесь лимита нет,
/// поэтому спрашивать можно так часто, как осмысленно человеку.
///
/// Портов три. Основной — 443, а 8443 и 2053 живут потому, что у провайдера
/// 443 иногда отфильтрован; сервер слушает все три одинаково. Удачный порт
/// запоминается на время работы: перебирать его заново на каждом запросе
/// незачем, а если он перестанет отвечать — переберём снова.</summary>
public static class Origin
{
    public const string Host = "game.lonedeadly.ru";

    /// <summary>Порядок неслучаен: сначала обычный, запасные — следом.</summary>
    private static readonly int[] Ports = [443, 8443, 2053];

    private static volatile int _port = 443;

    /// <summary>Порт, который отвечал в последний раз.</summary>
    public static int Port => _port;

    public static string Url(string path) =>
        _port == 443
            ? $"https://{Host}/{path.TrimStart('/')}"
            : $"https://{Host}:{_port}/{path.TrimStart('/')}";

    /// <summary>GET с перебором портов.
    ///
    /// Перебор только на отказе соединения. Ответ 404 — это ответ: сервер
    /// на связи и говорит, что такого файла нет. Пробовать из-за него
    /// другие порты значило бы получить те же три 404 и втрое дольше.</summary>
    public static async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        // Начинаем с того, что работал; остальные — как запасные.
        var order = Ports.OrderByDescending(p => p == _port).ToArray();

        Exception? last = null;

        foreach (var port in order)
        {
            var url = port == 443
                ? $"https://{Host}/{path.TrimStart('/')}"
                : $"https://{Host}:{port}/{path.TrimStart('/')}";

            try
            {
                var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                _port = port;
                return response;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Свой таймаут, а не отмена снаружи: порт молчит, пробуем следующий.
                last = ex;
            }
        }

        throw last ?? new HttpRequestException($"{Host} не отвечает ни на одном порту.");
    }
}
