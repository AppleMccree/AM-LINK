using System.Text;
using ClassInterpreter.ClassroomServer;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ClassroomStore>();
builder.Services.AddSingleton<SchoolQwenService>();
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));
var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
var store = app.Services.GetRequiredService<ClassroomStore>();
await store.InitializeAsync();
var bootstrapKey = builder.Configuration["Classroom:BootstrapKey"] ?? "am-link-local-setup";
if (app.Environment.IsProduction() &&
    (bootstrapKey == "change-this-before-deployment" || bootstrapKey == "am-link-local-setup"))
    throw new InvalidOperationException("生产环境必须通过 Classroom__BootstrapKey 设置强随机初始化密钥");
if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    var courseName = $"并发测试-{Guid.NewGuid():N}";
    var courseId = await store.CreateCourseAsync(courseName, "teacher-test-123");
    var verified = await store.VerifyTeacherAsync(courseName, "teacher-test-123") ?? throw new InvalidOperationException("教师密码验证失败");
    var teacherTokens = Enumerable.Range(0, 3).Select(_ => TeacherSessionRegistry.Create(verified.CourseId, verified.Version)).ToArray();
    if (teacherTokens.Distinct().Count() != 3) throw new InvalidOperationException("多教师会话创建失败");
    var lesson = await store.CreateLessonAsync(courseId, "100人联机测试");
    var started = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 100; i++)
    {
        var student = await store.JoinAsync(lesson.Id);
        var participant = await store.AuthenticateParticipantAsync(lesson.Id, student.Token) ?? throw new InvalidOperationException("匿名学生认证失败");
        await store.AddQuestionAsync(participant, new(Guid.NewGuid(), lesson.Id, $"匿名问题 {i + 1}", DateTimeOffset.UtcNow, $"{i / 60:D2}:{i % 60:D2}", i % 20 + 1, "仅少量选中上下文"));
    }
    var snapshot = await store.SnapshotAsync(lesson.Id);
    if (snapshot.OnlineStudents != 100 || snapshot.QuestionCount != 100 || snapshot.AnonymousAskers != 100) throw new InvalidOperationException("100人统计不正确");
    Console.WriteLine($"SELF_TEST_OK students={snapshot.OnlineStudents} teachers=3 questions={snapshot.QuestionCount} elapsed_ms={started.ElapsedMilliseconds}");
    return;
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "AM-LINK Classroom", time = DateTimeOffset.UtcNow }));
app.MapGet("/api/status", async () => Results.Ok(new
{
    status = "ok",
    service = "AM-LINK Classroom",
    setupRequired = await store.CountCoursesAsync() == 0,
    schoolAiConfigured = app.Services.GetRequiredService<SchoolQwenService>().IsConfigured
}));
app.MapPost("/api/setup/course", async (SetupCourseRequest request, IConfiguration config) =>
{
    var expected = config["Classroom:BootstrapKey"] ?? "am-link-local-setup";
    if (!CryptographicEquals(request.BootstrapKey, expected)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Password.Length < 6) return Results.BadRequest(new { error = "课程名称必填，教师密码至少6位" });
    var id = await store.CreateCourseAsync(request.Name, request.Password);
    return Results.Ok(new { courseId = id, message = "课程已创建" });
});
app.MapPost("/api/teacher/login", async (TeacherLoginRequest request) =>
{
    var verified = await store.VerifyTeacherAsync(request.CourseName, request.Password);
    return verified is null ? Results.Unauthorized() : Results.Ok(new { teacherToken = TeacherSessionRegistry.Create(verified.Value.CourseId, verified.Value.Version), courseId = verified.Value.CourseId });
});
app.MapPost("/api/teacher/lessons", async (CreateLessonRequest request, HttpRequest http) =>
{
    if (!Teacher(http, out var teacher)) return Results.Unauthorized();
    var lesson = await store.CreateLessonAsync(teacher.CourseId, request.Name);
    return Results.Ok(lesson);
});
app.MapGet("/api/teacher/lessons", async (HttpRequest http) => Teacher(http,out var teacher) ? Results.Ok(await store.GetLessonsAsync(teacher.CourseId)) : Results.Unauthorized());
app.MapPost("/api/teacher/password", async (PasswordRequest request,HttpRequest http) =>
{
    if(!Teacher(http,out var teacher))return Results.Unauthorized();
    if(request.NewPassword.Length<6)return Results.BadRequest(new{error="新密码至少6位"});
    await store.ChangeTeacherPasswordAsync(teacher.CourseId,request.NewPassword); TeacherSessionRegistry.RevokeCourse(teacher.CourseId); return Results.Ok(new{message="密码已修改，全部教师会话已退出"});
});
app.MapGet("/api/teacher/lessons/{lessonId:guid}/snapshot", async (Guid lessonId, HttpRequest http) =>
    await TeacherForLesson(http, lessonId) ? Results.Ok(await store.SnapshotAsync(lessonId)) : Results.Unauthorized());
