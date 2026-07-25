using System.Text.Json;
using ClassInterpreter.Core.Audio;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Infrastructure.Qwen;

public static class QwenAsrProtocol
{
    public const string Model = "qwen3-asr-flash-realtime";

    public static string CreateSessionUpdate(AudioFormat format, string? sourceLanguage = null, int silenceDurationMs = 1200)
    {
        var transcription = new Dictionary<string, object> { ["model"] = Model };
        if (!string.IsNullOrWhiteSpace(sourceLanguage)) transcription["language"] = sourceLanguage;
        return JsonSerializer.Serialize(new
        {
            type = "session.update",
            event_id = $"event_{Guid.NewGuid():N}",
            session = new
            {
                input_audio_format = "pcm",
                sample_rate = format.SampleRate,
                input_audio_transcription = transcription,
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.2,
                    prefix_padding_ms = 300,
                    silence_duration_ms = Math.Clamp(silenceDurationMs, 400, 3000)
                }
            }
        });
    }

    public static string CreateAudioAppend(ReadOnlySpan<byte> pcm) => JsonSerializer.Serialize(new
    {
        type = "input_audio_buffer.append",
        event_id = $"event_{Guid.NewGuid():N}",
        audio = Convert.ToBase64String(pcm)
    });

    public static string CreateSessionFinish() => JsonSerializer.Serialize(new
    {
        type = "session.finish",
        event_id = $"event_{Guid.NewGuid():N}"
    });

    public static SpeechProviderEvent? ParseServerEvent(string json, TimeSpan audioPosition)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = ReadString(root, "type") ?? string.Empty;

        if (type == "error" || type.EndsWith(".failed", StringComparison.Ordinal))
        {
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement : root;
            var code = ReadString(error, "code") ?? ReadString(error, "error_code") ?? "unknown_error";
            throw new QwenProviderException(code);
        }

        if (type is "conversation.item.input_audio_transcription.text" or "conversation.item.input_audio_transcription.delta")
        {
            var confirmed = ReadString(root, "text") ?? string.Empty;
            var stash = ReadString(root, "stash") ?? string.Empty;
            return new RecognitionEvent(
                ReadString(root, "item_id") ?? $"interim-{Guid.NewGuid():N}",
                RecognitionTextNormalizer.Merge(confirmed, stash),
                ReadString(root, "language") ?? "unknown",
                false,
                audioPosition,
                ReadString(root, "emotion"));
        }

        if (type == "conversation.item.input_audio_transcription.completed")
        {
            return new RecognitionEvent(
                ReadString(root, "item_id") ?? $"final-{Guid.NewGuid():N}",
                RecognitionTextNormalizer.Sanitize(ReadString(root, "transcript")),
                ReadString(root, "language") ?? "unknown",
                true,
                audioPosition,
                ReadString(root, "emotion"));
        }

        return string.IsNullOrWhiteSpace(type) ? null : new SpeechSessionEvent(type);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
