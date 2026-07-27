using System.Net.Http.Json;
using System.Text.Json;
using ClassInterpreter.Core.Classrooms;

namespace ClassInterpreter.Infrastructure.Classrooms;

public sealed class CloudClassroomSyncService(string queuePath) : IClassroomSyncService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<QueuedClassroomEvent> _queue = [];
    private CancellationTokenSource? _polling;
    private Uri? _server;
    private string? _token;
    private Guid? _lastBroadcast;
    public bool IsConnected { get; private set; }
    public ClassroomJoinResult? CurrentClassroom { get; private set; }
    public event EventHandler<ClassroomAggregateSnapshot>? SnapshotUpdated;
    public event EventHandler<TeacherBroadcast>? BroadcastReceived;
    public event EventHandler<string>? ConnectionStatusChanged;

    public async ValueTask<ClassroomJoinResult> JoinAsync(Uri server, ClassroomJoinRequest request, CancellationToken cancellationToken = default)
    {
        _server = new Uri(server.ToString().TrimEnd('/') + "/");
        await LoadQueueAsync(cancellationToken);
        using var response = await _http.PostAsJsonAsync(new Uri(_server, "api/classrooms/join"), request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        CurrentClassroom = await response.Content.ReadFromJsonAsync<ClassroomJoinResult>(cancellationToken: cancellationToken) ?? throw new InvalidOperationException("课堂服务没有返回加入结果");
        _token = CurrentClassroom.ParticipantToken;
        IsConnected = true;
        ConnectionStatusChanged?.Invoke(this, $"已加入：{CurrentClassroom.CourseName} · {CurrentClassroom.LessonName}");
        _polling?.Cancel(); _polling = new(); _ = PollAsync(_polling.Token);
        await FlushQueueAsync(cancellationToken);
        return CurrentClassroom;
    }

    public ValueTask LeaveAsync(CancellationToken cancellationToken = default)
    {
        _polling?.Cancel(); IsConnected = false; CurrentClassroom = null; _token = null;
        ConnectionStatusChanged?.Invoke(this, "未加入云端课堂（单机功能正常）");
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishQuestionAsync(ClassroomQuestionEvent question, CancellationToken cancellationToken = default) => SendOrQueueAsync("questions", question, cancellationToken);
    public ValueTask VoteAsync(QuestionVote vote, CancellationToken cancellationToken = default) => SendOrQueueAsync("votes", vote, cancellationToken);
    public ValueTask SendConfusionAsync(ConfusionSignal signal, CancellationToken cancellationToken = default) => SendOrQueueAsync("confusions", signal, cancellationToken);
    public async ValueTask<string> AskWithSchoolKeyAsync(string prompt, CancellationToken cancellationToken = default)
    {
        EnsureJoined();
        using var request = Authorized(HttpMethod.Post, $"api/classrooms/{CurrentClassroom!.LessonId}/school-ai");
        request.Content = JsonContent.Create(new { prompt });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SchoolAiResponse>(cancellationToken: cancellationToken);
        return result?.Answer ?? throw new InvalidOperationException("学校千问没有返回答案");
    }

    public async ValueTask<ClassroomAggregateSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureJoined();
        using var request = Authorized(HttpMethod.Get, $"api/classrooms/{CurrentClassroom!.LessonId}/snapshot");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ClassroomAggregateSnapshot>(cancellationToken: cancellationToken) ?? throw new InvalidOperationException("课堂快照为空");
        IsConnected = true; SnapshotUpdated?.Invoke(this, result);
        var latest = result.Broadcasts.FirstOrDefault();
        if (latest is not null && latest.Id != _lastBroadcast) { _lastBroadcast = latest.Id; BroadcastReceived?.Invoke(this, latest); }
        return result;
    }

    private async ValueTask SendOrQueueAsync<T>(string kind, T payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        if (!IsConnected || CurrentClassroom is null)
        {
            await QueueAsync(kind, json, cancellationToken); return;
        }
        try { await SendAsync(kind, json, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsConnected = false; ConnectionStatusChanged?.Invoke(this, "云端暂时断开，本地同传继续；操作将在重连后补传");
            await QueueAsync(kind, json, cancellationToken);
        }
    }

    private async Task SendAsync(string kind, string json, CancellationToken cancellationToken)
    {
        EnsureJoined();
        using var request = Authorized(HttpMethod.Post, $"api/classrooms/{CurrentClassroom!.LessonId}/{kind}");
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken); await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var reconnectAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FlushQueueAsync(cancellationToken); await RefreshAsync(cancellationToken);
                ConnectionStatusChanged?.Invoke(this, "云端课堂已连接");
                reconnectAttempts = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                IsConnected = false;
                reconnectAttempts++;
                ConnectionStatusChanged?.Invoke(this, "云端重连中，待发送内容已保存在本机；本地同传不受影响");
            }
            try
            {
                var delay = reconnectAttempts == 0
                    ? TimeSpan.FromSeconds(2)
                    : ClassroomOutboxPolicy.RetryDelay(reconnectAttempts - 1);
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task QueueAsync(string kind, string json, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken); try { _queue.Add(new(kind, json)); await SaveQueueAsync(cancellationToken); } finally { _gate.Release(); }
    }
    private async Task FlushQueueAsync(CancellationToken cancellationToken)
    {
        if (CurrentClassroom is null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (_queue.Count > 0)
            {
                var item = _queue[0];
                try { await SendAsync(item.Kind, item.Json, cancellationToken); _queue.RemoveAt(0); }
                catch (Exception failure) when (failure is ClassroomServerException or HttpRequestException or TaskCanceledException)
                {
                    if (ClassroomOutboxPolicy.Decide(failure, item.Attempts) == OutboxRetryDecision.Drop)
                    {
                        // 服务器明确拒绝或重试次数用尽：这一条永远发不出去，
                        // 丢弃它让后面的消息继续补传，而不是每 2 秒无限重试同一条。
                        _queue.RemoveAt(0);
                        ConnectionStatusChanged?.Invoke(this, "有一条离线消息无法补传，已跳过；其余消息继续发送");
                        continue;
                    }

                    _queue[0] = item with { Attempts = Math.Min(item.Attempts + 1, 1_000_000) };
                    await SaveQueueAsync(cancellationToken);
                    throw;
                }
            }
            await SaveQueueAsync(cancellationToken); IsConnected = true;
        }
        finally { _gate.Release(); }
    }
    private async Task LoadQueueAsync(CancellationToken ct) { if (!File.Exists(queuePath) || _queue.Count > 0) return; await using var s=File.OpenRead(queuePath); _queue.AddRange(await JsonSerializer.DeserializeAsync<List<QueuedClassroomEvent>>(s,cancellationToken:ct)??[]); }
    private async Task SaveQueueAsync(CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(queuePath)!); await using var s=File.Create(queuePath); await JsonSerializer.SerializeAsync(s,_queue,cancellationToken:ct); }
    private HttpRequestMessage Authorized(HttpMethod method, string path) { var r=new HttpRequestMessage(method,new Uri(_server!,path)); r.Headers.Authorization=new("Bearer",_token); return r; }
    private void EnsureJoined() { if (_server is null || CurrentClassroom is null || string.IsNullOrWhiteSpace(_token)) throw new InvalidOperationException("尚未加入课堂"); }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        var message = raw;
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) message = error.GetString() ?? raw;
            else if (root.TryGetProperty("detail", out var detail)) message = detail.GetString() ?? raw;
            else if (root.TryGetProperty("title", out var title)) message = title.GetString() ?? raw;
        }
        catch (JsonException) { }
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && string.IsNullOrWhiteSpace(message))
            message = "课堂码不存在或课堂已经结束";
        throw new ClassroomServerException(
            string.IsNullOrWhiteSpace(message) ? $"课堂服务返回 {(int)response.StatusCode}" : message,
            response.StatusCode);
    }
    public async ValueTask DisposeAsync() { _polling?.Cancel(); _polling?.Dispose(); _http.Dispose(); _gate.Dispose(); await Task.CompletedTask; }
    private sealed record QueuedClassroomEvent(string Kind, string Json, int Attempts = 0);
    private sealed record SchoolAiResponse(string Answer);
}
