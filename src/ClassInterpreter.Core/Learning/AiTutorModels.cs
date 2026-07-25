namespace ClassInterpreter.Core.Learning;

public enum AiQuestionStatus
{
    Pending,
    Completed,
    Failed
}

public sealed record AiQuestionRecord(
    Guid Id,
    string LessonKey,
    Guid? CourseId,
    DateTimeOffset AskedAt,
    string Question,
    string? SelectedText,
    string? Answer,
    int? SlidePage,
    string? TranscriptTimestamp,
    string Model,
    AiQuestionStatus Status,
    string? Error);

public sealed record AiTutorRequest(
    string Question,
    string? SelectedText,
    string CourseName,
    int? CurrentSlidePage,
    string SlideContext,
    string TranscriptContext);

public interface IAiTutorService
{
    ValueTask<string> AskAsync(AiTutorRequest request, CancellationToken cancellationToken = default);
}
