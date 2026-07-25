namespace ClassInterpreter.Core.Learning;

public enum AiUsageKind
{
    SpeechRecognition,
    Translation,
    AiTutor,
    StudyPack
}

public sealed record AiUsageRecord(
    DateOnly Day,
    AiUsageKind Kind,
    string Model,
    long RequestCount,
    long FailureCount,
    long InputCharacters,
    long OutputCharacters,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    long AudioMilliseconds)
{
    public static long EstimateTokens(string? text) => string.IsNullOrEmpty(text)
        ? 0
        : Math.Max(1, (long)Math.Ceiling(text.Length / 2.5));
}
