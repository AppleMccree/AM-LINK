using Microsoft.AspNetCore.SignalR;

namespace ClassInterpreter.ClassroomServer;

public sealed class ClassroomHub(ClassroomStore store) : Hub
{
    public async Task JoinStudent(Guid lessonId, string participantToken)
    {
        if (await store.AuthenticateParticipantAsync(lessonId, participantToken) is null) throw new HubException("课堂身份已失效");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"lesson:{lessonId}");
    }

    public Task JoinTeacher(Guid lessonId, string teacherToken) =>
        TeacherSessionRegistry.TryValidate(teacherToken, lessonId, store).ContinueWith(async task =>
        {
            if (!task.Result) throw new HubException("教师会话已失效");
            await Groups.AddToGroupAsync(Context.ConnectionId, $"lesson:{lessonId}");
        }).Unwrap();
}

public static class TeacherSessionRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TeacherSession> Sessions = new();
    public static string Create(Guid courseId, int version)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Sessions[token] = new(courseId, version, DateTimeOffset.UtcNow.AddHours(12));
        return token;
    }
    public static bool TryGet(string token, out TeacherSession session) => Sessions.TryGetValue(token, out session!) && session.ExpiresAt > DateTimeOffset.UtcNow;
    public static void RevokeCourse(Guid courseId) { foreach(var item in Sessions.Where(item=>item.Value.CourseId==courseId).ToArray()) Sessions.TryRemove(item.Key,out _); }
    public static async Task<bool> TryValidate(string token, Guid lessonId, ClassroomStore store)
    {
        if (!TryGet(token, out var session)) return false;
        var lesson = await store.FindLessonAsync(lessonId);
        return lesson?.CourseId == session.CourseId;
    }
}
