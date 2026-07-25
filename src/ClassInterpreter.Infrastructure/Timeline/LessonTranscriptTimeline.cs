using ClassInterpreter.Core.Sessions;

namespace ClassInterpreter.Infrastructure.Timeline;

public static class LessonTranscriptTimeline
{
    public static IReadOnlyList<TranscriptSegment> Combine(
        IEnumerable<(Session Session, IReadOnlyList<TranscriptSegment> Segments)> batches)
    {
        var result = new List<TranscriptSegment>();
        var cursor = TimeSpan.Zero;
        var firstSession = true;
        foreach (var batch in batches.OrderBy(item => item.Session.StartedAt))
        {
            var segments = batch.Segments
                .OrderBy(item => item.Start)
                .ThenBy(item => item.Sequence)
                .ToArray();
            if (segments.Length == 0) continue;

            // A restarted session begins its own audio clock at zero. Concatenate recorded
            // time instead of adding the wall-clock downtime between interruptions.
            var offset = firstSession ? TimeSpan.Zero : cursor + TimeSpan.FromSeconds(1);
            result.AddRange(segments.Select(item => item with
            {
                Start = offset + item.Start,
                End = offset + item.End
            }));
            cursor = offset + segments.Max(item => item.End > item.Start ? item.End : item.Start);
            firstSession = false;
        }

        return result.OrderBy(item => item.Start).ThenBy(item => item.Sequence).ToArray();
    }
}
