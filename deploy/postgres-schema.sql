CREATE TABLE courses(id uuid PRIMARY KEY,name text NOT NULL,password_hash text NOT NULL,password_salt text NOT NULL,password_version integer NOT NULL DEFAULT 1,created_at timestamptz NOT NULL);
CREATE TABLE lessons(id uuid PRIMARY KEY,course_id uuid NOT NULL REFERENCES courses(id),name text NOT NULL,code char(6) NOT NULL UNIQUE,started_at timestamptz NOT NULL,ended_at timestamptz NULL);
CREATE TABLE participants(id uuid PRIMARY KEY,lesson_id uuid NOT NULL REFERENCES lessons(id),token_hash text NOT NULL,last_seen timestamptz NOT NULL,joined_at timestamptz NOT NULL);
CREATE TABLE questions(id uuid PRIMARY KEY,event_id uuid NOT NULL UNIQUE,lesson_id uuid NOT NULL REFERENCES lessons(id),participant_id uuid NOT NULL REFERENCES participants(id),question text NOT NULL,asked_at timestamptz NOT NULL,transcript_timestamp text NULL,slide_page integer NULL,selected_context varchar(500) NULL,votes integer NOT NULL DEFAULT 0,pinned boolean NOT NULL DEFAULT false,addressed boolean NOT NULL DEFAULT false,topic text NOT NULL DEFAULT '其他');
CREATE TABLE votes(event_id uuid PRIMARY KEY,question_id uuid NOT NULL REFERENCES questions(id),participant_id uuid NOT NULL REFERENCES participants(id),voted_at timestamptz NOT NULL,UNIQUE(question_id,participant_id));
CREATE TABLE confusions(event_id uuid PRIMARY KEY,lesson_id uuid NOT NULL REFERENCES lessons(id),participant_id uuid NOT NULL REFERENCES participants(id),occurred_at timestamptz NOT NULL,transcript_timestamp text NULL,slide_page integer NULL);
CREATE TABLE broadcasts(id uuid PRIMARY KEY,lesson_id uuid NOT NULL REFERENCES lessons(id),message text NOT NULL,sent_at timestamptz NOT NULL);
CREATE INDEX ix_questions_lesson ON questions(lesson_id,asked_at DESC);
CREATE INDEX ix_participants_online ON participants(lesson_id,last_seen DESC);
