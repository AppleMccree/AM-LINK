using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ClassInterpreter.ClassroomServer;

public sealed record TeacherSession(Guid CourseId, int PasswordVersion, DateTimeOffset ExpiresAt);
public sealed record LessonRecord(Guid Id, Guid CourseId, string CourseName, string Name, string Code, DateTimeOffset? EndedAt = null);

public sealed class ClassroomStore(IConfiguration configuration)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(configuration["Classroom:Database"] ?? "data/classrooms.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS courses(id TEXT PRIMARY KEY,name TEXT NOT NULL,password_hash TEXT NOT NULL,password_salt TEXT NOT NULL,password_version INTEGER NOT NULL DEFAULT 1,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS lessons(id TEXT PRIMARY KEY,course_id TEXT NOT NULL,name TEXT NOT NULL,code TEXT NOT NULL UNIQUE,started_at TEXT NOT NULL,ended_at TEXT NULL,FOREIGN KEY(course_id) REFERENCES courses(id));
            CREATE TABLE IF NOT EXISTS participants(id TEXT PRIMARY KEY,lesson_id TEXT NOT NULL,token_hash TEXT NOT NULL,last_seen TEXT NOT NULL,joined_at TEXT NOT NULL,FOREIGN KEY(lesson_id) REFERENCES lessons(id));
            CREATE TABLE IF NOT EXISTS questions(id TEXT PRIMARY KEY,event_id TEXT NOT NULL UNIQUE,lesson_id TEXT NOT NULL,participant_id TEXT NOT NULL,question TEXT NOT NULL,asked_at TEXT NOT NULL,transcript_timestamp TEXT NULL,slide_page INTEGER NULL,selected_context TEXT NULL,votes INTEGER NOT NULL DEFAULT 0,pinned INTEGER NOT NULL DEFAULT 0,addressed INTEGER NOT NULL DEFAULT 0,topic TEXT NOT NULL DEFAULT '其他',FOREIGN KEY(lesson_id) REFERENCES lessons(id));
            CREATE TABLE IF NOT EXISTS votes(event_id TEXT NOT NULL UNIQUE,question_id TEXT NOT NULL,participant_id TEXT NOT NULL,voted_at TEXT NOT NULL,UNIQUE(question_id,participant_id));
            CREATE TABLE IF NOT EXISTS confusions(event_id TEXT NOT NULL UNIQUE,lesson_id TEXT NOT NULL,participant_id TEXT NOT NULL,occurred_at TEXT NOT NULL,transcript_timestamp TEXT NULL,slide_page INTEGER NULL);
            CREATE TABLE IF NOT EXISTS broadcasts(id TEXT PRIMARY KEY,lesson_id TEXT NOT NULL,message TEXT NOT NULL,sent_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_questions_lesson ON questions(lesson_id,asked_at);
            CREATE INDEX IF NOT EXISTS ix_participants_online ON participants(lesson_id,last_seen);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Guid> CreateCourseAsync(string name, string password)
    {
        var id = Guid.NewGuid();
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = HashPassword(password, salt);
        await ExecuteAsync("INSERT INTO courses(id,name,password_hash,password_salt,created_at) VALUES($id,$name,$hash,$salt,$now)",
            ("$id", id), ("$name", name.Trim()), ("$hash", Convert.ToBase64String(hash)), ("$salt", Convert.ToBase64String(salt)), ("$now", DateTimeOffset.UtcNow));
        return id;
    }

    public Task<long> CountCoursesAsync() => ScalarAsync<long>("SELECT COUNT(*) FROM courses");

    public async Task<(Guid CourseId, int Version)?> VerifyTeacherAsync(string courseName, string password)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,password_hash,password_salt,password_version FROM courses WHERE name=$name ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$name", courseName.Trim());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var salt = Convert.FromBase64String(reader.GetString(2));
        var expected = Convert.FromBase64String(reader.GetString(1));
        return CryptographicOperations.FixedTimeEquals(expected, HashPassword(password, salt))
            ? (Guid.Parse(reader.GetString(0)), reader.GetInt32(3)) : null;
    }

    public async Task<LessonRecord> CreateLessonAsync(Guid courseId, string name)
    {
        var id = Guid.NewGuid();
        string code;
        do code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        while (await ScalarAsync<long>("SELECT COUNT(*) FROM lessons WHERE code=$code AND ended_at IS NULL", ("$code", code)) > 0);
        await ExecuteAsync("INSERT INTO lessons(id,course_id,name,code,started_at) VALUES($id,$course,$name,$code,$now)",
            ("$id", id), ("$course", courseId), ("$name", name.Trim()), ("$code", code), ("$now", DateTimeOffset.UtcNow));
        var course = await ScalarAsync<string>("SELECT name FROM courses WHERE id=$id", ("$id", courseId)) ?? string.Empty;
        return new(id, courseId, course, name.Trim(), code);
    }

    public async Task<LessonRecord?> FindLessonByCodeAsync(string code)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT l.id,l.course_id,c.name,l.name,l.code FROM lessons l JOIN courses c ON c.id=l.course_id WHERE l.code=$code AND l.ended_at IS NULL";
        command.Parameters.AddWithValue("$code", code);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    public async Task<LessonRecord?> FindLessonAsync(Guid lessonId)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT l.id,l.course_id,c.name,l.name,l.code FROM lessons l JOIN courses c ON c.id=l.course_id WHERE l.id=$id";
        command.Parameters.AddWithValue("$id", lessonId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    public async Task<IReadOnlyList<LessonRecord>> GetActiveLessonsAsync(Guid courseId)
    {
        var items = new List<LessonRecord>();
        await using var connection = await OpenAsync();
        var command=connection.CreateCommand(); command.CommandText="SELECT l.id,l.course_id,c.name,l.name,l.code FROM lessons l JOIN courses c ON c.id=l.course_id WHERE l.course_id=$course AND l.ended_at IS NULL ORDER BY l.started_at DESC"; command.Parameters.AddWithValue("$course",courseId.ToString());
        await using var reader=await command.ExecuteReaderAsync(); while(await reader.ReadAsync()) items.Add(new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        return items;
    }

    public async Task<IReadOnlyList<LessonRecord>> GetLessonsAsync(Guid courseId)
    {
        var items = new List<LessonRecord>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT l.id,l.course_id,c.name,l.name,l.code,l.ended_at FROM lessons l JOIN courses c ON c.id=l.course_id WHERE l.course_id=$course ORDER BY l.started_at DESC";
        command.Parameters.AddWithValue("$course", courseId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
        }
        return items;
    }

    public async Task ChangeTeacherPasswordAsync(Guid courseId,string password)
    {
        var salt=RandomNumberGenerator.GetBytes(24); var hash=HashPassword(password,salt);
        await ExecuteAsync("UPDATE courses SET password_hash=$hash,password_salt=$salt,password_version=password_version+1 WHERE id=$id",("$hash",Convert.ToBase64String(hash)),("$salt",Convert.ToBase64String(salt)),("$id",courseId));
    }

    public async Task<(Guid ParticipantId, string Token)> JoinAsync(Guid lessonId)
    {
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await ExecuteAsync("INSERT INTO participants(id,lesson_id,token_hash,last_seen,joined_at) VALUES($id,$lesson,$token,$now,$now)",
            ("$id", id), ("$lesson", lessonId), ("$token", HashToken(token)), ("$now", DateTimeOffset.UtcNow));
        return (id, token);
    }

    public async Task<Guid?> AuthenticateParticipantAsync(Guid lessonId, string token, bool touch = true)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM participants WHERE lesson_id=$lesson AND token_hash=$token LIMIT 1";
        command.Parameters.AddWithValue("$lesson", lessonId.ToString()); command.Parameters.AddWithValue("$token", HashToken(token));
        var value = await command.ExecuteScalarAsync();
        if (value is null) return null;
        var id = Guid.Parse((string)value);
        if (touch) await ExecuteAsync("UPDATE participants SET last_seen=$now WHERE id=$id", ("$now", DateTimeOffset.UtcNow), ("$id", id));
        return id;
    }

    public Task AddQuestionAsync(Guid participantId, ClassroomQuestionEvent item) => ExecuteAsync(
        "INSERT OR IGNORE INTO questions(id,event_id,lesson_id,participant_id,question,asked_at,transcript_timestamp,slide_page,selected_context,topic) VALUES($id,$event,$lesson,$participant,$question,$at,$ts,$page,$context,$topic)",
        ("$id", Guid.NewGuid()), ("$event", item.EventId), ("$lesson", item.LessonId), ("$participant", participantId), ("$question", item.Question.Trim()), ("$at", item.AskedAt), ("$ts", item.TranscriptTimestamp), ("$page", item.SlidePage), ("$context", Trim(item.SelectedContext, 500)), ("$topic", Topic(item.Question)));

    public async Task AddVoteAsync(Guid participantId, QuestionVote vote)
    {
        var changed = await ExecuteAsync("INSERT OR IGNORE INTO votes(event_id,question_id,participant_id,voted_at) VALUES($event,$question,$participant,$at)",
            ("$event", vote.EventId), ("$question", vote.QuestionId), ("$participant", participantId), ("$at", vote.VotedAt));
        if (changed > 0) await ExecuteAsync("UPDATE questions SET votes=votes+1 WHERE id=$id", ("$id", vote.QuestionId));
    }

    public Task AddConfusionAsync(Guid participantId, ConfusionSignal item) => ExecuteAsync(
        "INSERT OR IGNORE INTO confusions(event_id,lesson_id,participant_id,occurred_at,transcript_timestamp,slide_page) VALUES($event,$lesson,$participant,$at,$ts,$page)",
        ("$event", item.EventId), ("$lesson", item.LessonId), ("$participant", participantId), ("$at", item.OccurredAt), ("$ts", item.TranscriptTimestamp), ("$page", item.SlidePage));

    public async Task<ClassroomAggregateSnapshot> SnapshotAsync(Guid lessonId)
    {
        var questions = new List<ClassroomQuestionView>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,question,asked_at,transcript_timestamp,slide_page,selected_context,votes,pinned,addressed,topic FROM questions WHERE lesson_id=$lesson ORDER BY pinned DESC,votes DESC,asked_at DESC";
        command.Parameters.AddWithValue("$lesson", lessonId.ToString());
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) questions.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7) != 0, reader.GetInt32(8) != 0, reader.GetString(9)));
        var broadcasts = new List<TeacherBroadcast>();
        var bc = connection.CreateCommand(); bc.CommandText = "SELECT id,message,sent_at FROM broadcasts WHERE lesson_id=$lesson ORDER BY sent_at DESC LIMIT 20"; bc.Parameters.AddWithValue("$lesson", lessonId.ToString());
        await using (var reader = await bc.ExecuteReaderAsync()) while (await reader.ReadAsync()) broadcasts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))));
        var online = await ScalarAsync<long>("SELECT COUNT(*) FROM participants WHERE lesson_id=$lesson AND last_seen >= $cutoff", ("$lesson", lessonId), ("$cutoff", DateTimeOffset.UtcNow.AddSeconds(-45)));
        var askers = await ScalarAsync<long>("SELECT COUNT(DISTINCT participant_id) FROM questions WHERE lesson_id=$lesson", ("$lesson", lessonId));
        var confusion = await ScalarAsync<long>("SELECT COUNT(*) FROM confusions WHERE lesson_id=$lesson", ("$lesson", lessonId));
        return new((int)online, questions.Count, (int)askers, questions.Count(q => !q.IsAddressed), (int)confusion, questions, broadcasts);
    }

    public Task SetQuestionStateAsync(Guid id, string field, bool value)
    {
        if (field is not ("pinned" or "addressed")) throw new ArgumentException("Unsupported field", nameof(field));
        return ExecuteAsync($"UPDATE questions SET {field}=$value WHERE id=$id", ("$value", value ? 1 : 0), ("$id", id));
    }
    public async Task<TeacherBroadcast> BroadcastAsync(Guid lessonId, string message)
    {
        var item = new TeacherBroadcast(Guid.NewGuid(), message.Trim(), DateTimeOffset.UtcNow);
        await ExecuteAsync("INSERT INTO broadcasts(id,lesson_id,message,sent_at) VALUES($id,$lesson,$message,$at)", ("$id", item.Id), ("$lesson", lessonId), ("$message", item.Message), ("$at", item.SentAt));
        return item;
    }
    public Task EndLessonAsync(Guid lessonId) => ExecuteAsync("UPDATE lessons SET ended_at=$now WHERE id=$id", ("$now", DateTimeOffset.UtcNow), ("$id", lessonId));
    public Task DeleteLessonAsync(Guid lessonId) => ExecuteAsync("DELETE FROM lessons WHERE id=$id", ("$id", lessonId));

    private async Task<SqliteConnection> OpenAsync() { var c = new SqliteConnection(_connectionString); await c.OpenAsync(); return c; }
    private async Task<int> ExecuteAsync(string sql, params (string, object?)[] args) { await using var c = await OpenAsync(); var cmd = c.CreateCommand(); cmd.CommandText = sql; Add(cmd, args); return await cmd.ExecuteNonQueryAsync(); }
    private async Task<T?> ScalarAsync<T>(string sql, params (string, object?)[] args) { await using var c = await OpenAsync(); var cmd = c.CreateCommand(); cmd.CommandText = sql; Add(cmd, args); var v = await cmd.ExecuteScalarAsync(); return v is null or DBNull ? default : (T)Convert.ChangeType(v, typeof(T)); }
    private static void Add(SqliteCommand cmd, IEnumerable<(string Name, object? Value)> args) { foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid g => g.ToString(), DateTimeOffset d => d.ToString("O"), _ => value }); }
    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    private static string? Trim(string? value, int limit) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(limit, value.Trim().Length)];
    private static string Topic(string text) => text.Contains("作业") ? "作业" : text.Contains("公式") || text.Contains("计算") ? "公式与计算" : text.Contains("概念") || text.Contains("什么") ? "概念理解" : "课堂内容";
}
