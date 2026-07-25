using System.Text;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Infrastructure.StudyPacks;

public static class MarkdownStudyPackWriter
{
    public static string Render(Session session, string analysisMarkdown, IReadOnlyList<TranscriptSegment> transcripts)
    {
        var builder = new StringBuilder();
        builder.Append("# AI 课堂学习包：").AppendLine(session.CourseName).AppendLine();
        builder.AppendLine("> 本学习包由千问阅读本节课课件、完整字幕和课堂问答后生成；文末保留可核查逐字稿。").AppendLine();
        builder.Append("- 开始：").AppendLine(session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        if (session.EndedAt is not null)
        {
            builder.Append("- 结束：").AppendLine(session.EndedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }

        builder.AppendLine().AppendLine(analysisMarkdown.Trim()).AppendLine();
        builder.AppendLine("## 可核查逐字稿").AppendLine();
        foreach (var segment in transcripts.Where(segment => segment.IsFinal).OrderBy(segment => segment.Start).ThenBy(segment => segment.Sequence))
        {
            var direction = TranslationDirection.FromId(segment.TranslationDirectionId);
            builder.Append("- [").Append(FormatTimestamp(segment.Start)).Append("] ")
                .Append('[').Append(segment.Language.ToUpperInvariant()).Append("] ")
                .Append('[').Append(direction.DisplayName).Append("] ")
                .Append(segment.SourceText);
            var page = segment.CandidateSlidePage ?? segment.ViewedSlidePage;
            if (page is not null)
            {
                builder.Append(" [PPT第").Append(page.Value).Append("页]");
            }
            var targetText = segment.TargetText ?? segment.ChineseText;
            if (!string.IsNullOrWhiteSpace(targetText))
            {
                builder.Append(" → ").Append(targetText);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static async ValueTask WriteAsync(
        string path,
        Session session,
        string analysisMarkdown,
        IReadOnlyList<TranscriptSegment> transcripts,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, Render(session, analysisMarkdown, transcripts), Encoding.UTF8, cancellationToken);
    }

    private static string FormatTimestamp(TimeSpan timestamp) =>
        timestamp.TotalHours >= 1 ? timestamp.ToString(@"hh\:mm\:ss") : timestamp.ToString(@"mm\:ss");
}
