namespace ClassInterpreter.Core.Classrooms;

public sealed record ClassroomJoinRequest(string ClassroomCode, string AnonymousClientId);

public sealed record ClassroomJoinResult(
    Guid LessonId,
    string CourseName,
    string LessonName,
    string ParticipantToken,
    DateTimeOffset JoinedAt);

public sealed record ClassroomQuestionEvent(
    Guid EventId,
    Guid LessonId,
    string Question,
    DateTimeOffset AskedAt,
    string? TranscriptTimestamp,
    int? SlidePage,
    string? SelectedContext);

public sealed record ConfusionSignal(
    Guid EventId,
    Guid LessonId,
    DateTimeOffset OccurredAt,
    string? TranscriptTimestamp,
    int? SlidePage);

public sealed record QuestionVote(Guid EventId, Guid QuestionId, DateTimeOffset VotedAt);

public sealed record ClassroomQuestionView(
    Guid Id,
    string Question,
    DateTimeOffset AskedAt,
    string? TranscriptTimestamp,
    int? SlidePage,
    string? SelectedContext,
    int Votes,
    bool IsPinned,
    bool IsAddressed,
    string Topic);

public sealed record TeacherBroadcast(Guid Id, string Message, DateTimeOffset SentAt);

public sealed record ClassroomAggregateSnapshot(
    int OnlineStudents,
    int QuestionCount,
    int AnonymousAskers,
    int UnaddressedQuestions,
    int ConfusionCount,
    IReadOnlyList<ClassroomQuestionView> Questions,
    IReadOnlyList<TeacherBroadcast> Broadcasts);

public interface IClassroomSyncService : IAsyncDisposable
{
    bool IsConnected { get; }
    ClassroomJoinResult? CurrentClassroom { get; }
    event EventHandler<ClassroomAggregateSnapshot>? SnapshotUpdated;
    event EventHandler<TeacherBroadcast>? BroadcastReceived;
    event EventHandler<string>? ConnectionStatusChanged;

    ValueTask<ClassroomJoinResult> JoinAsync(
        Uri server,
        ClassroomJoinRequest request,
        CancellationToken cancellationToken = default);
    ValueTask LeaveAsync(CancellationToken cancellationToken = default);
    ValueTask PublishQuestionAsync(ClassroomQuestionEvent question, CancellationToken cancellationToken = default);
    ValueTask VoteAsync(QuestionVote vote, CancellationToken cancellationToken = default);
    ValueTask SendConfusionAsync(ConfusionSignal signal, CancellationToken cancellationToken = default);
    ValueTask<string> AskWithSchoolKeyAsync(string prompt, CancellationToken cancellationToken = default);
    ValueTask<ClassroomAggregateSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
