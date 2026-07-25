using System.Text.Json;
using System.Text.Encodings.Web;

namespace ClassInterpreter.Infrastructure.StudyPacks;

public static class QwenStudyPackProtocol
{
    public const string Model = "qwen3.7-plus";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string CreateRequest(string transcript) => JsonSerializer.Serialize(new
    {
        model = Model,
        messages = new object[]
        {
            new
            {
                role = "system",
                content = "你是严谨的大学课程笔记助教。请真正阅读整节课的PPT文字、完整双语字幕和学生问答，重建老师实际讲授的内容。不要把零碎口头语、ASR错句、翻译矛盾或识别质量点评写成课程主题；遇到冲突时优先依据PPT、连续上下文和老师重复强调的说法，并把无法确认之处单独列出。输出中文Markdown，禁止代码围栏。必须按顺序包含：## 考试、作业与成绩评定（放在最前，分别列考试范围/形式/日期、作业内容/提交方式/截止时间、成绩构成/比例/出勤要求；只记录老师明确说过的内容并附[PPT第N页]或[mm:ss]，没提到就明确写“本节课未明确提及”，绝不推测）；## 一分钟总览（5至8条）；## 按授课顺序的详细总结；## 老师重点强调与重复内容；## 核心概念与术语；## 重要例子、论证与推导；## 学生问AI内容；## 易错点与待确认问题；## 考试复习清单；## 五道带答案的复习题。重要结论必须尽量附引用。总结课程知识，不要逐句复述。资料不足必须说明，不得编造。"
            },
            new { role = "user", content = transcript }
        },
        stream = false,
        temperature = 0.2,
        max_tokens = 8192
    }, JsonOptions);

    public static string CreateChunkRequest(string chunk, int index, int total) => JsonSerializer.Serialize(new
    {
        model = Model,
        messages = new object[]
        {
            new { role = "system", content = "你正在阅读一节大学课程资料的一部分。第一优先级提取老师明确提到的考试范围/形式/日期、作业内容/提交方式/截止时间、成绩构成/比例和出勤要求，并保留原始[PPT第N页]或[mm:ss]证据。然后提取实际讲授的知识、老师反复强调的重点、概念、论点、例子、推导和学生问题。过滤“嗯、那个”等口头语，不要把孤立ASR错句或翻译矛盾当成知识点；冲突内容标为待确认。不要写总摘要，不得编造，不要使用代码围栏。" },
            new { role = "user", content = $"这是第{index}/{total}部分：\n{chunk}" }
        },
        stream = false,
        temperature = 0.2,
        max_tokens = 4096
    }, JsonOptions);

    public static string CreateSynthesisRequest(string notes) => JsonSerializer.Serialize(new
    {
        model = Model,
        messages = new object[]
        {
            new { role = "system", content = "你是严谨的大学课程笔记助教。综合全部分块笔记，去重并恢复课程逻辑，生成可直接用于复习的中文Markdown，禁止代码围栏。必须按顺序包含：## 考试、作业与成绩评定（最优先，分别列考试、作业、成绩构成与出勤；只采用有引用的老师明确表述，没有就写“本节课未明确提及”）；## 一分钟总览；## 按授课顺序的详细总结；## 老师重点强调与重复内容；## 核心概念与术语；## 重要例子、论证与推导；## 学生问AI内容；## 易错点与待确认问题；## 考试复习清单；## 五道带答案的复习题。过滤口头语和孤立识别错误，不要把翻译质量分析写成课程总结。保留[PPT第N页]和[mm:ss]引用，不得编造。" },
            new { role = "user", content = notes }
        },
        stream = false,
        temperature = 0.2,
        max_tokens = 8192
    }, JsonOptions);

    public static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var choices = document.RootElement.GetProperty("choices");
        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("千问未返回课后分析内容。");
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