app.MapPost("/api/teacher/lessons/{lessonId:guid}/questions/{questionId:guid}/{field}", async (Guid lessonId, Guid questionId, string field, StateRequest request, HttpRequest http, IHubContext<ClassroomHub> hub) =>
{
    if (!await TeacherForLesson(http, lessonId) || field is not ("pinned" or "addressed")) return Results.Unauthorized();
    await store.SetQuestionStateAsync(questionId, field, request.Value);
    await BroadcastSnapshot(lessonId, hub);
    return Results.Ok();
});
app.MapPost("/api/teacher/lessons/{lessonId:guid}/broadcast", async (Guid lessonId, MessageRequest request, HttpRequest http, IHubContext<ClassroomHub> hub) =>
{
    if (!await TeacherForLesson(http, lessonId)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest();
    var item = await store.BroadcastAsync(lessonId, request.Message);
    await hub.Clients.Group($"lesson:{lessonId}").SendAsync("TeacherBroadcast", item);
    return Results.Ok(item);
});
app.MapGet("/api/teacher/lessons/{lessonId:guid}/export.csv", async (Guid lessonId, HttpRequest http) =>
{
    if (!await TeacherForLesson(http, lessonId)) return Results.Unauthorized();
    var snapshot = await store.SnapshotAsync(lessonId);
    var csv = new StringBuilder("时间,问题,PPT页,时间戳,点赞,主题,已讲解\r\n");
    foreach (var q in snapshot.Questions) csv.Append(Csv(q.AskedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(q.Question)).Append(',').Append(q.SlidePage).Append(',').Append(Csv(q.TranscriptTimestamp)).Append(',').Append(q.Votes).Append(',').Append(Csv(q.Topic)).Append(',').Append(q.IsAddressed ? "是" : "否").Append("\r\n");
    return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", $"AM-LINK-{lessonId}.csv");
});
app.MapDelete("/api/teacher/lessons/{lessonId:guid}", async (Guid lessonId, HttpRequest http) => { if (!await TeacherForLesson(http, lessonId)) return Results.Unauthorized(); await store.DeleteLessonAsync(lessonId); return Results.Ok(); });
app.MapPost("/api/teacher/lessons/{lessonId:guid}/end", async (Guid lessonId, HttpRequest http) =>
{
    if (!await TeacherForLesson(http, lessonId)) return Results.Unauthorized();
    await store.EndLessonAsync(lessonId);
    return Results.Ok(new { message = "课堂已结束，学生将不能再加入" });
});

app.MapPost("/api/classrooms/join", async (ClassroomJoinRequest request) =>
{
    var lesson = await store.FindLessonByCodeAsync(request.ClassroomCode.Trim());
    if (lesson is null) return Results.NotFound(new { error = "课堂码不存在或课堂已结束" });
    var participant = await store.JoinAsync(lesson.Id);
    return Results.Ok(new ClassroomJoinResult(lesson.Id, lesson.CourseName, lesson.Name, participant.Token, DateTimeOffset.UtcNow));
});
app.MapPost("/api/classrooms/{lessonId:guid}/heartbeat", async (Guid lessonId, HttpRequest http) => await Participant(http, lessonId) is not null ? Results.Ok() : Results.Unauthorized());
app.MapGet("/api/classrooms/{lessonId:guid}/snapshot", async (Guid lessonId, HttpRequest http) => await Participant(http, lessonId) is not null ? Results.Ok(await store.SnapshotAsync(lessonId)) : Results.Unauthorized());
app.MapPost("/api/classrooms/{lessonId:guid}/questions", async (Guid lessonId, ClassroomQuestionEvent item, HttpRequest http, IHubContext<ClassroomHub> hub) =>
{
    var participant = await Participant(http, lessonId); if (participant is null || item.LessonId != lessonId) return Results.Unauthorized();
    await store.AddQuestionAsync(participant.Value, item); await BroadcastSnapshot(lessonId, hub); return Results.Accepted();
});
app.MapPost("/api/classrooms/{lessonId:guid}/votes", async (Guid lessonId, QuestionVote item, HttpRequest http, IHubContext<ClassroomHub> hub) =>
{
    var participant = await Participant(http, lessonId); if (participant is null) return Results.Unauthorized();
    await store.AddVoteAsync(participant.Value, item); await BroadcastSnapshot(lessonId, hub); return Results.Accepted();
});
app.MapPost("/api/classrooms/{lessonId:guid}/confusions", async (Guid lessonId, ConfusionSignal item, HttpRequest http, IHubContext<ClassroomHub> hub) =>
{
    var participant = await Participant(http, lessonId); if (participant is null || item.LessonId != lessonId) return Results.Unauthorized();
    await store.AddConfusionAsync(participant.Value, item); await BroadcastSnapshot(lessonId, hub); return Results.Accepted();
});
app.MapPost("/api/classrooms/{lessonId:guid}/school-ai", async (Guid lessonId, SchoolAiRequest item, HttpRequest http, SchoolQwenService qwen) =>
{
    var participant = await Participant(http, lessonId); if (participant is null) return Results.Unauthorized();
    if (!qwen.IsConfigured) return Results.Problem("本课堂未配置学校统一千问Key", statusCode:503);
    if (string.IsNullOrWhiteSpace(item.Prompt) || item.Prompt.Length > 30_000) return Results.BadRequest(new { error="问题上下文为空或过长" });
    try { return Results.Ok(new { answer=await qwen.AskAsync(lessonId,participant.Value,item.Prompt,http.HttpContext.RequestAborted) }); }
    catch(SchoolQwenLimitException ex){return Results.Problem(ex.Message,statusCode:429);}
});
app.MapHub<ClassroomHub>("/classroomHub");
app.MapFallbackToFile("index.html");
await app.RunAsync();

bool Teacher(HttpRequest request, out TeacherSession session) => TeacherSessionRegistry.TryGet(Bearer(request), out session);
async Task<bool> TeacherForLesson(HttpRequest request, Guid lessonId) => await TeacherSessionRegistry.TryValidate(Bearer(request), lessonId, store);
async Task<Guid?> Participant(HttpRequest request, Guid lessonId) => await store.AuthenticateParticipantAsync(lessonId, Bearer(request));
async Task BroadcastSnapshot(Guid lessonId, IHubContext<ClassroomHub> hub) => await hub.Clients.Group($"lesson:{lessonId}").SendAsync("SnapshotUpdated", await store.SnapshotAsync(lessonId));
static string Bearer(HttpRequest r) => r.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
static bool CryptographicEquals(string a, string b) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

public sealed record SetupCourseRequest(string Name, string Password, string BootstrapKey);
public sealed record TeacherLoginRequest(string CourseName, string Password);
public sealed record CreateLessonRequest(string Name);
public sealed record StateRequest(bool Value);
public sealed record MessageRequest(string Message);
public sealed record SchoolAiRequest(string Prompt);
public sealed record PasswordRequest(string NewPassword);
