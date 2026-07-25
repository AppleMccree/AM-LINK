# Class Interpreter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a low-footprint Windows app that turns mixed English/Japanese classroom audio into low-latency Chinese subtitles, follows imported slides, and exports an auditable Markdown study pack.

**Architecture:** A WPF shell hosts isolated audio, cloud-provider, timeline, slide-matching, and study-pack modules. Immutable timestamped events flow through the live pipeline and are persisted in SQLite; external models are hidden behind interfaces so Qwen defaults can be replaced without UI or storage changes.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, NAudio, Microsoft.Data.Sqlite, Open XML SDK, PDFiumSharp, Serilog, xUnit, FluentAssertions, WireMock.Net.

## Global Constraints

- Build and runtime data root: `D:\Codex\ClassInterpreter`; never place recordings or model caches under `%APPDATA%` or C:.
- Default models: Qwen realtime ASR, MT Lite, Flash, and VL Flash through Alibaba Cloud Model Studio International.
- Subtitle targets: interim median <=1.5 s; final Chinese P95 <=3 s.
- Audio retention: 14 days plus 24-hour recoverable-delete window.
- Secrets: Windows Credential Manager only; redact from logs and exports.
- Every feature uses test-first implementation and ends with a focused commit.

---

### Task 1: Solution shell, configuration, and storage root

**Files:** Create `src/ClassInterpreter.App`, `src/ClassInterpreter.Core`, `src/ClassInterpreter.Infrastructure`, `tests/ClassInterpreter.Tests`, and `ClassInterpreter.sln`.

**Interfaces:** Produce `AppPaths.Create(string root)`, `ISecretStore`, `AppSettings`, and a WPF shell with startup diagnostics.

- [ ] Write xUnit tests proving every derived path remains under the supplied D: root, invalid/non-writable roots fail with a Chinese error, and serialized settings contain no API key.
- [ ] Run `dotnet test` and verify the new tests fail because the types do not exist.
- [ ] Create the .NET 8 projects, central package versions, nullable/implicit usings, and `AppPaths` with `data/db`, `data/audio`, `data/trash`, `data/cache`, `data/exports`, and `logs` directories.
- [ ] Implement `WindowsCredentialSecretStore` using Credential Manager and a settings screen that validates but never echoes the key.
- [ ] Add Serilog rolling files with a redaction enricher for authorization headers and key-like values.
- [ ] Run `dotnet test` and `dotnet build -c Release`; expect zero failures and warnings, then commit `feat: create D-drive application foundation`.

### Task 2: Durable session timeline and retention

**Files:** Create focused persistence classes under `src/ClassInterpreter.Infrastructure/Timeline` and domain records under `src/ClassInterpreter.Core/Sessions`.

**Interfaces:** Produce `Session`, `TranscriptSegment`, `TranslationSegment`, `SlideFocusEvent`, `ITimelineRepository`, and `IAudioRetentionService`.

- [ ] Write repository tests for ordered append/read, interim-to-final replacement, crash recovery of an open session, and schema migration from an empty database.
- [ ] Write retention tests with a fake clock proving 13-day audio remains, 14-day audio moves to trash, locked audio remains, and trash is permanently deleted only after 24 hours.
- [ ] Implement SQLite migrations, WAL mode, five-second batched commits, idempotent event IDs, session recovery, and retention transactions.
- [ ] Run repository and retention tests twice to prove idempotence; commit `feat: persist recoverable classroom timelines`.

### Task 3: Audio capture and segmented local recording

**Files:** Create `AudioCaptureService`, `WaveSegmentWriter`, `AudioLevelMonitor`, and their tests.

**Interfaces:** Produce `IAudioSource.StartAsync(AudioFormat, CancellationToken)` returning `IAsyncEnumerable<AudioFrame>` where frames are 16 kHz, mono, signed 16-bit PCM with monotonic timestamps.

