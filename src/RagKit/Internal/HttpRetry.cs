using System.Net.Http;

namespace RagKit.Internal;

/// <summary>
/// POST with bounded retries + exponential backoff for transient failures
/// (HTTP 429 / 5xx, network errors, timeouts). Honors <c>Retry-After</c> on 429.
/// The content factory is re-invoked per attempt (HttpContent is single-use).
/// </summary>
internal static class HttpRetry
{
    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, string path, Func<HttpContent> content, CancellationToken ct, int attempts = 3)
    {
        for (int i = 0; ; i++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await http.PostAsync(path, content(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (i < attempts - 1 && !ct.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                // Network error or timeout (not a user cancellation): back off and retry.
                await Task.Delay(Backoff(i), ct).ConfigureAwait(false);
                continue;
            }

            int code = (int)resp.StatusCode;
            if (i < attempts - 1 && (code == 429 || code >= 500))
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? Backoff(i);
                resp.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            return resp;
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt));
}
