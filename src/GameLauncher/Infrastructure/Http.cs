using System.Net;
using System.Net.Http;

namespace GameLauncher.Infrastructure;

/// <summary>Один HttpClient на всё приложение.</summary>
public static class Http
{
    /// <summary>Представляемся: в логах раздачи должно быть видно, кто
    /// пришёл, — иначе непонятно, лаунчер это или чей-то сканер.</summary>
    public const string UserAgent = "GameLauncher (+https://github.com/Lonedeadly/GameLauncher)";

    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        // Бесконечный таймаут намеренно: HttpClient.Timeout режет операцию
        // целиком, включая чтение тела, и оборвал бы длинную закачку игры на полпути.
        // Ограничение по времени задаёт вызывающий через CancellationToken.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>Токен, который отменится сам через <paramref name="timeout"/>.</summary>
    public static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}
