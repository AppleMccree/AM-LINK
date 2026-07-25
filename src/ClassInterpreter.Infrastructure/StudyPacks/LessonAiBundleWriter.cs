using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Core.Slides;

namespace ClassInterpreter.Infrastructure.StudyPacks;

public static class LessonAiBundleWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<string> WriteAsync(
        string lessonDirectory,
        Session session,
        string lessonKey,
        SlideDocument? slides,
        IReadOnlyList<TranscriptSegment> transcripts,
        IReadOnlyList<AiQuestionRecord> questions,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(lessonDirectory);
        var files = Directory.GetFiles(lessonDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(lessonDirectory, path))
            .OrderBy(path => path)
            .ToArray();
        var bundle = new
        {
            version = 1,
            lessonKey,
            course = session.CourseName,
            startedAt = session.StartedAt,
            endedAt = session.EndedAt,
            slides = slides?.Pages.Select(page => new
            {
                page = page.PageNumber,
                page.Title,
                page.Text,
                page.Notes
            }).ToArray() ?? [],
            transcript = transcripts.Where(item => item.IsFinal).OrderBy(item => item.Start).Select(item => new
            {
                timestamp = FormatTimestamp(item.Start),
                language = item.Language,
                source = item.SourceText,
                translation = item.TargetText ?? item.ChineseText,
                viewedSlidePage = item.ViewedSlidePage,
                candidateSlidePage = item.CandidateSlidePage,
                slideMatchConfidence = item.SlideMatchConfidence,
                slideMatchEvidence = item.SlideMatchEvidence,
                slideFollowAction = item.SlideFollowAction.ToString()
            }).ToArray(),
            questions = questions.Select(item => new
            {
                askedAt = item.AskedAt,
                item.Question,
                item.SelectedText,
                item.Answer,
                item.SlidePage,
                item.TranscriptTimestamp,
                status = item.Status.ToString()
            }).ToArray(),
            files
        };
        var path = Path.Combine(lessonDirectory, "AI学习资料包.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(bundle, JsonOptions), Encoding.UTF8, cancellationToken);
        await WriteQuestionsMarkdownAsync(Path.Combine(lessonDirectory, "问AI记录.md"), questions, cancellationToken);
        return path;
    }

    public static string RenderForModel(
        Session session,
        SlideDocument? slides,
        IReadOnlyList<TranscriptSegment> transcripts,
        IReadOnlyList<AiQuestionRecord> questions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 课程：{session.CourseName}").AppendLine();
        builder.AppendLine("## 课件文字");
        if (slides is null || slides.Pages.Count == 0) builder.AppendLine("（没有课件文字）");
        else foreach (var page in slides.Pages)
            builder.AppendLine($"[PPT第{page.PageNumber}页] {page.Title}\n{page.Text}\n备注：{page.Notes}");
        builder.AppendLine().AppendLine("## 双语课堂字幕");
        foreach (var item in transcripts.Where(item => item.IsFinal).OrderBy(item => item.Start))
        {
            var page = item.CandidateSlidePage ?? item.ViewedSlidePage;
            var pageReference = page is null ? string.Empty : $" [PPT第{page}页]";
            builder.AppendLine($"[{FormatTimestamp(item.Start)}] [{item.Language}]{pageReference} {item.SourceText} → {item.TargetText ?? item.ChineseText}");
        }
        builder.AppendLine().AppendLine("## 学生问AI记录");
        if (questions.Count == 0) builder.AppendLine("（没有问答记录）");
        else foreach (var item in questions)
            builder.AppendLine($"[{item.AskedAt:HH:mm:ss}] 问：{item.Question}\n答：{item.Answer ?? $"（{item.Status}）"}");
        return builder.ToString();
    }

    private static async Task WriteQuestionsMarkdownAsync(
        string path,
        IReadOnlyList<AiQuestionRecord> questions,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder("# 问AI记录\n\n");
        if (questions.Count == 0) builder.AppendLine("这节课没有AI问答记录。");
        foreach (var item in questions)
        {
            builder.AppendLine($"## {item.AskedAt:HH:mm:ss} · {item.Question}").AppendLine();
            if (!string.IsNullOrWhiteSpace(item.SelectedText)) builder.AppendLine($"> {item.SelectedText}\n");
            builder.AppendLine(item.Answer ?? $"未完成：{item.Error ?? item.Status.ToString()}").AppendLine();
        }
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static string FormatTimestamp(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
}
