namespace ClassInterpreter.Core.Speech;

public abstract record SpeechProviderEvent;

public sealed record RecognitionEvent(
    string SegmentId,
    string Text,
    string Language,
    bool IsFinal,
    TimeSpan AudioPosition,
    string? Emotion) : SpeechProviderEvent;

public sealed record SpeechSessionEvent(string Type) : SpeechProviderEvent;