- [ ] Write tests using synthetic PCM for format conversion, monotonically increasing timestamps, bounded channel backpressure, segment rollover, pause/resume, and final WAV header repair after simulated crash.
- [ ] Implement NAudio microphone enumeration and capture; keep UI work off the capture callback and cap the in-memory channel at two seconds.
- [ ] Implement local WAV segments, input meter, silence/VAD hints, and device-disconnect recovery while preserving already-written audio.
- [ ] Add a diagnostic screen that records 30 seconds and plays it back without any cloud key.
- [ ] Run tests plus a documented manual microphone smoke test; commit `feat: capture and safeguard classroom audio`.

### Task 4: Qwen realtime ASR adapter and reconnect pipeline

**Files:** Create provider-neutral speech contracts and `QwenRealtimeAsrClient`; add WireMock/WebSocket protocol fixtures without real keys.

**Interfaces:** `IStreamingRecognizer.RecognizeAsync(IAsyncEnumerable<AudioFrame>, RecognitionContext, CancellationToken)` returns `IAsyncEnumerable<RecognitionEvent>` containing segment ID, text, start/end, interim/final, detected language, and confidence when supplied.

- [ ] Write protocol tests for session creation, PCM append, interim/final parsing, English/Japanese output, server error mapping, cancellation, reconnect, and no secret leakage.
- [ ] Implement the Qwen WebSocket client with bounded send/receive queues, keepalive, exponential backoff with jitter, and explicit authentication/rate-limit/balance errors.
- [ ] Implement offline buffering metadata so reconnect resumes live recognition while queued WAV segments become separate backfill jobs.
- [ ] Add opt-in integration tests selected by `QWEN_API_KEY`, excluded from normal test runs, and a mixed English/Japanese sample benchmark command.
- [ ] Verify normal tests work with no network and integration tests stream when a key is present; commit `feat: stream multilingual Qwen transcripts`.

### Task 5: Dual-speed Chinese translation and terminology

**Files:** Create `ITextTranslator`, `QwenMtTranslator`, `TranslationCoordinator`, `CourseGlossary`, and subtitle view models.

**Interfaces:** `TranslateAsync(TranslationRequest)` accepts source text, detected-language hints, recent stable context, glossary mappings, and interim/final mode; returns Chinese text plus source segment IDs.

- [ ] Write tests proving interim requests use a short context, final requests supersede only their own recent subtitle lines, old final lines never mutate, glossary replacements persist, and mixed-script input is preserved for verification.
- [ ] Implement MT Lite streaming calls, optional MT Flash selection, debouncing of unstable ASR revisions, context-window limits, and retry without duplicate UI events.
- [ ] Build the live subtitle panel with Chinese primary text, smaller source text, EN/JA/MIXED label, interim styling, confidence warning, and correction-to-glossary action.
- [ ] Add latency telemetry measured from audio-frame time to first and final render, shown as aggregate only and stored locally.
- [ ] Run tests and a 30-minute recorded-stream soak test; commit `feat: add low-latency auditable Chinese subtitles`.

### Task 6: PPTX/PDF import and slide index

**Files:** Create `SlideImporter`, `PptxExtractor`, `PdfExtractor`, `SlideThumbnailRenderer`, `ISlideVisionAnalyzer`, and Qwen VL implementation.

**Interfaces:** Produce `SlideDocument` with ordered pages containing title, body text, speaker notes, local thumbnail path, visual description, and extraction status.

- [ ] Add fixture PPTX/PDF files covering text, notes, diagrams, scanned pages, corrupt files, and duplicate slides; write extraction and ordering tests.
- [ ] Implement Open XML extraction and PDFium rendering strictly into the D: cache, keyed by source hash for reuse.
- [ ] Call Qwen VL Flash only for pages below a local-text threshold and only after the user-enabled upload policy; cache returned descriptions and show per-page upload status.
- [ ] Build import progress, cancellation, error summary, current-page preview, and thumbnail strip.
- [ ] Run fixtures with network disabled, then the opt-in VL integration test; commit `feat: import and index classroom slides`.

### Task 7: Stable slide matching and focus UI

**Files:** Create `SlideMatcher`, scoring configuration, evaluation fixtures, and slide focus view models.

