using ClassInterpreter.Core.Slides;

namespace ClassInterpreter.Core.Sessions;

public enum SessionStatus
{
    Preparing,
    Live,
    Paused,
    Interrupted,
    Completed
}

public sealed record Course(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    bool IsArchived);

public sealed record Session(
    Guid Id,
    string CourseName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status)
{
    public Guid? CourseId { get; init; }
    public string? MaterialPath { get; init; }
    public string? MaterialType { get; init; }
    public string? StudyPackPath { get; init; }
    public int LessonNumber { get; init; }
    public string MaterialBadge => string.IsNullOrWhiteSpace(MaterialPath) ? string.Empty : "已存课件";
    public string? LessonKey { get; init; }
    public int? LastSlidePage { get; init; }
}

public sealed record TranscriptSegment(
    Guid Id,
    Guid SessionId,
    long Sequence,
    TimeSpan Start,
    TimeSpan End,
    string SourceText,
    string? ChineseText,
    bool IsFinal,
    string Language,
    double? Confidence)
{
    public string? TargetText { get; init; }

    public string TranslationDirectionId { get; init; } = "mixed-to-chinese";

    public int? ViewedSlidePage { get; init; }

    public int? CandidateSlidePage { get; init; }

    public double? SlideMatchConfidence { get; init; }

    public string? SlideMatchEvidence { get; init; }

    public SlideFollowAction SlideFollowAction { get; init; }
}

public sealed record TranslationSegment(
    Guid Id,
    Guid SessionId,
    Guid SourceSegmentId,
    string ChineseText,
    bool IsFinal);

public sealed record SlideFocusEvent(
    Guid Id,
    Guid SessionId,
    DateTimeOffset OccurredAt,
    int PageNumber,
    double Confidence);
