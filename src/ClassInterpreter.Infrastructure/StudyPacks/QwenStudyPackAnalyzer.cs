using System.Net.Http.Headers;
using System.Text;
using ClassInterpreter.Core.StudyPacks;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Infrastructure.Qwen;

namespace ClassInterpreter.Infrastructure.StudyPacks;

public sealed class QwenStudyPackAnalyzer(Uri endpoint, string apiKey) : IStudyPackAnalyzer, IDisposable
{
    private const int MaxAttempts = 3;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Uri[] _endpoints =
    [
        endpoint,
        QwenEndpoint.SingaporeTranslationFallback()
    ];
    public long RequestCount { get; private set; }
    public long FailureCount { get; private set; }
    public long InputCharacters { get; private set; }
    public long OutputCharacters { get; private set; }
    public long EstimatedInputTokens { get; private set; }
    public long EstimatedOutputTokens { get; private set; }

    public async ValueTask<string> AnalyzeAsync(string timestampedTranscript, CancellationToken cancellationToken = default)
        => await SendAsync(QwenStudyPackProtocol.CreateRequest(timestampedTranscript), cancellationToken);

    public async ValueTask<string> AnalyzeBundleAsync(string bundle, CancellationToken cancellationToken = default)
    {
        var chunks = StudyPackChunker.Split(bundle);
        if (chunks.Count == 1) return await AnalyzeAsync(bundle, cancellationToken);
        var notes = new List<string>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            notes.Add(await SendAsync(QwenStudyPackProtocol.CreateChunkRequest(chunks[index], index + 1, chunks.Count), cancellationToken));
        }
        return await SendAsync(QwenStudyPackProtocol.CreateSynthesisRequest(string.Join("\n\n---\n\n", notes)), cancellationToken);
    }

    private async ValueTask<string> SendAsync(string json, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var currentEndpoint in _endpoints.Distinct())
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                RequestCount++;
                InputCharacters += json.Length;
                EstimatedInputTokens += AiUsageRecord.EstimateTokens(json);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, currentEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        FailureCount++;
                        var providerMessage = await response.Content.ReadAsStringAsync(cancellationToken);
                        lastError = new InvalidOperationException(
                            $"学习总结请求失败：HTTP {(int)response.StatusCode}，{Shorten(providerMessage)}");
                        var retryable = (int)response.StatusCode is 408 or 429 or >= 500;
                        if (retryable && attempt < MaxAttempts)
                        {
                            await Task.Delay(RetryDelay(response, attempt), cancellationToken);
                            continue;
                        }

                        // A workspace endpoint may be temporarily unavailable while the
                        // Singapore public-compatible endpoint is healthy.
                        if (retryable) break;
                        throw lastError;
                    }

                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = QwenStudyPackProtocol.ParseResponse(responseJson);
                    OutputCharacters += result.Length;
                    EstimatedOutputTokens += AiUsageRecord.EstimateTokens(result);
                    return result;
                }
                catch (HttpRequestException exception)
                {
                    FailureCount++;
                    lastError = exception;
                    if (attempt < MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * attempt), cancellationToken);
                        continue;
                    }
                    break;
                }
                catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    FailureCount++;
                    lastError = exception;
                    if (attempt < MaxAttempts) continue;
                    break;
                }
            }
        }

        throw new InvalidOperationException(
            "学习总结连接千问失败：工作空间专用地址和新加坡备用地址均已自动重试。请确认 API Key 后重新总结。",
            lastError);
    }

    private static string Shorten(string value)
    {
        var clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= 240 ? clean : clean[..240] + "…";
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt) =>
        response.Headers.RetryAfter?.Delta is { } providerDelay && providerDelay <= TimeSpan.FromSeconds(30)
            ? providerDelay
            : TimeSpan.FromSeconds(attempt * attempt);

    public void Dispose() => _httpClient.Dispose();
}
