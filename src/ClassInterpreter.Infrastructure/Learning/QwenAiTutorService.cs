using System.Net.Http.Headers;
using System.Text;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Infrastructure.Qwen;

namespace ClassInterpreter.Infrastructure.Learning;

public sealed class QwenAiTutorService(Uri endpoint, string apiKey) : IAiTutorService, IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async ValueTask<string> AskAsync(AiTutorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(QwenAiTutorProtocol.CreateRequest(request), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new QwenProviderException($"ai_tutor_http_{(int)response.StatusCode}");
        }

        return QwenAiTutorProtocol.ParseResponse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public void Dispose() => _httpClient.Dispose();
}
