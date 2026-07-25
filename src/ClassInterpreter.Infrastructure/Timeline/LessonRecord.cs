using ClassInterpreter.Core.Sessions;

namespace ClassInterpreter.Infrastructure.Timeline;

public sealed record LessonRecord(IReadOnlyList<Session> Sessions, int LessonNumber)
{
    public DateTimeOffset StartedAt => Sessions.Min(session => session.StartedAt);
    public DateTimeOffset? EndedAt => Sessions.Max(session => session.EndedAt ?? session.StartedAt);
    public SessionStatus Status => Sessions.Any(session => session.Status == SessionStatus.Live)
        ? SessionStatus.Live
        : Sessions.Any(session => session.Status == SessionStatus.Interrupted)
            ? SessionStatus.Interrupted
            : SessionStatus.Completed;
    public string? MaterialPath => Sessions
        .OrderByDescending(session => session.StartedAt)
        .Select(session => session.MaterialPath)
        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        ?? Sessions.OrderByDescending(session => session.StartedAt)
            .Select(session => session.MaterialPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    public string? StudyPackPath => Sessions
        .OrderByDescending(session => session.StartedAt)
        .Select(session => session.StudyPackPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    public int? LastSlidePage => Sessions
        .OrderByDescending(session => session.StartedAt)
        .Select(session => session.LastSlidePage)
        .FirstOrDefault(page => page is > 0);
    public string MaterialBadge => string.IsNullOrWhiteSpace(MaterialPath) ? string.Empty : "已存课件";

    public static IReadOnlyList<LessonRecord> Build(IReadOnlyList<Session> sessions, bool mergeNearby)
    {
        if (sessions.Count == 0) return [];
        var chronological = sessions.OrderBy(session => session.StartedAt).ToArray();
        var groups = new List<List<Session>>();
        foreach (var session in chronological)
        {
            if (!mergeNearby || groups.Count == 0)
            {
                groups.Add([session]);
                continue;
            }

            var current = groups[^1];
            var previousEnd = current.Max(item => item.EndedAt ?? item.StartedAt);
            var gap = session.StartedAt - previousEnd;
            var currentKey = current.Select(item => item.LessonKey).FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
            var sameExplicitLesson = !string.IsNullOrWhiteSpace(session.LessonKey) &&
                                     string.Equals(session.LessonKey, currentKey, StringComparison.Ordinal);
            var canMergeByTime = string.IsNullOrWhiteSpace(session.LessonKey) && string.IsNullOrWhiteSpace(currentKey) &&
                                 session.StartedAt.Date == current[0].StartedAt.Date &&
                                 gap >= TimeSpan.FromMinutes(-2) && gap <= TimeSpan.FromMinutes(30);
            // A crash/restart creates a new explicit lesson key. Treat the next nearby
            // session as a continuation when the preceding segment was interrupted.
            var previousWasInterrupted = current.OrderBy(item => item.StartedAt).Last().Status == SessionStatus.Interrupted;
            var canMergeInterruptedRestart = previousWasInterrupted &&
                                             session.StartedAt.Date == current[0].StartedAt.Date &&
                                             gap >= TimeSpan.FromMinutes(-2) && gap <= TimeSpan.FromMinutes(30);
            if (sameExplicitLesson || canMergeByTime || canMergeInterruptedRestart)
            {
                current.Add(session);
            }
            else
            {
                groups.Add([session]);
            }
        }

        return groups
            .Select((group, index) => new LessonRecord(group, index + 1))
            .OrderByDescending(record => record.StartedAt)
            .ToArray();
    }
}