**Interfaces:** `Match(SlideMatchContext)` returns ranked `SlideCandidate` values with page number, score, evidence terms, and `AutoFocusAllowed`.

- [ ] Write deterministic tests for forward progression, brief references to earlier slides, duplicate vocabulary, topic jumps, low-confidence hold, and three-candidate limit.
- [ ] Implement lexical/BM25 similarity over 20–45 seconds of final source and Chinese text, title/notes boosts, and a page-distance transition penalty; do not use interim text.
- [ ] Tune thresholds only against a versioned evaluation fixture and report accuracy plus unwanted-jump count.
- [ ] Connect high-confidence results to in-app page focus and low-confidence results to non-disruptive candidates; never control PowerPoint itself.
- [ ] Achieve >=90% fixture accuracy with zero auto-jumps below threshold; commit `feat: follow the lecturer through imported slides`.

### Task 8: Markdown study-pack generation

**Files:** Create `IStudyPackAnalyzer`, `QwenStudyPackAnalyzer`, optional `DeepSeekStudyPackAnalyzer`, schema records, and `MarkdownStudyPackWriter`.

**Interfaces:** Analyzer returns structured chapters, claims, knowledge points, actions, deadlines, questions, and review outline, each carrying source segment IDs and slide pages.

- [ ] Write tests for chunking a two-hour timeline without splitting final segments, merging page/topic chunks, rejecting nonexistent citations, preserving uncertain items, and deterministic Markdown links.
- [ ] Implement Qwen Flash structured analysis in bounded chunks followed by a consolidation pass; validate every citation against SQLite before rendering.
- [ ] Implement the optional DeepSeek V4 Flash adapter behind the same interface, disabled until a second key is supplied.
- [ ] Export only Markdown plus relative thumbnail/audio links under `data/exports/<course>/<session>`; never include secrets or unsupported claims.
- [ ] Run golden-file tests in Chinese and manually inspect a full sample learning pack; commit `feat: generate source-linked Markdown study packs`.

### Task 9: Recovery, cost controls, and two-hour system test

**Files:** Add orchestration state machine, cost ledger, recovery UI, end-to-end fixtures, and installer/publish configuration.

**Interfaces:** Produce explicit states `Idle`, `Preparing`, `Live`, `Reconnecting`, `Paused`, `Backfilling`, `Finalizing`, `Completed`, and `Faulted` with legal transitions only.

- [ ] Write state-machine tests for pause, device loss, network loss, authentication failure, crash restart, backfill, and clean completion.
- [ ] Implement session orchestration, per-service usage estimates, configurable per-class spending warning, and actionable Chinese errors.
- [ ] Run a deterministic two-hour synthetic session while monitoring private bytes and queue depths; assert bounded buffers and no lost finalized segments.
- [ ] Run end-to-end acceptance with a mixed English/Japanese recording and imported deck; record subtitle latency distribution, slide accuracy, recovery outcome, and estimated cost.
- [ ] Publish self-contained `win-x64` output to `D:\Codex\ClassInterpreter\dist`, configure installer defaults and data path to D:, and verify launch on a clean Windows user profile.
- [ ] Run `dotnet test`, `dotnet build -c Release`, secret scan, and retention dry run; commit `feat: ship resilient classroom interpreter v1`.

## Final acceptance gate

- [ ] Confirm interim median <=1.5 s and final P95 <=3 s on the target network.
- [ ] Confirm two-hour stability, bounded memory, and complete audio after a five-minute outage.
- [ ] Confirm slide accuracy >=90% and no low-confidence automatic jumps.
- [ ] Confirm 14-day retention and 24-hour recoverable deletion with a fake clock and a real filesystem smoke test.
- [ ] Confirm no secret appears in settings, SQLite, logs, crash output, Markdown, or packaged artifacts.
- [ ] If Qwen mixed-language recognition is not usable on the real benchmark, implement the already-defined `IStreamingRecognizer` with Deepgram Nova-3 multilingual before declaring v1 complete.

