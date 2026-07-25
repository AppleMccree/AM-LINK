using System.Net.Http.Headers;
using System.Text;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Infrastructure.Qwen;

public sealed class QwenMtTranslator : ITextTranslator, IDisposable
{
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TranslationDirection _direction;

    public string? DomainHint { get; set; }
    public IReadOnlyList<string> PreservedTerms { get; set; } = [];

    public QwenMtTranslator(Uri endpoint, string apiKey, HttpClient? httpClient = null)
        : this(endpoint, apiKey, TranslationDirection.MixedToChinese, httpClient)
    {
    }

    public QwenMtTranslator(Uri endpoint, string apiKey, TranslationDirection direction, HttpClient? httpClient = null)
    {
        _endpoint = endpoint;
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException("API Key 不能为空。", nameof(apiKey))
            : apiKey;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsClient = httpClient is null;
        _direction = direction ?? throw new ArgumentNullException(nameof(direction));
    }

    public async ValueTask<string> TranslateAsync(
        string sourceText,
        bool isFinal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(
            QwenMtProtocol.CreateRequest(sourceText, _direction, DomainHint, PreservedTerms),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new QwenProviderException($"translation_http_{(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return QwenMtProtocol.ParseResponse(json);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
