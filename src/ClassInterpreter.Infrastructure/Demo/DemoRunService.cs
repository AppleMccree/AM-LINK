using ClassInterpreter.Core.Configuration;
using ClassInterpreter.Core.Demo;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Infrastructure.StudyPacks;
using ClassInterpreter.Infrastructure.Timeline;

namespace ClassInterpreter.Infrastructure.Demo;

public sealed record DemoRunResult(string DatabasePath, string MarkdownPath, Guid SessionId);

public sealed class DemoRunService(AppPaths paths)
{
    public async ValueTask<DemoRunResult> RunAsync(
        DemoScenario scenario,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var databasePath = Path.Combine(paths.DatabaseDirectory, "timeline.db");
        var repository = new SqliteTimelineRepository(databasePath);
        await repository.InitializeAsync(cancellationToken);
        var startedAt = DateTimeOffset.Now;
        var session = new Session(
            Guid.NewGuid(),
            "演示课堂：Transformer 组会",
            startedAt,
            null,
            SessionStatus.Live);
        await repository.UpsertSessionAsync(session, cancellationToken);

        long sequence = 0;
        foreach (var utterance in scenario.Utterances)
        {
            await repository.UpsertTranscriptAsync(new TranscriptSegment(
                Guid.NewGuid(),
                session.Id,
                ++sequence,
                utterance.At,
                utterance.At + TimeSpan.FromSeconds(2),
                utterance.Source,
                utterance.Chinese,
                true,
                utterance.Language,
                1)
            {
                TargetText = utterance.Chinese,
                TranslationDirectionId = ClassInterpreter.Core.Speech.TranslationDirection.MixedToChinese.Id
            }, cancellationToken);
        }

        var completed = session with { EndedAt = DateTimeOffset.Now, Status = SessionStatus.Completed };
        await repository.UpsertSessionAsync(completed, cancellationToken);
        var transcripts = await repository.GetTranscriptsAsync(session.Id, cancellationToken);
        var markdownPath = Path.Combine(
            paths.ExportDirectory,
            "演示课堂",
            startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            "学习包.md");
        await MarkdownStudyPackWriter.WriteAsync(
            markdownPath,
            completed,
            scenario.AnalysisMarkdown,
            transcripts,
            cancellationToken);
        return new DemoRunResult(databasePath, markdownPath, session.Id);
    }
}
