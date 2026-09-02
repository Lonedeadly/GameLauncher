using System.Net.Http;
using System.Security.Cryptography;

namespace GameLauncher.Infrastructure;

/// <summary>Скачивание в файл с подсчётом SHA-256 по ходу дела.
///
/// Хэш считается на лету, а не вторым проходом по готовому файлу: иначе
/// полсотни мегабайт пришлось бы прочитать с диска ещё раз.
///
/// Общий код для игр и для самого лаунчера — обновляя себя, лаунчер должен
/// проверять скачанное ровно так же строго, как проверяет чужие архивы.</summary>
public static class Downloader
{
    /// <summary>Сколько ждать следующего байта. Предел на тишину, а не на
    /// всю закачку: шестьдесят мегабайт по медленной связи идут долго и
    /// честно, а оборвать надо тот случай, когда сервер замолчал совсем.
    /// Иначе полоса стояла бы вечно — у HttpClient таймаут отключён
    /// намеренно, он резал бы длинные закачки на середине.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <returns>SHA-256 скачанного, строчными шестнадцатеричными.</returns>
    /// <exception cref="HttpRequestException">Сервер ответил отказом или замолчал.</exception>
    /// <exception cref="OperationCanceledException">Отменено вызывающим.</exception>
    public static async Task<string> ToFileAsync(
        string url,
        string path,
        long expectedSize,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        // Один источник на всё: заводится заново перед каждым чтением.
        // Отмена снаружи проходит сквозь него как есть.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            idle.CancelAfter(IdleTimeout);
            using var response = await Http.Client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, idle.Token);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"{(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            var total = response.Content.Headers.ContentLength ?? expectedSize;

            await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, useAsync: true);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = 0L;

            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                var read = await source.ReadAsync(buffer, idle.Token);
                if (read == 0) break;

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;

                // Не дёргаем интерфейс на каждый блок: примерно раз на 256 КБ.
                if (done - lastReport >= 262144)
                {
                    onProgress?.Invoke(done, total);
                    lastReport = done;
                }
            }

            onProgress?.Invoke(done, total);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Сработал наш предел, а не отмена снаружи. Для вызывающего это
            // такой же отказ сети, как обрыв, — и лечится так же: повторить.
            throw new HttpRequestException(
                $"сервер перестал отвечать ({IdleTimeout.TotalSeconds:0} с без данных)");
        }
    }
}
