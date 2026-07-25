namespace ClassInterpreter.Core.StudyPacks;

public interface IStudyPackAnalyzer
{
    ValueTask<string> AnalyzeAsync(string timestampedTranscript, CancellationToken cancellationToken = default);
}
