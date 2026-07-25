namespace ClassInterpreter.ClassroomServer;

public sealed record ClassroomJoinRequest(string ClassroomCode, string AnonymousClientId);
public sealed record ClassroomJoinResult(Guid LessonId, string CourseName, string LessonName, string ParticipantToken, DateTimeOffset JoinedAt);
public sealed record ClassroomQuestionEvent(Guid EventId, Guid LessonId, string Question, DateTimeOffset AskedAt, string? TranscriptTimestamp, int? SlidePage, string? SelectedContext);
public sealed record ConfusionSignal(Guid EventId, Guid LessonId, DateTimeOffset OccurredAt, string? TranscriptTimestamp, int? SlidePage);
public sealed record QuestionVote(Guid EventId, Guid QuestionId, DateTimeOffset VotedAt);
public sealed record ClassroomQuestionView(Guid Id, string Question, DateTimeOffset AskedAt, string? TranscriptTimestamp, int? SlidePage, string? SelectedContext, int Votes, bool IsPinned, bool IsAddressed, string Topic);
public sealed record TeacherBroadcast(Guid Id, string Message, DateTimeOffset SentAt);
public sealed record ClassroomAggregateSnapshot(int OnlineStudents, int QuestionCount, int AnonymousAskers, int UnaddressedQuestions, int ConfusionCount, IReadOnlyList<ClassroomQuestionView> Questions, IReadOnlyList<TeacherBroadcast> Broadcasts);
