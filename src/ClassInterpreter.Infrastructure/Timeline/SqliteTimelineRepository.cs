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
   ï½m¢G§²ÚîÆ­yÖÇ”Æ—7CÄ•W6vU&V6÷&CãâvWD•W6vT7–æ2€¢FFTöæÇ“òg&öÒÒçVÆÂÀ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢f"&W7VÇG2ÒæWrÆ—7CÄ•W6vU&V6÷&Câ‚“°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒg&öÒ—2çVÆÀ¢ò%4TÄT5BF’Æ¶–æBÆÖöFVÂÇ&WVW7Eö6÷VçBÆf–ÇW&Uö6÷VçBÆ–çWEö6†&7FW'2Æ÷WGWEö6†&7FW'2ÆW7F–ÖFVEö–çWE÷Fö¶Vç2ÆW7F–ÖFVEö÷WGWE÷Fö¶Vç2ÆVF–õöÖ–ÆÆ—6V6öæG2e$ôÒ•÷W6vUöF–Ç’õ$DU"%’F’DU42Æ¶–æC² ¢¢%4TÄT5BF’Æ¶–æBÆÖöFVÂÇ&WVW7Eö6÷VçBÆf–ÇW&Uö6÷VçBÆ–çWEö6†&7FW'2Æ÷WGWEö6†&7FW'2ÆW7F–ÖFVEö–çWE÷Fö¶Vç2ÆW7F–ÖFVEö÷WGWE÷Fö¶Vç2ÆVF–õöÖ–ÆÆ—6V6öæG2e$ôÒ•÷W6vUöF–Ç’t„U$RF’ãÒFg&öÒõ$DU"%’F’DU42Æ¶–æC²#°¢–b†g&öÒ—2æ÷BçVÆÂ’6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"Fg&öÒ"Âg&öÒåfÇVRåFõ7G&–ær‚'———’ÔÔÒÖFB"’“°¢v—BW6–ærf"&VFW"Òv—B6öÖÖæBäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v†–ÆR†v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢&W7VÇG2äFB†æWr•W6vU&V6÷&B€¢FFTöæÇ’å'6TW†7B‡&VFW"ävWE7G&–ærƒ’Â'———’ÔÔÒÖFB"’Â„•W6vT¶–æB—&VFW"ävWD–çC3"ƒ’Â&VFW"ävWE7G&–ærƒ"’À¢&VFW"ävWD–çCcBƒ2’Â&VFW"ävWD–çCcBƒB’Â&VFW"ävWD–çCcBƒR’Â&VFW"ävWD–çCcBƒb’À¢&VFW"ävWD–çCcBƒr’Â&VFW"ävWD–çCcBƒ‚’Â&VFW"ävWD–çCcBƒ’’’“°¢&WGW&â&W7VÇG3°¢Ð ¢V&Æ–27–æ2fÇVUF6³Ä•&VDöæÇ”Æ—7CÄ•VW7F–öå&V6÷&CãâvWD•VW7F–öç47–æ2€¢7G&–ærÆW76öä¶W’À¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢f"&W7VÇG2ÒæWrÆ—7CÄ•VW7F–öå&V6÷&Câ‚“°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢4TÄT5B–BÂÆW76öåö¶W’Â6÷W'6Uö–BÂ6¶VEöBÂVW7F–öâÂ6VÆV7FVE÷FW‡BÂç7vW"À¢6Æ–FU÷vRÂG&ç67&—E÷F–ÖW7F×ÂÖöFVÂÂ7FGW2ÂW'&÷ ¢e$ôÒ•÷VW7F–öç2t„U$RÆW76öåö¶W“ÒFÆW76öâõ$DU"%’6¶VEöC°¢""#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FÆW76öâ"ÂÆW76öä¶W’“°¢v—BW6–ærf"&VFW"Òv—B6öÖÖæBäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v†–ÆR†v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢°¢&W7VÇG2äFB†æWr•VW7F–öå&V6÷&B€¢wV–Bå'6R‡&VFW"ävWE7G&–ærƒ’’Â&VFW"ävWE7G&–ærƒ’À¢&VFW"ä—4D$çVÆÂƒ"’òçVÆÂ¢wV–Bå'6R‡&VFW"ävWE7G&–ærƒ"’’Â'6R‡&VFW"ävWE7G&–ærƒ2’’À¢&VFW"ävWE7G&–ærƒB’Â&VFW"ä—4D$çVÆÂƒR’òçVÆÂ¢&VFW"ävWE7G&–ærƒR’À¢&VFW"ä—4D$çVÆÂƒb’òçVÆÂ¢&VFW"ävWE7G&–ærƒb’Â&VFW"ä—4D$çVÆÂƒr’òçVÆÂ¢&VFW"ävWD–çC3"ƒr’À¢&VFW"ä—4D$çVÆÂƒ‚’òçVÆÂ¢&VFW"ävWE7G&–ærƒ‚’Â&VFW"ävWE7G&–ærƒ’’À¢„•VW7F–öå7FGW2—&VFW"ävWD–çC3"ƒ’Â&VFW"ä—4D$çVÆÂƒ’òçVÆÂ¢&VFW"ävWE7G&–ærƒ’’“°¢Ð¢&WGW&â&W7VÇG3°¢Ð ¢V&Æ–27–æ2fÇVUF6²W6W'EG&ç67&—D7–æ2…G&ç67&—E6VvÖVçB6VvÖVçBÂ6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢”å4U%B”åDòG&ç67&—G2€¢–BÂ6W76–öåö–BÂ6WVVæ6RÂ7F'E÷F–6·2ÂVæE÷F–6·2Â6÷W&6U÷FW‡BÀ¢6†–æW6U÷FW‡BÂF&vWE÷FW‡BÂG&ç6ÆF–öåöF—&V7F–öâÂ—5öf–æÂÂÆæwVvRÂ6öæf–FVæ6RÀ¢f–WvVE÷6Æ–FU÷vRÂ6æF–FFU÷6Æ–FU÷vRÂ6Æ–FUöÖF6…ö6öæf–FVæ6RÂ6Æ–FUöÖF6…öWf–FVæ6RÂ6Æ–FUöföÆÆ÷uö7F–öâ¢dÅTU2‚F–BÂG6W76–öâÂG6WVVæ6RÂG7F'BÂFVæBÂG6÷W&6RÀ¢F6†–æW6RÂGF&vWBÂFF—&V7F–öâÂFf–æÂÂFÆæwVvRÂF6öæf–FVæ6RÀ¢Gf–WvVEvRÂF6æF–FFUvRÂFÖF6„6öæf–FVæ6RÂFÖF6„Wf–FVæ6RÂFföÆÆ÷t7F–öâ¢ôâ4ôädÄ”5B†–B’DòUDDR4U@¢6WVVæ6SÖW†6ÇVFVBç6WVVæ6RÀ¢7F'E÷F–6·3ÖW†6ÇVFVBç7F'E÷F–6·2À¢VæE÷F–6·3ÖW†6ÇVFVBæVæE÷F–6·2À¢6÷W&6U÷FW‡CÖW†6ÇVFVBç6÷W&6U÷FW‡BÀ¢6†–æW6U÷FW‡CÖW†6ÇVFVBæ6†–æW6U÷FW‡BÀ¢F&vWE÷FW‡CÖW†6ÇVFVBçF&vWE÷FW‡BÀ¢G&ç6ÆF–öåöF—&V7F–öãÖW†6ÇVFVBçG&ç6ÆF–öåöF—&V7F–öâÀ¢—5öf–æÃÖW†6ÇVFVBæ—5öf–æÂÀ¢ÆæwVvSÖW†6ÇVFVBæÆæwVvRÀ¢6öæf–FVæ6SÖW†6ÇVFVBæ6öæf–FVæ6RÀ¢f–WvVE÷6Æ–FU÷vSÖW†6ÇVFVBçf–WvVE÷6Æ–FU÷vRÀ¢6æF–FFU÷6Æ–FU÷vSÖW†6ÇVFVBæ6æF–FFU÷6Æ–FU÷vRÀ¢6Æ–FUöÖF6…ö6öæf–FVæ6SÖW†6ÇVFVBç6Æ–FUöÖF6…ö6öæf–FVæ6RÀ¢6Æ–FUöÖF6…öWf–FVæ6SÖW†6ÇVFVBç6Æ–FUöÖF6…öWf–FVæ6RÀ¢6Æ–FUöföÆÆ÷uö7F–öãÖW†6ÇVFVBç6Æ–FUöföÆÆ÷uö7F–öã°¢""#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F–B"Â6VvÖVçBä–BåFõ7G&–ær‚$B"’“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G6W76–öâ"Â6VvÖVçBå6W76–öä–BåFõ7G&–ær‚$B"’“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G6WVVæ6R"Â6VvÖVçBå6WVVæ6R“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G7F'B"Â6VvÖVçBå7F'BåF–6·2“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FVæB"Â6VvÖVçBäVæBåF–6·2“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G6÷W&6R"Â6VvÖVçBå6÷W&6UFW‡B“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F6†–æW6R"Â6VvÖVçBä6†–æW6UFW‡B—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBä6†–æW6UFW‡B“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"GF&vWB"Â6VvÖVçBåF&vWEFW‡B—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBåF&vWEFW‡B“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FF—&V7F–öâ"Â6VvÖVçBåG&ç6ÆF–öäF—&V7F–öä–B“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"Ff–æÂ"Â6VvÖVçBä—4f–æÂò¢“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FÆæwVvR"Â6VvÖVçBäÆæwVvR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F6öæf–FVæ6R"Â6VvÖVçBä6öæf–FVæ6R—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBä6öæf–FVæ6RåfÇVR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"Gf–WvVEvR"Â6VvÖVçBåf–WvVE6Æ–FUvR—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBåf–WvVE6Æ–FUvRåfÇVR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F6æF–FFUvR"Â6VvÖVçBä6æF–FFU6Æ–FUvR—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBä6æF–FFU6Æ–FUvRåfÇVR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FÖF6„6öæf–FVæ6R"Â6VvÖVçBå6Æ–FTÖF6„6öæf–FVæ6R—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBå6Æ–FTÖF6„6öæf–FVæ6RåfÇVR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FÖF6„Wf–FVæ6R"Â6VvÖVçBå6Æ–FTÖF6„Wf–FVæ6R—2çVÆÂòD$çVÆÂåfÇVR¢6VvÖVçBå6Æ–FTÖF6„Wf–FVæ6R“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FföÆÆ÷t7F–öâ"Â†–çB—6VvÖVçBå6Æ–FTföÆÆ÷t7F–öâ“°¢v—B6öÖÖæBäW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢V&Æ–27–æ2fÇVUF6²WFFUG&ç67&—E6Æ–FTÆ–æ´7–æ2€¢wV–BG&ç67&—D–BÀ¢–çBf–WvVE6Æ–FUvRÀ¢6Æ–FTföÆÆ÷t7F–öâ7F–öâÀ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢UDDRG&ç67&—G0¢4UBf–WvVE÷6Æ–FU÷vSÒGf–WvVEvRÂ6Æ–FUöföÆÆ÷uö7F–öãÒF7F–öà¢t„U$R–CÒF–C°¢""#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F–B"ÂG&ç67&—D–BåFõ7G&–ær‚$B"’“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"Gf–WvVEvR"Âf–WvVE6Æ–FUvR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F7F–öâ"Â†–çB–7F–öâ“°¢v—B6öÖÖæBäW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢V&Æ–27–æ2fÇVUF6³Ä•&VDöæÇ”Æ—7CÅG&ç67&—E6VvÖVçCãâvWEG&ç67&—G47–æ2€¢wV–B6W76–öä–BÀ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢f"&W7VÇG2ÒæWrÆ—7CÅG&ç67&—E6VvÖVçCâ‚“°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢4TÄT5B–BÂ6W76–öåö–BÂ6WVVæ6RÂ7F'E÷F–6·2ÂVæE÷F–6·2Â6÷W&6U÷FW‡BÀ¢6†–æW6U÷FW‡BÂF&vWE÷FW‡BÂG&ç6ÆF–öåöF—&V7F–öâÂ—5öf–æÂÂÆæwVvRÂ6öæf–FVæ6RÀ¢f–WvVE÷6Æ–FU÷vRÂ6æF–FFU÷6Æ–FU÷vRÂ6Æ–FUöÖF6…ö6öæf–FVæ6RÂ6Æ–FUöÖF6…öWf–FVæ6RÂ6Æ–FUöföÆÆ÷uö7F–öà¢e$ôÒG&ç67&—G2t„U$R6W76–öåö–CÒG6W76–öâõ$DU"%’6WVVæ6RÂ7F'E÷F–6·3°¢""#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G6W76–öâ"Â6W76–öä–BåFõ7G&–ær‚$B"’“°¢v—BW6–ærf"&VFW"Òv—B6öÖÖæBäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v†–ÆR†v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢°¢&W7VÇG2äFB†æWrG&ç67&—E6VvÖVçB€¢wV–Bå'6R‡&VFW"ävWE7G&–ærƒ’’ÂwV–Bå'6R‡&VFW"ävWE7G&–ærƒ’’Â&VFW"ävWD–çCcBƒ"’À¢F–ÖU7âäg&öÕF–6·2‡&VFW"ävWD–çCcBƒ2’’ÂF–ÖU7âäg&öÕF–6·2‡&VFW"ävWD–çCcBƒB’’À¢&VFW"ävWE7G&–ærƒR’Â&VFW"ä—4D$çVÆÂƒb’òçVÆÂ¢&VFW"ävWE7G&–ærƒb’À¢&VFW"ävWD–çCcBƒ’’ÒÂ&VFW"ävWE7G&–ærƒ’Â&VFW"ä—4D$çVÆÂƒ’òçVÆÂ¢&VFW"ävWDF÷V&ÆRƒ’¢°¢F&vWEFW‡BÒ&VFW"ä—4D$çVÆÂƒr’òçVÆÂ¢&VFW"ävWE7G&–ærƒr’À¢G&ç6ÆF–öäF—&V7F–öä–BÒ&VFW"ävWE7G&–ærƒ‚’À¢f–WvVE6Æ–FUvRÒ&VFW"ä—4D$çVÆÂƒ"’òçVÆÂ¢&VFW"ävWD–çC3"ƒ"’À¢6æF–FFU6Æ–FUvRÒ&VFW"ä—4D$çVÆÂƒ2’òçVÆÂ¢&VFW"ävWD–çC3"ƒ2’À¢6Æ–FTÖF6„6öæf–FVæ6RÒ&VFW"ä—4D$çVÆÂƒB’òçVÆÂ¢&VFW"ävWDF÷V&ÆRƒB’À¢6Æ–FTÖF6„Wf–FVæ6RÒ&VFW"ä—4D$çVÆÂƒR’òçVÆÂ¢&VFW"ävWE7G&–ærƒR’À¢6Æ–FTföÆÆ÷t7F–öâÒ&VFW"ä—4D$çVÆÂƒb’ò6Æ–FTföÆÆ÷t7F–öâäæöæR¢…6Æ–FTföÆÆ÷t7F–öâ—&VFW"ävWD–çC3"ƒb¢Ò“°¢Ð ¢&WGW&â&W7VÇG3°¢Ð ¢V&Æ–27–æ2fÇVUF6³Å6W76–öãóâvWE6W76–öä7–æ2„wV–B6W76–öä–BÂ6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ%4TÄT5B–BÂ6÷W'6UöæÖRÂ7F'FVEöBÂVæFVEöBÂ7FGW2Â6÷W'6Uö–BÂÖFW&–Å÷F‚ÂÖFW&–Å÷G—RÂ7GVG•÷6µ÷F‚ÂÆW76öåö¶W’ÂÆ7E÷6Æ–FU÷vRe$ôÒ6W76–öç2t„U$R–CÒF–C²#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F–B"Â6W76–öä–BåFõ7G&–ær‚$B"’“°¢v—BW6–ærf"&VFW"Òv—B6öÖÖæBäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢–b‚v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢°¢&WGW&âçVÆÃ°¢Ð ¢&WGW&â&VE6W76–öâ‡&VFW"“°¢Ð ¢V&Æ–27–æ2fÇVUF6²Ö&´÷Vå6W76–öç4–çFW''WFVD7–æ2€¢FFUF–ÖTöfg6WB&V6÷fW&VDBÀ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶VâÒFVfVÇB¢°¢v—BW6–ærf"6öææV7F–öâÒv—B÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢UDDR6W76–öç24UB7FGW3ÒF–çFW''WFVBÂVæFVEöCÒFVæFV@¢t„U$R7FGW2”â‚G&W&–ærÂFÆ—fRÂGW6VB“°¢""#°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"F–çFW''WFVB"Â†–çB•6W76–öå7FGW2ä–çFW''WFVB“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FVæFVB"Âf÷&ÖB‡&V6÷fW&VDB’“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"G&W&–ær"Â†–çB•6W76–öå7FGW2å&W&–ær“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"FÆ—fR"Â†–çB•6W76–öå7FGW2äÆ—fR“°¢6öÖÖæBå&ÖWFW'2äFEv—F…fÇVR‚"GW6VB"Â†–çB•6W76–öå7FGW2åW6VB“°¢v—B6öÖÖæBäW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢&—fFR7–æ2fÇVUF6³Å7Æ—FT6öææV7F–öãâ÷Vä7–æ2„6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶Vâ¢°¢f"6öææV7F–öâÒæWr7Æ—FT6öææV7F–öâ…ö6öææV7F–öå7G&–ær“°¢v—B6öææV7F–öâä÷Vä7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢&WGW&â6öææV7F–öã°¢Ð ¢&—fFR7FF–27–æ2fÇVUF6²Vç7W&U6W76–öä6öÇVÖä7–æ2…7Æ—FT6öææV7F–öâ6öææV7F–öâÂ7G&–ær6öÇVÖäæÖRÂ7G&–ærFVf–æ—F–öâÂ6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶Vâ¢°¢v—BW6–ærf"–ç7V7BÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢–ç7V7Bä6öÖÖæEFW‡BÒ%$tÔF&ÆUö–æfò‡6W76–öç2“²#°¢v—BW6–ærf"&VFW"Òv—B–ç7V7BäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v†–ÆR†v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢–b‡7G&–æräWVÇ2‡&VFW"ävWE7G&–ærƒ’Â6öÇVÖäæÖRÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’’&WGW&ã°¢v—B&VFW"äF—7÷6T7–æ2‚“°¢v—BW6–ærf"ÇFW"Ò6öææV7F–öâä7&VFT6öÖÖæB‚“°¢ÇFW"ä6öÖÖæEFW‡BÒB$ÅDU"D$ÄR6W76–öç2DB4ôÅTÔâ¶6öÇVÖäæÖWÒ¶FVf–æ—F–öçÓ²#°¢v—BÇFW"äW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢&—fFR7FF–27–æ2fÇVUF6²Ö–w&FTÆVv7”6÷W'6W47–æ2…7Æ—FT6öææV7F–öâ6öææV7F–öâÂ6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶Vâ¢°¢v—BW6–ærf"6öÖÖæBÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢6öÖÖæBä6öÖÖæEFW‡BÒ"" ¢”å4U%B”åDò6÷W'6W2†–BÂæÖRÂ7&VFVEöBÂ—5ö&6†—fVB¢4TÄT5BÆ÷vW"††W‚‡&æFöÖ&Æö"ƒB’’ÇÂrÒrÇÂ†W‚‡&æFöÖ&Æö"ƒ"’’ÇÂrÓBrÇÂ7V'7G"††W‚‡&æFöÖ&Æö"ƒ"’’Ã"’ÇÂrÖrÇÂ7V'7G"††W‚‡&æFöÖ&Æö"ƒ"’’Ã"’ÇÂrÒrÇÂ†W‚‡&æFöÖ&Æö"ƒb’’’À¢G&–Ò†6÷W'6UöæÖR’ÂÖ–â‡7F'FVEöB’Â ¢e$ôÒ6W76–öç2t„U$R6÷W'6Uö–B•2åTÄÂu$õU%’G&–Ò†6÷W'6UöæÖR“°¢UDDR6W76–öç24UB6÷W'6Uö–CÒ…4TÄT5B–Be$ôÒ6÷W'6W2t„U$RæÖS×G&–Ò‡6W76–öç2æ6÷W'6UöæÖR’4ôÄÄDRäô44Rõ$DU"%’7&VFVEöBÄ”Ô•B¢t„U$R6÷W'6Uö–B•2åTÄÃ°¢""#°¢v—B6öÖÖæBäW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢&—fFR7FF–27–æ2fÇVUF6²Vç7W&UG&ç67&—D6öÇVÖä7–æ2€¢7Æ—FT6öææV7F–öâ6öææV7F–öâÀ¢7G&–ær6öÇVÖäæÖRÀ¢7G&–ærFVf–æ—F–öâÀ¢6æ6VÆÆF–öåFö¶Vâ6æ6VÆÆF–öåFö¶Vâ¢°¢v—BW6–ærf"–ç7V7BÒ6öææV7F–öâä7&VFT6öÖÖæB‚“°¢–ç7V7Bä6öÖÖæEFW‡BÒ%$tÔF&ÆUö–æfò‡G&ç67&—G2“²#°¢v—BW6–ærf"&VFW"Òv—B–ç7V7BäW†V7WFU&VFW$7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢v†–ÆR†v—B&VFW"å&VD7–æ2†6æ6VÆÆF–öåFö¶Vâ’¢°¢–b‡7G&–æräWVÇ2‡&VFW"ävWE7G&–ærƒ’Â6öÇVÖäæÖRÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢°¢&WGW&ã°¢Ð¢Ð ¢v—B&VFW"äF—7÷6T7–æ2‚“°¢v—BW6–ærf"ÇFW"Ò6öææV7F–öâä7&VFT6öÖÖæB‚“°¢ÇFW"ä6öÖÖæEFW‡BÒB$ÅDU"D$ÄRG&ç67&—G2DB4ôÅTÔâ¶6öÇVÖäæÖWÒ¶FVf–æ—F–öçÓ²#°¢v—BÇFW"äW†V7WFTæöåVW'”7–æ2†6æ6VÆÆF–öåFö¶Vâ“°¢Ð ¢&—fFR7FF–26W76–öâ&VE6W76–öâ…7Æ—FTFF&VFW"&VFW"’ÓâæWr€¢wV–Bå'6R‡&VFW"ävWE7G&–ærƒ’’Â&VFW"ävWE7G&–ærƒ’Â'6R‡&VFW"ävWE7G&–ærƒ"’’À¢&VFW"ä—4D$çVÆÂƒ2’òçVÆÂ¢'6R‡&VFW"ävWE7G&–ærƒ2’’Â…6W76–öå7FGW2—&VFW"ävWD–çC3"ƒB’¢°¢6÷W'6T–BÒ&VFW"ä—4D$çVÆÂƒR’òçVÆÂ¢wV–Bå'6R‡&VFW"ävWE7G&–ærƒR’’À¢ÖFW&–ÅF‚Ò&VFW"ä—4D$çVÆÂƒb’òçVÆÂ¢&VFW"ävWE7G&–ærƒb’À¢ÖFW&–ÅG—RÒ&VFW"ä—4D$çVÆÂƒr’òçVÆÂ¢&VFW"ävWE7G&–ærƒr’À¢7GVG•6µF‚Ò&VFW"ä—4D$çVÆÂƒ‚’òçVÆÂ¢&VFW"ävWE7G&–ærƒ‚’À¢ÆW76öä¶W’Ò&VFW"ä—4D$çVÆÂƒ’’òçVÆÂ¢&VFW"ävWE7G&–ærƒ’’À¢Æ7E6Æ–FUvRÒ&VFW"ä—4D$çVÆÂƒ’òçVÆÂ¢&VFW"ävWD–çC3"ƒ¢Ó° ¢&—fFR7FF–27G&–ærf÷&ÖB„FFUF–ÖTöfg6WBfÇVR’ÓâfÇVRåFõ7G&–ær‚$ò"Â7VÇGW&T–æfòä–çf&–çD7VÇGW&R“° ¢&—fFR7FF–2FFUF–ÖTöfg6WB'6R‡7G&–ærfÇVR’Óà¢FFUF–ÖTöfg6WBå'6R‡fÇVRÂ7VÇGW&T–æfòä–çf&–çD7VÇGW&RÂFFUF–ÖU7G–ÆW2å&÷VæGG&—¶–æB“°§Ð