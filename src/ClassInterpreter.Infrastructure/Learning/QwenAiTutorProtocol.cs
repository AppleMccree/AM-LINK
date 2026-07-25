using System.Text.Encodings.Web;
using System.Text.Json;
using ClassInterpreter.Core.Learning;

namespace ClassInterpreter.Infrastructure.Learning;

public static class QwenAiTutorProtocol
{
    public const string Model = "qwen-flash";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string CreateRequest(AiTutorRequest request) => JsonSerializer.Serialize(new
    {
        model = Model,
        messages = new object[]
        {
            new
            {
                role = "system",
                content = "你是大学课堂学习助教。只能依据用户提供的课堂字幕和课件文字回答中文。每个事实尽量使用[PPT第N页]或[mm:ss]标明依据；资料不足必须明确说课堂资料中无法确认，不得编造。先直接回答，再用简短要点解释。如果用户只输入一个被选中的词、短语或句子，应主动解释它在当前课堂中的含义和上下文，不要求用户再补一句“请解释”。"
            },
            new
            {
                role = "user",
                content = $"课程：{request.CourseName}\n当前PPT页：{request.CurrentSlidePage?.ToString() ?? "无"}\n选中内容：{request.SelectedText ?? "无"}\n\n课件上下文：\n{request.SlideContext}\n\n课堂字幕上下文：\n{request.TranscriptContext}\n\n问题：{request.Question}"
            }
        },
        stream = false
    }, JsonOptions);

    public static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var choices = document.RootElement.GetProperty("choices");
        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("千问未返回问 AI 内容。");

        var result = content.Trim();
        if (result.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = result.IndexOf('\n');
            if (firstLine >= 0) result = result[(firstLine + 1)..];
            if (result.EndsWith("```", StringComparison.Ordinal)) result = result[..^3].TrimEnd();
        }
        return result;
    }
}
