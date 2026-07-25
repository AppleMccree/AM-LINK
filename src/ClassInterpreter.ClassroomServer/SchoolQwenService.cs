using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClassInterpreter.ClassroomServer;

public sealed class SchoolQwenService(IConfiguration configuration)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly SemaphoreSlim _concurrency = new(configuration.GetValue("Qwen:MaxConcurrency", 8));
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _studentCalls = new();
    private readonly ConcurrentDictionary<Guid, int> _lessonCalls = new();
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Qwen:ApiKey"]);

    public async Task<string> AskAsync(Guid lessonId, Guid participantId, string prompt, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var calls = _studentCalls.GetOrAdd(participantId, _ => new());
        lock (calls)
        {
            while (calls.TryPeek(out var oldest) && now-oldest > TimeSpan.FromMinutes(1)) calls.Dequeue();
            if (calls.Count >= configuration.GetValue("Qwen:StudentCallsPerMinute", 6)) throw new SchoolQwenLimitException("个人课堂AI调用过快，请稍后再问");
            calls.Enqueue(now);
        }
        var used = _lessonCalls.AddOrUpdate(lessonId, 1, (_, value) => value + 1);
        if (used > configuration.GetValue("Qwen:LessonCallBudget", 500)) throw new SchoolQwenLimitException("本课堂统一AI预算已到提醒上限，请联系老师");
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration["Qwen:Endpoint"] ?? "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["Qwen:ApiKey"]);
            request.Content = JsonContent.Create(new { model="qwen-flash", messages=new[]{new{role="system",content="你是课堂学习助教。只根据学生提供的课堂上下文用中文回答；证据不足要明确说明。保留[PPT第N页]和[mm:ss]引用。"},new{role="user",content=prompt}}, temperature=0.2 });
            using var response = await _http.SendAsync(request,cancellationToken);
            if ((int)response.StatusCode==429) throw new SchoolQwenLimitException("千问服务繁忙，问题已保留，请稍后重试");
            response.EnsureSuccessStatusCode();
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "资料不足，暂时无法回答。";
        }
        finally { _concurrency.Release(); }
    }
}
public sealed class SchoolQwenLimitException(string message):Exception(message);
