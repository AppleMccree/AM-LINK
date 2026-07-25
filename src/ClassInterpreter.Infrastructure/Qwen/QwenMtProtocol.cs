using System.Text.Json;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Infrastructure.Qwen;

public static class QwenMtProtocol
{
    public const string Model = "qwen-mt-flash";

    public static string CreateRequest(string sourceText) => CreateRequest(sourceText, TranslationDirection.MixedToChinese);

    public static string CreateRequest(
        string sourceText,
        TranslationDirection direction,
        string? domainHint = null,
        IReadOnlyList<string>? preservedTerms = null) => JsonSerializer.Serialize(new
    {
        model = Model,
        messages = new[] { new { role = "user", content = sourceText } },
        stream = false,
        translation_options = new
        {
            source_lang = direction.SourceLanguage,
            target_lang = direction.TargetLanguage,
            domains = string.IsNullOrWhiteSpace(domainHint)
                ? "University lecture or research meeting. Preserve technical terms, names, formulas, and abbreviations accurately."
                : domainHint,
            terms = preservedTerms?.Select(term => new { source = term, target = term }).ToArray()
        }
    });

    public static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new QwenProviderException("translation_response_missing_choices");
        }

        var message = choices[0].GetProperty("message");
        var content = message.GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content)
            ? throw new QwenProviderException("translation_response_empty")
            : content;
    }
}
