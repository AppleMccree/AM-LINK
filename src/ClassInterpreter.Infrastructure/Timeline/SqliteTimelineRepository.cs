using System.Globalization;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Core.Slides;
using Microsoft.Data.Sqlite;

namespace ClassInterpreter.Infrastructure.Timeline;

public sealed class SqliteTimelineRepository
{
    private readonly string _connectionString;

    public SqliteTimelineRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            INSERT INTO schema_info(version)
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                course_name TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                status INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS courses (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS transcripts (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                start_ticks INTEGER NOT NULL,
                end_ticks INTEGER NOT NULL,
                source_text TEXT NOT NULL,
                chinese_text TEXT NULL,
                target_text TEXT NULL,
                translation_direction TEXT NOT NULL DEFAULT 'mixed-to-chinese',
                is_final INTEGER NOT NULL,
                language TEXT NOT NULL,
                confidence REAL NULL,
                viewed_slide_page INTEGER NULL,
                candidate_slide_page INTEGER NULL,
                slide_match_confidence REAL NULL,
                slide_match_evidence TEXT NULL,
                slide_follow_action INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_transcripts_session_sequence
                ON transcripts(session_id, sequence);
            CREATE TABLE IF NOT EXISTS ai_questions (
                id TEXT PRIMARY KEY,
                lesson_key TEXT NOT NULL,
                course_id TEXT NULL,
                asked_at TEXT NOT NULL,
                question TEXT NOT NULL,
                selected_text TEXT NULL,
                answer TEXT NULL,
                slide_page INTEGER NULL,
                transcript_timestamp TEXT NULL,
                model TEXT NOT NULL,
                status INTEGER NOT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_ai_questions_lesson_time
                ON ai_questions(lesson_key, asked_at);
            CREATE TABLE IF NOT EXISTS ai_usage_daily (
                day TEXT NOT NULL,
                kind INTEGER NOT NULL,
                model TEXT NOT NULL,
                request_count INTEGER NOT NULL DEFAULT 0,
                failure_count INTEGER NOT NULL DEFAULT 0,
                input_characters INTEGER NOT NULL DEFAULT 0,
                output_characters INTEGER NOT NULL DEFAULT 0,
                estimated_input_tokens INTEGER NOT NULL DEFAULT 0,
                estimated_output_tokens INTEGER NOT NULL DEFAULT 0,
                audio_milliseconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(day, kind, model)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "target_text", "TEXT NULL", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "translation_direction", "TEXT NOT NULL DEFAULT 'mixed-to-chinese'", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "viewed_slide_page", "INTEGER NULL", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "candidate_slide_page", "INTEGER NULL", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "slide_match_confidence", "REAL NULL", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "slide_match_evidence", "TEXT NULL", cancellationToken);
        await EnsureTranscriptColumnAsync(connection, "slide_follow_action", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureSessionColumnAsync(connection, "course_id", "TEXT NULL", cancellationToken);
        await EnsureSessionColumnAsync(connection, "material_path", "TEXT NULL", cancellationToken);
        await EnsureSessionColumnAsync(connection, "material_type", "TEXT NULL", cancellationToken);
        await EnsureSessionColumnAsync(connection, "study_pack_path", "TEXT NULL", cancellationToken);
        await EnsureSessionColumnAsync(connection, "lesson_key", "TEXT NULL", cancellationToken);
        await EnsureSessionColumnAsync(connection, "last_slide_page", "INTEGER NULL", cancellationToken);
        await MigrateLegacyCoursesAsync(connection, cancellationToken);
    }

    public async ValueTask UpsertSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, course_name, started_at, ended_at, status, course_id, material_path, material_type, study_pack_path, lesson_key, last_slide_page)
            VALUES ($id, $course, $started, $ended, $status, $courseId, $material, $materialType, $studyPack, $lessonKey, $lastSlidePage)
            ON CONFLICT(id) DO UPDATE SET
                course_name=excluded.course_name,
                started_at=excluded.started_at,
                ended_at=excluded.ended_at,
                status=excluded.status,
                course_id=excluded.course_id,
                material_path=COALESCE(excluded.material_path, sessions.material_path),
                material_type=COALESCE(excluded.material_type, sessions.material_type),
                study_pack_path=COALESCE(excluded.study_pack_path, sessions.study_pack_path),
                lesson_key=COALESCE(excluded.lesson_key, sessions.lesson_key),
                last_slide_page=COALESCE(excluded.last_slide_page, sessions.last_slide_page);
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$course", session.CourseName);
        command.Parameters.AddWithValue("$started", Format(session.StartedAt));
        command.Parameters.AddWithValue("$ended", session.EndedAt is null ? DBNull.Value : Format(session.EndedAt.Value));
        command.Parameters.AddWithValue("$status", (int)session.Status);
        command.Parameters.AddWithValue("$courseId", session.CourseId is null ? DBNull.Value : session.CourseId.Value.ToString("D"));
        command.Parameters.AddWithValue("$material", session.MaterialPath is null ? DBNull.Value : session.MaterialPath);
        command.Parameters.AddWithValue("$materialType", session.MaterialType is null ? DBNull.Value : session.MaterialType);
        command.Parameters.AddWithValue("$studyPack", session.StudyPackPath is null ? DBNull.Value : session.StudyPackPath);
        command.Parameters.AddWithValue("$lessonKey", session.LessonKey is null ? DBNull.Value : session.LessonKey);
        command.Parameters.AddWithValue("$lastSlidePage", session.LastSlidePage is null ? DBNull.Value : session.LastSlidePage.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask UpsertCourseAsync(Course course, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO courses(id, name, created_at, is_archived) VALUES($id, $name, $created, $archived)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, is_archived=excluded.is_archived;
            """;
        command.Parameters.AddWithValue("$id", course.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", course.Name.Trim());
        command.Parameters.AddWithValue("$created", Format(course.CreatedAt));
        command.Parameters.AddWithValue("$archived", course.IsArchived ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Course>> GetCoursesAsync(bool archived, CancellationToken cancellationToken = default)
    {
        var results = new List<Course>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at, is_archived FROM courses WHERE is_archived=$archived ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new Course(Guid.Parse(reader.GetString(0)), reader.GetString(1), Parse(reader.GetString(2)), reader.GetInt64(3) != 0));
        return results;
    }

    public async ValueTask<IReadOnlyList<Session>> GetSessionsForCourseAsync(Guid courseId, CancellationToken cancellationToken = default)
    {
        var results = new List<Session>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, course_name, started_at, ended_at, status, course_id, material_path, material_type, study_pack_path, lesson_key, last_slide_page FROM sessions WHERE course_id=$course ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$course", courseId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadSession(reader));
        for (var index = 0; index < results.Count; index++)
        {
            results[index] = results[index] with { LessonNumber = results.Count - index };
        }
        return results;
    }

    /// <summary>
    /// 每天最多生成一份完整数据库备份（VACUUM INTO 热备份，不锁写入），
    /// 只保留最近 keepCount 份；已存在当天备份时直接跳过。
    /// </summary>
    public async ValueTask<string?> BackupAsync(string backupDirectory, int keepCount = 7, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        Directory.CreateDirectory(backupDirectory);
        var target = Path.Combine(backupDirectory, $"timeline-backup-{DateTime.Now:yyyyMMdd}.db");
        if (!File.Exists(target))
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", target);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var stale in Directory.GetFiles(backupDirectory, "timeline-backup-*.db")
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                     .Skip(Math.Max(1, keepCount)))
        {
            File.Delete(stale);
        }

        return target;
    }

    /// <summary>删除整节课时调用：清掉该课的问AI记录，避免留下无主数据。</summary>
    public async ValueTask DeleteAiQuestionsForLessonAsync(string lessonKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_questions WHERE lesson_key=$lesson;";
        command.Parameters.AddWithValue("$lesson", lessonKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM transcripts WHERE session_id=$id;
            DELETE FROM sessions WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask MergeLessonsAsync(
        Guid courseId,
        string sourceLessonKey,
        string targetLessonKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLessonKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLessonKey);
        if (string.Equals(sourceLessonKey, targetLessonKey, StringComparison.Ordinal)) return;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET lesson_key=$target, study_pack_path=NULL
            WHERE course_id=$course AND lesson_key IN ($source, $target);
            UPDATE ai_questions SET lesson_key=$target WHERE lesson_key=$source;
            """;
        command.Parameters.AddWithValue("$course", courseId.ToString("D"));
        command.Parameters.AddWithValue("$source", sourceLessonKey);
        command.Parameters.AddWithValue("$target", targetLessonKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask UpsertAiQuestionAsync(AiQuestionRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_questions(id, lesson_key, course_id, asked_at, question, selected_text, answer,
                                     slide_page, transcript_timestamp, model, status, error)
            VALUES($id, $lesson, $course, $asked, $question, $selected, $answer,
                   $page, $timestamp, $model, $status, $error)
            ON CONFLICT(id) DO UPDATE SET
                answer=excluded.answer,
                status=excluded.status,
                error=excluded.error,
                slide_page=excluded.slide_page,
                transcript_timestamp=excluded.transcript_timestamp;
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$lesson", record.LessonKey);
        command.Parameters.AddWithValue("$course", record.CourseId is null ? DBNull.Value : record.CourseId.Value.ToString("D"));
        command.Parameters.AddWithValue("$asked", Format(record.AskedAt));
        command.Parameters.AddWithValue("$question", record.Question);
        command.Parameters.AddWithValue("$selected", record.SelectedText is null ? DBNull.Value : record.SelectedText);
        command.Parameters.AddWithValue("$answer", record.Answer is null ? DBNull.Value : record.Answer);
        command.Parameters.AddWithValue("$page", record.SlidePage is null ? DBNull.Value : record.SlidePage.Value);
        command.Parameters.AddWithValue("$timestamp", record.TranscriptTimestamp is null ? DBNull.Value : record.TranscriptTimestamp);
        command.Parameters.AddWithValue("$model", record.Model);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$error", record.Error is null ? DBNull.Value : record.Error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask RecordAiUsageAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_usage_daily(day, kind, model, request_count, failure_count, input_characters,
                                       output_characters, estimated_input_tokens, estimated_output_tokens, audio_milliseconds)
            VALUES($day,$kind,$model,$requests,$failures,$input,$output,$inputTokens,$outputTokens,$audio)
            ON CONFLICT(day,kind,model) DO UPDATE SET
                request_count=request_count+excluded.request_count,
                failure_count=failure_count+excluded.failure_count,
                input_characters=input_characters+excluded.input_characters,
                output_characters=output_characters+excluded.output_characters,
                estimated_input_tokens=estimated_input_tokens+excluded.estimated_input_tokens,
                estimated_output_tokens=estimated_output_tokens+excluded.estimated_output_tokens,
                audio_milliseconds=audio_milliseconds+excluded.audio_milliseconds;
            """;
        command.Parameters.AddWithValue("$day", record.Day.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$kind", (int)record.Kind);
        command.Parameters.AddWithValue("$model", record.Model);
        command.Parameters.AddWithValue("$requests", record.RequestCount);
        command.Parameters.AddWithValue("$failures", record.FailureCount);
        command.Parameters.AddWithValue("$input", record.InputCharacters);
        command.Parameters.AddWithValue("$output", record.OutputCharacters);
        command.Parameters.AddWithValue("$inputTokens", record.EstimatedInputTokens);
        command.Parameters.AddWithValue("$outputTokens", record.EstimatedOutputTokens);
        command.Parameters.AddWithValue("$audio", record.AudioMilliseconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<AiUsageRecord>> GetAiUsageAsync(
        DateOnly? from = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AiUsageRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = from is null
            ? "SELECT day,kind,model,request_count,failure_count,input_characters,output_characters,estimated_input_tokens,estimated_output_tokens,audio_milliseconds FROM ai_usage_daily ORDER BY day DESC,kind;"
            : "SELECT day,kind,model,request_count,failure_count,input_characters,output_characters,estimated_input_tokens,estimated_output_tokens,audio_milliseconds FROM ai_usage_daily WHERE day >= $from ORDER BY day DESC,kind;";
        if (from is not null) command.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new AiUsageRecord(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"), (AiUsageKind)reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9)));
        return results;
    }

    public async ValueTask<IReadOnlyList<AiQuestionRecord>> GetAiQuestionsAsync(
        string lessonKey,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AiQuestionRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, lesson_key, course_id, asked_at, question, selected_text, answer,
                   slide_page, transcript_timestamp, model, status, error
            FROM ai_questions WHERE lesson_key=$lesson ORDER BY asked_at;
            """;
        command.Parameters.AddWithValue("$lesson", lessonKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AiQuestionRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), Parse(reader.GetString(3)),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
                (AiQuestionStatus)reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return results;
    }

    public async ValueTask UpsertTranscriptAsync(TranscriptSegment segment, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcripts(
                id, session_id, sequence, start_ticks, end_ticks, source_text,
                chinese_text, target_text, translation_direction, is_final, language, confidence,
                viewed_slide_page, candidate_slide_page, slide_match_confidence, slide_match_evidence, slide_follow_action)
            VALUES ($id, $session, $sequence, $start, $end, $source,
                    $chinese, $target, $direction, $final, $language, $confidence,
                    $viewedPage, $candidatePage, $matchConfidence, $matchEvidence, $followAction)
            ON CONFLICT(id) DO UPDATE SET
                sequence=excluded.sequence,
                start_ticks=excluded.start_ticks,
                end_ticks=excluded.end_ticks,
                source_text=excluded.source_text,
                chinese_text=excluded.chinese_text,
                target_text=excluded.target_text,
                translation_direction=excluded.translation_direction,
                is_final=excluded.is_final,
                language=excluded.language,
                confidence=excluded.confidence,
                viewed_slide_page=excluded.viewed_slide_page,
                candidate_slide_page=excluded.candidate_slide_page,
                slide_match_confidence=excluded.slide_match_confidence,
                slide_match_evidence=excluded.slide_match_evidence,
                slide_follow_action=excluded.slide_follow_action;
            """;
        command.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
        command.Parameters.AddWithValue("$session", segment.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", segment.Sequence);
        command.Parameters.AddWithValue("$start", segment.Start.Ticks);
        command.Parameters.AddWithValue("$end", segment.End.Ticks);
        command.Parameters.AddWithValue("$source", segment.SourceText);
        command.Parameters.AddWithValue("$chinese", segment.ChineseText is null ? DBNull.Value : segment.ChineseText);
        command.Parameters.AddWithValue("$target", segment.TargetText is null ? DBNull.Value : segment.TargetText);
        command.Parameters.AddWithValue("$direction", segment.TranslationDirectionId);
        command.Parameters.AddWithValue("$final", segment.IsFinal ? 1 : 0);
        command.Parameters.AddWithValue("$language", segment.Language);
        command.Parameters.AddWithValue("$confidence", segment.Confidence is null ? DBNull.Value : segment.Confidence.Value);
        command.Parameters.AddWithValue("$viewedPage", segment.ViewedSlidePage is null ? DBNull.Value : segment.ViewedSlidePage.Value);
        command.Parameters.AddWithValue("$candidatePage", segment.CandidateSlidePage is null ? DBNull.Value : segment.CandidateSlidePage.Value);
        command.Parameters.AddWithValue("$matchConfidence", segment.SlideMatchConfidence is null ? DBNull.Value : segment.SlideMatchConfidence.Value);
        command.Parameters.AddWithValue("$matchEvidence", segment.SlideMatchEvidence is null ? DBNull.Value : segment.SlideMatchEvidence);
        command.Parameters.AddWithValue("$followAction", (int)segment.SlideFollowAction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask UpdateTranscriptSlideLinkAsync(
        Guid transcriptId,
        int viewedSlidePage,
        SlideFollowAction action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transcripts
            SET viewed_slide_page=$viewedPage, slide_follow_action=$action
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", transcriptId.ToString("D"));
        command.Parameters.AddWithValue("$viewedPage", viewedSlidePage);
        command.Parameters.AddWithValue("$action", (int)action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<TranscriptSegment>> GetTranscriptsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TranscriptSegment>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, sequence, start_ticks, end_ticks, source_text,
                   chinese_text, target_text, translation_direction, is_final, language, confidence,
                   viewed_slide_page, candidate_slide_page, slide_match_confidence, slide_match_evidence, slide_follow_action
            FROM transcripts WHERE session_id=$session ORDER BY sequence, start_ticks;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TranscriptSegment(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                TimeSpan.FromTicks(reader.GetInt64(3)), TimeSpan.FromTicks(reader.GetInt64(4)),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(9) != 0, reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetDouble(11))
            {
                TargetText = reader.IsDBNull(7) ? null : reader.GetString(7),
                TranslationDirectionId = reader.GetString(8),
                ViewedSlidePage = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                CandidateSlidePage = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                SlideMatchConfidence = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                SlideMatchEvidence = reader.IsDBNull(15) ? null : reader.GetString(15),
                SlideFollowAction = reader.IsDBNull(16) ? SlideFollowAction.None : (SlideFollowAction)reader.GetInt32(16)
            });
        }

        return results;
    }

    public async ValueTask<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, course_name, started_at, ended_at, status, course_id, material_path, material_type, study_pack_path, lesson_key, last_slide_page FROM sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSession(reader);
    }

    public async ValueTask MarkOpenSessionsInterruptedAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions SET status=$interrupted, ended_at=$ended
            WHERE status IN ($preparing, $live, $paused);
            """;
        command.Parameters.AddWithValue("$interrupted", (int)SessionStatus.Interrupted);
        command.Parameters.AddWithValue("$ended", Format(recoveredAt));
        command.Parameters.AddWithValue("$preparing", (int)SessionStatus.Preparing);
        command.Parameters.AddWithValue("$live", (int)SessionStatus.Live);
        command.Parameters.AddWithValue("$paused", (int)SessionStatus.Paused);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask EnsureSessionColumnAsync(SqliteConnection connection, string columnName, string definition, CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(sessions);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE sessions ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask MigrateLegacyCoursesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO courses(id, name, created_at, is_archived)
            SELECT lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-a' || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6))),
                   trim(course_name), min(started_at), 0
            FROM sessions WHERE course_id IS NULL GROUP BY trim(course_name);
            UPDATE sessions SET course_id=(SELECT id FROM courses WHERE name=trim(sessions.course_name) COLLATE NOCASE ORDER BY created_at LIMIT 1)
            WHERE course_id IS NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask EnsureTranscriptColumnAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(transcripts);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE transcripts ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Session ReadSession(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), (SessionStatus)reader.GetInt32(4))
    {
        CourseId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
        MaterialPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        MaterialType = reader.IsDBNull(7) ? null : reader.GetString(7),
        StudyPackPath = reader.IsDBNull(8) ? null : reader.GetString(8),
        LessonKey = reader.IsDBNull(9) ? null : reader.GetString(9),
        LastSlidePage = reader.IsDBNull(10) ? null : reader.GetInt32(10)
    };

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
