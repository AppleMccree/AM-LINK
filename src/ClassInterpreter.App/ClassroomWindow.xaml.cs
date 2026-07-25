using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ClassInterpreter.Core.Classrooms;

namespace ClassInterpreter.App;

public partial class ClassroomWindow : Window
{
    private readonly IClassroomSyncService _service;
    private ClassroomAggregateSnapshot? _snapshot;
    public string ServerUrl => ServerBox.Text.Trim();
    public ClassroomWindow(IClassroomSyncService service, string serverUrl)
    {
        InitializeComponent(); _service=service; ServerBox.Text=serverUrl;
        _service.SnapshotUpdated += SnapshotUpdated; _service.BroadcastReceived += BroadcastReceived; _service.ConnectionStatusChanged += ConnectionStatusChanged;
        Closed += (_,_) => { _service.SnapshotUpdated -= SnapshotUpdated; _service.BroadcastReceived -= BroadcastReceived; _service.ConnectionStatusChanged -= ConnectionStatusChanged; };
        if (_service.CurrentClassroom is not null) Joined(_service.CurrentClassroom);
    }
    private async void Join_Click(object sender,RoutedEventArgs e)
    {
        if (_service.IsConnected) { await _service.LeaveAsync(); JoinButton.Content="匿名加入课堂"; ConfusionButton.IsEnabled=false; return; }
        if (!Uri.TryCreate(ServerBox.Text.Trim(),UriKind.Absolute,out var server) || CodeBox.Text.Trim().Length!=6) { StatusText.Text="请填写服务器地址和六位课堂码"; return; }
        try { JoinButton.IsEnabled=false; StatusText.Text="正在匿名加入……"; var result=await _service.JoinAsync(server,new(CodeBox.Text.Trim(),Guid.NewGuid().ToString("N"))); Joined(result); await _service.RefreshAsync(); }
        catch(Exception ex){StatusText.Text=$"加入失败：{ex.Message}";} finally{JoinButton.IsEnabled=true;}
    }
    private async void TestServer_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var server))
        {
            StatusText.Text = "服务器地址格式不正确，应以 http:// 或 https:// 开头";
            return;
        }
        TestServerButton.IsEnabled = false;
        StatusText.Text = "正在检测课堂服务器……";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var statusUri = new Uri(new Uri(server.ToString().TrimEnd('/') + "/"), "api/status");
            using var response = await client.GetAsync(statusUri);
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = $"服务器可访问，但课堂服务返回 {(int)response.StatusCode}";
                return;
            }
            var status = await response.Content.ReadFromJsonAsync<ServerStatus>();
            StatusText.Text = status?.SetupRequired == true
                ? "服务器正常，但老师还没有完成首次建课"
                : "服务器正常，可以输入六位课堂码加入";
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "检测超时：该地址尚未部署、网络不通或服务器未启动";
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = $"无法连接：{ex.Message}。单机同传仍可正常使用";
        }
        finally { TestServerButton.IsEnabled = true; }
    }
    private void Joined(ClassroomJoinResult result){JoinButton.Content="离开云端课堂";ConfusionButton.IsEnabled=true;StatusText.Text=$"已加入 {result.CourseName} · {result.LessonName}";CodeBox.Text=result.LessonId.ToString("N")[..6];}
    private async void Confusion_Click(object sender,RoutedEventArgs e){var room=_service.CurrentClassroom;if(room is null)return;await _service.SendConfusionAsync(new(Guid.NewGuid(),room.LessonId,DateTimeOffset.Now,null,null));StatusText.Text="已匿名告诉老师：这里没听懂";}
    private async void Vote_Click(object sender,RoutedEventArgs e){if(QuestionList.SelectedItem is not QuestionItem item)return;await _service.VoteAsync(new(Guid.NewGuid(),item.Id,DateTimeOffset.Now));StatusText.Text="已点赞";}
    private void SnapshotUpdated(object? sender,ClassroomAggregateSnapshot e)=>Dispatcher.Invoke(()=>{_snapshot=e;QuestionList.ItemsSource=e.Questions.Select(q=>new QuestionItem(q.Id,q.Question,$"👍 {q.Votes} · {(q.IsAddressed?"老师已讲解":"待讲解")} · {(q.SlidePage is null?"无PPT页":$"PPT第{q.SlidePage}页")} · {q.TranscriptTimestamp}"));StatsText.Text=$"{e.OnlineStudents}人在线 · {e.QuestionCount}个问题 · {e.ConfusionCount}次没听懂";});
    private void BroadcastReceived(object? sender,TeacherBroadcast e)=>Dispatcher.Invoke(()=>BroadcastText.Text=$"老师广播：{e.Message}");
    private void ConnectionStatusChanged(object? sender,string e)=>Dispatcher.Invoke(()=>StatusText.Text=e);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private sealed record ServerStatus(string Status, bool SetupRequired, bool SchoolAiConfigured);
    private sealed record QuestionItem(Guid Id,string Question,string Meta);
}
