using System.Text.Json;
using System.Runtime.CompilerServices;
using ClassInterpreter.Core.Configuration;
using ClassInterpreter.Infrastructure.Logging;
using ClassInterpreter.Infrastructure.Secrets;
using ClassInterpreter.Infrastructure.Retention;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;
using ClassInterpreter.Core.Audio;
using ClassInterpreter.Infrastructure.Audio;
using ClassInterpreter.Core.Speech;
using ClassInterpreter.Infrastructure.Qwen;
using ClassInterpreter.Infrastructure.Configuration;
using ClassInterpreter.Core.Slides;
using ClassInterpreter.Infrastructure.Slides;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using ClassInterpreter.Core.StudyPacks;
using ClassInterpreter.Infrastructure.StudyPacks;
using System.Runtime.InteropServices;
using ClassInterpreter.Core.Demo;
using ClassInterpreter.Infrastructure.Demo;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Infrastructure.Learning;

var tests = new List<(string Name, Action Run)>
{
    ("AppPaths remain under the requested D drive root", AppPathsRemainUnderRoot),
    ("AppPaths reject an empty root with a Chinese error", () => RejectInvalidRoot("")),
    ("AppPaths accept a portable non-D root", AppPathsAcceptPortableRoot),
    ("Root resolver preserves writable D drive", RootResolverPreservesWritableDDrive),
    ("Root resolver uses executable directory without D drive", RootResolverUsesExecutableDirectory),
    ("Root resolver falls back to LocalAppData when portable directory is read-only", RootResolverUsesLocalAppData),
    ("Default root resolver can simulate a computer without D drive", DefaultRootResolverCanDisableDDrive),
    ("Serialized settings never contain an API key", SettingsNeverContainApiKey),
    ("AppPaths create every D drive directory", AppPathsCreateDirectories),
    ("AppPaths report directory failures in Chinese", AppPathsReportDirectoryFailure),
    ("Sensitive log values are redacted", SensitiveLogValuesAreRedacted),
    ("Qwen credential target is stable", QwenCredentialTargetIsStable),
    ("Windows credential store round-trips a secret", WindowsCredentialStoreRoundTripsSecret),
    ("Audio younger than 14 days remains active", AudioYoungerThanRetentionRemains),
    ("Audio at 14 days moves to recoverable trash", ExpiredAudioMovesToTrash),
    ("Locked audio is retained", LockedAudioIsRetained),
    ("Trash is deleted only after 24 hours", TrashDeletesAfterGracePeriod),
    ("Timeline initializes an empty SQLite database", TimelineInitializesEmptyDatabase),
    ("Timeline returns transcript segments in sequence order", TimelineOrdersSegments),
    ("Final transcript replaces its interim version", FinalTranscriptReplacesInterim),
    ("Timeline preserves translation direction and target text", TimelinePreservesDirection),
    ("History formatter merges fragmented classroom subtitles", HistoryFormatterMergesFragments),
    ("History formatter separates speech after a time gap", HistoryFormatterHonorsTimeGaps),
    ("Merged lesson timeline orders by recorded time without downtime jumps", MergedLessonTimelineIsContinuous),
    ("Open sessions recover as interrupted after a crash", OpenSessionsRecoverAsInterrupted),
    ("PCM level monitor reports silence and full scale", PcmLevelMonitorReportsPeak),
    ("Audio timestamps remain monotonic", AudioTimestampsRemainMonotonic),
    ("Wave segment writer produces a valid recoverable WAV", WaveSegmentWriterProducesWav),
    ("Late microphone stop callbacks cannot cancel a disposed signal", MicrophoneStopCallbackIsLifecycleSafe),
    ("Bounded audio queue drops the oldest frame", BoundedAudioQueueDropsOldest),
    ("Qwen session update configures PCM and server VAD", QwenSessionUpdateConfiguresPcm),
    ("Qwen interim event combines confirmed and stashed text", QwenInterimCombinesTextAndStash),
    ("Qwen completed event becomes a final recognition event", QwenCompletedBecomesFinal),
    ("Qwen errors map to a safe provider exception", QwenErrorsAreSafe),
    ("Qwen Singapore endpoint only accepts a safe workspace id", QwenEndpointIsSafe),
    ("Settings file round-trips without secrets", SettingsFileRoundTrips),
    ("Translation directions expose stable language metadata", TranslationDirectionsExposeMetadata),
    ("Japanese and English listening modes use explicit source languages", ListeningModesUseExplicitLanguages),
    ("Qwen ASR session receives the selected source language", QwenAsrReceivesSourceLanguage),
    ("Courses persist rename archive and chronological sessions", CoursesPersistNavigation),
    ("Nearby interrupted records become one numbered lesson", NearbyRecordsBecomeOneLesson),
    ("Two lessons can be merged manually without losing AI questions", LessonsCanBeMergedManually),
    ("A classroom record can be deleted without deleting its course", ClassroomRecordCanBeDeleted),
    ("Old settings default to mixed speech translating to Chinese", OldSettingsDefaultDirection),
    ("Translation direction is locked while a session is running", TranslationDirectionLocksWhileRunning),
    ("Chinese output modes reject non-Chinese recognition", ChineseModesRequireChineseInput),
    ("Qwen MT request targets Simplified Chinese", QwenMtTargetsChinese),
    ("Qwen MT request targets Japanese", QwenMtTargetsJapanese),
    ("Qwen MT request targets English", QwenMtTargetsEnglish),
    ("Qwen MT response extracts translated content", QwenMtExtractsContent),
    ("PPTX extractor preserves slide order and text", PptxExtractorPreservesSlides),
    ("PDF extractor preserves page order and text", PdfExtractorPreservesPages),
    ("PDF renderer exports and reuses page images", PdfRendererExportsPageImages),
    ("Slide matcher focuses the relevant nearby page", SlideMatcherFocusesRelevantPage),
    ("Slide matcher follows Chinese and Japanese slide text", SlideMatcherHandlesCjkText),
    ("Slide matcher gives recent speech more weight", SlideMatcherPrefersRecentSpeech),
    ("Slide matcher refuses low confidence distant jumps", SlideMatcherRefusesDistantJump),
    ("Slide matcher returns no more than three candidates", SlideMatcherReturnsThreeCandidates),
    ("Slide follow requires stable high-confidence evidence", SlideFollowRequiresStableEvidence),
    ("Slide follow pauses immediately after manual browsing", SlideFollowPausesForStudent),
    ("Study pack request prioritizes assessment and course knowledge", StudyPackRequestHasSections),
    ("Markdown study pack includes auditable transcript links", MarkdownStudyPackIncludesTranscript),
    ("Markdown study pack includes translation direction and target text", MarkdownStudyPackIncludesDirection),
    ("Live audio hub records independently from cloud consumers", LiveAudioHubRecordsIndependently),
    ("Demo scenario exercises mixed English Japanese Chinese and slides", DemoScenarioCoversCoreFlow),
    ("Demo scenario provides Japanese and English speaking examples", DemoScenarioCoversSpeakingDirections),
    ("UI renderer waits for asynchronous window initialization", UiRendererWaitsForInitialization),
    ("Application entry points use the portable root resolver", ApplicationUsesPortableRootResolver),
    ("macOS text inputs vertically center their content", MacInputsCenterContent),
    ("AM-LINK console branding and developer credit are present", AmLinkBrandingIsPresent),
    ("AM-LINK console uses black and gold theme tokens", AmLinkThemeUsesBlackAndGold),
    ("AM-LINK UI contains no third-party product branding", AmLinkContainsNoThirdPartyBranding),
    ("Classroom workspace prioritizes slides and subtitles", ClassroomWorkspacePrioritizesContent),
    ("Settings drawer starts collapsed and has a real toggle", SettingsDrawerIsInteractive),
    ("Slide navigation uses real buttons", SlideNavigationIsInteractive),
    ("History view is scrollable copyable and supports quick page jumps", HistoryViewIsUsable),
    ("Live translated subtitles are selectable for AI questions", LiveSubtitlesAreSelectableForAi),
    ("Interrupted lessons restore their saved slide deck", InterruptedLessonRestoresSlides),
    ("AI classroom questions persist and can be updated after failure", AiQuestionsPersistAcrossRestart),
    ("AI tutor request requires auditable citations", AiTutorRequestRequiresCitations),
    ("Long lesson material is chunked without losing its ending", LongLessonMaterialIsChunked),
    ("AI lesson bundle contains class evidence without API keys", AiLessonBundleContainsEvidence),
    ("Temporary translator chooses both Chinese directions", QuickTranslatorChoosesBothDirections),
    ("Bidirectional classroom mode routes both speakers", BidirectionalClassroomRoutesBothSpeakers),
    ("Bidirectional language routing avoids ambiguous Han-only pane switches", BidirectionalRoutingAvoidsAmbiguousHan),
    ("Bidirectional routing resists wrong provider language labels", BidirectionalRoutingResistsWrongLabels),
    ("Bidirectional routing holds backchannels and mixed overlap", BidirectionalRoutingHoldsUnsafeSegments),
    ("Voice language profile only resolves after repeated clear samples", VoiceLanguageProfileLearnsConservatively),
    ("Recognition text removes overlapping and runaway repeated tokens", RecognitionTextRemovesRunawayRepeats),
    ("Independent interpreter displays only the translated language", TemporaryInterpreterShowsTargetsOnly),
    ("Independent interpreter uses live microphone and saves both panes", TemporaryInterpreterIsLiveAndEphemeral),
    ("Bidirectional discussion defaults to AI routing with manual overrides", BidirectionalPushToTalkLocksAsrLanguage),
    ("Experimental overlap mode is removed", ExperimentalOverlapModeIsRemoved),
    ("Live subtitles preserve old reading position", LiveSubtitlesPreserveReadingPosition),
    ("Application uses guarded optional slide following", AutomaticSlideFocusIsGuarded),
    ("Decorative console controls are removed", DecorativeConsoleControlsAreRemoved),
    ("Interrupted lessons persist their last manual slide page", LastSlidePagePersists),
    ("PPT terminology is injected into Qwen MT requests", PptTerminologyEnrichesTranslation),
    ("AI usage aggregates locally without storing credentials", AiUsageAggregatesLocally),
    ("AI usage estimates Singapore list-price cost", AiUsageEstimatesCost),
    ("Auxiliary windows use one system close control", AuxiliaryWindowsHaveSingleCloseControl),
    ("Headless demo service writes SQLite and Markdown", HeadlessDemoWritesArtifacts)
};

if (string.Equals(Environment.GetEnvironmentVariable("RUN_OFFICE_SMOKE"), "1", StringComparison.Ordinal))
{
    tests.Add(("PowerPoint renderer exports real slide thumbnails", PowerPointRendererExportsThumbnails));
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void AiQuestionsPersistAcrossRestart()
{
    var root = TestDirectories.Create("ai-question-tests");
    try
    {
        var database = Path.Combine(root, "timeline.db");
        var repository = new SqliteTimelineRepository(database);
        repository.InitializeAsync().GetAwaiter().GetResult();
        var id = Guid.NewGuid();
        var failed = new AiQuestionRecord(
            id, "lesson-stable-key", null, DateTimeOffset.Now, "这个概念是什么？", "选中的字幕", null,
            3, "05:12", "qwen-flash", AiQuestionStatus.Failed, "temporary");
        repository.UpsertAiQuestionAsync(failed).GetAwaiter().GetResult();

        var restarted = new SqliteTimelineRepository(database);
        restarted.InitializeAsync().GetAwaiter().GetResult();
        var items = restarted.GetAiQuestionsAsync("lesson-stable-key").GetAwaiter().GetResult();
        Require(items.Count == 1, "Saved question count changed.");
        Require(items[0].Status == AiQuestionStatus.Failed, "Failed status was not restored.");

        restarted.UpsertAiQuestionAsync(items[0] with
        {
            Answer = "这是课堂依据中的概念。[PPT第3页]",
            Status = AiQuestionStatus.Completed,
            Error = null
        }).GetAwaiter().GetResult();
        var completed = restarted.GetAiQuestionsAsync("lesson-stable-key").GetAwaiter().GetResult().Single();
        Require(completed.Status == AiQuestionStatus.Completed, "Retry status was not saved.");
        Require(completed.Answer!.Contains("PPT第3页", StringComparison.Ordinal), "Retry did not update the saved answer.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void AiTutorRequestRequiresCitations()
{
    var json = QwenAiTutorProtocol.CreateRequest(new AiTutorRequest(
        "为什么？", "原句", "人机交互", 8, "[PPT第8页] 内容", "[12:34] 字幕"));
    Require(json.Contains("[PPT第N页]", StringComparison.Ordinal), "PPT citation rule is missing.");
    Require(json.Contains("[mm:ss]", StringComparison.Ordinal), "Timestamp citation rule is missing.");
    Require(json.Contains("资料不足", StringComparison.Ordinal), "Insufficient-evidence rule is missing.");
    Require(!json.Contains("api_key", StringComparison.OrdinalIgnoreCase), "Request serialized a secret field.");
}

static void LongLessonMaterialIsChunked()
{
    var text = string.Concat(Enumerable.Range(0, 1800).Select(index => $"[{index / 60:00}:{index % 60:00}] 第{index}条课堂内容。\n"));
    var chunks = StudyPackChunker.Split(text, 12000, 500);
    Require(chunks.Count > 2, "Long material was not chunked.");
    Require(chunks.All(chunk => chunk.Length <= 12000), "A chunk exceeded the configured limit.");
    Require(chunks[^1].Contains("第1799条课堂内容", StringComparison.Ordinal), "The lesson ending was lost.");
}

static void AiLessonBundleContainsEvidence()
{
    var root = TestDirectories.Create("ai-bundle-tests");
    try
    {
        var session = new Session(Guid.NewGuid(), "人工智能", DateTimeOffset.Now, DateTimeOffset.Now, SessionStatus.Completed)
        {
            LessonKey = "lesson-1"
        };
        var slides = new SlideDocument("lesson.pptx", [new SlidePage(1, "问题", "课件正文", "老师备注")]);
        var transcript = new TranscriptSegment(Guid.NewGuid(), session.Id, 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
            "これは質問です", "这是一个问题", true, "ja", 1) { TargetText = "这是一个问题" };
        var question = new AiQuestionRecord(Guid.NewGuid(), "lesson-1", null, DateTimeOffset.Now, "什么意思？", "��}5��$z{-���jםrison.Ordinal) &&
            xaml.Contains("CopyHistoryButton_Click", StringComparison.Ordinal),
        "History transcript is not scrollable and copyable.");
    Require(xaml.Contains("x:Name=\"JumpPageBox\"", StringComparison.Ordinal) &&
            xaml.Contains("JumpToPageButton_Click", StringComparison.Ordinal),
        "Quick page jump controls are missing.");
    Require(xaml.Contains("LessonNumber, StringFormat=第 {0} 节课", StringComparison.Ordinal),
        "Classrooms are not displayed as numbered lessons.");
    Require(xaml.Contains("x:Name=\"MergeNearbySessionsCheckBox\"", StringComparison.Ordinal) &&
            xaml.Contains("x:Name=\"MergeTranscriptCheckBox\"", StringComparison.Ordinal),
        "Time-based record and transcript merge options are missing.");
    Require(xaml.Contains("x:Name=\"ContinueLessonButton\"", StringComparison.Ordinal) &&
            xaml.Contains("ContinueLessonButton_Click", StringComparison.Ordinal),
        "A historical lesson cannot continue live interpretation.");
    Require(xaml.Contains("x:Name=\"MergeWithPreviousLessonButton\"", StringComparison.Ordinal) &&
            xaml.Contains("MergeWithPreviousLessonButton_Click", StringComparison.Ordinal),
        "A user cannot manually merge two lesson records.");
    var code = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    Require(xaml.Contains("x:Name=\"OpenRecordingsButton\"", StringComparison.Ordinal)
            && xaml.Contains("OpenRecordingsButton_Click", StringComparison.Ordinal)
            && code.Contains("FindLessonRecordings", StringComparison.Ordinal)
            && code.Contains("课堂录音-{startedAt:yyyyMMdd-HHmmss}.wav", StringComparison.Ordinal),
        "Lesson recordings cannot be viewed by course or preserved across continued sessions.");
}

static void LiveSubtitlesAreSelectableForAi()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    var code = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    var tutor = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "AiTutorWindow.xaml.cs"));
    Require(xaml.Contains("<TextBox x:Name=\"ConfirmedTranslationText\" IsReadOnly=\"True\"", StringComparison.Ordinal), "Confirmed live translations cannot be selected.");
    Require(xaml.Contains("<TextBox x:Name=\"ChineseSubtitleText\"", StringComparison.Ordinal), "Current translation cannot be selected.");
    Require(code.Contains("ConfirmedTranslationText.SelectedText", StringComparison.Ordinal), "Ask AI ignores selected live translation history.");
    Require(code.Contains("ConfirmedTranslationText.Select(selectionStart, selectionLength)", StringComparison.Ordinal), "New subtitles destroy the user's active selection.");
    Require(xaml.Split("ContextMenu=\"{StaticResource SubtitleContextMenu}\"", StringSplitOptions.None).Length - 1 == 3 &&
            xaml.Contains("Header=\"问 AI\"", StringComparison.Ordinal) &&
            code.Contains("AskSelectedTextMenuItem_Click", StringComparison.Ordinal),
        "Live and historical subtitles do not expose the shared right-click Ask AI action.");
    Require(code.Contains("askItem.IsEnabled = hasSelection", StringComparison.Ordinal),
        "Right-click Ask AI is not disabled when there is no selected text.");
    Require(tutor.Contains("QuestionBox.Text = initialQuestion", StringComparison.Ordinal),
        "Selected subtitle text is not inserted into the editable AI question box.");
    Require(tutor.Contains("public void SetQuestion(string question)", StringComparison.Ordinal),
        "An already open AI window cannot receive a newly selected subtitle.");
    Require(!tutor.Contains("请解释这段内容", StringComparison.Ordinal), "The redundant default AI question remains.");
}

static void InterruptedLessonRestoresSlides()
{
    var root = FindProjectRoot();
    var code = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    Require(code.Contains("recentInterrupted", StringComparison.Ordinal) && code.Contains("await ShowHistoryAsync(recentInterrupted)", StringComparison.Ordinal),
        "A restarted app does not reopen the interrupted lesson and its slide deck.");
}

static void DecorativeConsoleControlsAreRemoved()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    foreach (var obsoleteLabel in new[] { "SYSTEM MODULES", "SIGNAL RACK", "CHANNEL A", "CHANNEL B", "DATA ROOT", "ACTIVE MODE" })
    {
        Require(!xaml.Contains(obsoleteLabel, StringComparison.Ordinal), $"Decorative label remains: {obsoleteLabel}");
    }
}

static void AmLinkThemeUsesBlackAndGold()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "App.xaml"));
    Require(xaml.Contains("x:Key=\"GoldAccentBrush\"", StringComparison.Ordinal), "Gold accent token missing.");
    Require(xaml.Contains("x:Key=\"DashedConsoleButtonStyle\"", StringComparison.Ordinal), "Dashed console button missing.");
    Require(xaml.Contains("#D5B85C", StringComparison.OrdinalIgnoreCase), "Approved gold color missing.");
}

static void AmLinkContainsNoThirdPartyBranding()
{
    var root = FindProjectRoot();
    var files = new[]
    {
        Path.Combine(root, "src", "ClassInterpreter.App", "App.xaml"),
        Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml")
    };
    var combined = string.Join('\n', files.Select(File.ReadAllText));
    Require(!combined.Contains("DG-LAB", StringComparison.OrdinalIgnoreCase), "Third-party name DG-LAB leaked into UI.");
    Require(!combined.Contains("Coyote", StringComparison.OrdinalIgnoreCase), "Third-party name Coyote leaked into UI.");
    Require(!combined.Contains("郊狼", StringComparison.Ordinal), "Third-party Chinese name leaked into UI.");
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClassInterpreter.sln")))
    {
        directory = directory.Parent;
    }
    return directory?.FullName ?? @"D:\Codex\ClassInterpreter";
}

static void HeadlessDemoWritesArtifacts()
{
    var root = TestDirectories.Create("headless-demo-tests");
    var paths = AppPaths.Create(root);
    try
    {
        var result = new DemoRunService(paths).RunAsync(DemoScenario.Create()).GetAwaiter().GetResult();
        Require(File.Exists(result.DatabasePath), "Headless demo did not create SQLite.");
        Require(File.Exists(result.MarkdownPath), "Headless demo did not create Markdown.");
        var markdown = File.ReadAllText(result.MarkdownPath);
        Require(markdown.Contains("Self-Attention", StringComparison.Ordinal), "Headless Markdown lacks analysis.");
        Require(markdown.Contains("次回までに", StringComparison.Ordinal), "Headless Markdown lacks Japanese source.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ReleaseCom(object? value)
{
    if (value is not null && Marshal.IsComObject(value))
    {
        Marshal.FinalReleaseComObject(value);
    }
}

static void LastSlidePagePersists()
{
    var root = TestDirectories.Create("last-slide-page");
    try
    {
        var repository = new SqliteTimelineRepository(Path.Combine(root, "timeline.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var session = new Session(Guid.NewGuid(), "class", DateTimeOffset.Now, null, SessionStatus.Interrupted)
            { LastSlidePage = 27 };
        repository.UpsertSessionAsync(session).GetAwaiter().GetResult();
        Require(repository.GetSessionAsync(session.Id).GetAwaiter().GetResult()?.LastSlidePage == 27,
            "Last manual slide page was not persisted.");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}

static void PptTerminologyEnrichesTranslation()
{
    var slides = new SlideDocument("lecture.pdf", new[]
    {
        new SlidePage(1, "Human-AI Interaction", "意図認識とBayesian Networkを扱う", ""),
        new SlidePage(2, "Recognition", "専門用語を確認", "")
    });
    var hint = SlideTerminology.BuildDomainHint(slides, 1);
    var terms = SlideTerminology.PreservedTerms(slides, 1);
    var json = QwenMtProtocol.CreateRequest("Human AI interaction", TranslationDirection.EnglishToChinese, hint, terms);
    Require(json.Contains("Human-AI Interaction", StringComparison.Ordinal), "Current PPT terminology missing from request.");
    Require(json.Contains("terms", StringComparison.Ordinal) && json.Contains("domains", StringComparison.Ordinal),
        "Qwen terminology controls were not sent.");
}

static void AiUsageAggregatesLocally()
{
    var root = TestDirectories.Create("ai-usage");
    try
    {
        var repository = new SqliteTimelineRepository(Path.Combine(root, "timeline.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var today = DateOnly.FromDateTime(DateTime.Now);
        repository.RecordAiUsageAsync(new AiUsageRecord(today, AiUsageKind.Translation, "qwen-mt-flash", 1, 0, 20, 10, 8, 4, 0)).GetAwaiter().GetResult();
        repository.RecordAiUsageAsync(new AiUsageRecord(today, AiUsageKind.Translation, "qwen-mt-flash", 1, 1, 5, 0, 2, 0, 0)).GetAwaiter().GetResult();
        var row = repository.GetAiUsageAsync(today).GetAwaiter().GetResult().Single();
        Require(row.RequestCount == 2 && row.FailureCount == 1 && row.InputCharacters == 25,
            "Local AI usage did not aggregate atomically.");
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}

static void AiUsageEstimatesCost()
{
    var day = DateOnly.FromDateTime(DateTime.Now);
    var asr = new AiUsageRecord(day, AiUsageKind.SpeechRecognition, "qwen3-asr-flash-realtime", 1, 0, 0, 0, 0, 0, 60_000);
    var mt = new AiUsageRecord(day, AiUsageKind.Translation, "qwen-mt-flash", 1, 0, 0, 0, 1_000_000, 1_000_000, 0);
    var study = new AiUsageRecord(day, AiUsageKind.StudyPack, "qwen3.7-plus", 1, 0, 0, 0, 1_000_000, 1_000_000, 0);
    Require(AiUsagePricing.EstimateUsd(asr) == 0.005400m, "Singapore realtime ASR price is wrong.");
    Require(AiUsagePricing.EstimateUsd(mt) == 0.65m, "Singapore Qwen MT price is wrong.");
    Require(AiUsagePricing.EstimateUsd(study) == 2.00m, "Singapore study-summary model price is wrong.");
    Require(!AiUsagePricing.IsSupported("future-unknown-model"), "Unknown models must not be silently priced as free.");
}

static void AuxiliaryWindowsHaveSingleCloseControl()
{
    foreach (var name in new[] { "AiTutorWindow.xaml", "QuickTranslatorWindow.xaml", "ClassroomWindow.xaml" })
    {
        var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", name));
        Require(!xaml.Contains("Content=\"关闭\"", StringComparison.Ordinal), $"{name} still duplicates the title-bar close button.");
    }
}

static void Equal(string expected, string actual) =>
    Require(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), $"Expected '{expected}', got '{actual}'.");

static void EqualDouble(double expected, double actual) =>
    Require(Math.Abs(expected - actual) < 0.0001, $"Expected '{expected}', got '{actual}'.");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static TException Expect<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

file static class TestDirectories
{
    public static string Create(string category) => Path.Combine(
        Path.GetTempPath(),
        "AM-LINK-tests",
        category,
        Guid.NewGuid().ToString("N"));
}

file sealed class RetentionFixture : IDisposable
{
    private RetentionFixture(AppPaths paths, DateTimeOffset now)
    {
        Paths = paths;
        Now = now;
        Service = new AudioRetentionService(paths, TimeSpan.FromDays(14), TimeSpan.FromHours(24));
    }

    public AppPaths Paths { get; }
    public DateTimeOffset Now { get; }
    public AudioRetentionService Service { get; }

    public static RetentionFixture Create()
    {
        var root = TestDirectories.Create("retention-tests");
        var paths = AppPaths.Create(root);
        paths.EnsureDirectories();
        return new RetentionFixture(paths, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
    }

    public string CreateAudio(string name, DateTimeOffset createdAt)
    {
        var path = Path.Combine(Paths.AudioDirectory, name);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
        File.SetLastWriteTimeUtc(path, createdAt.UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }
}

file sealed class TimelineFixture : IDisposable
{
    private TimelineFixture(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "timeline.db");
        Repository = new SqliteTimelineRepository(DatabasePath);
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public SqliteTimelineRepository Repository { get; }

    public static TimelineFixture Create() =>
        new(TestDirectories.Create("timeline-tests"));

    public static TimelineFixture CreateInitialized()
    {
        var fixture = Create();
        fixture.Repository.InitializeAsync().GetAwaiter().GetResult();
        return fixture;
    }

    public Session StartSession()
    {
        var session = new Session(Guid.NewGuid(), "test course", new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero), null, SessionStatus.Live);
        Repository.UpsertSessionAsync(session).GetAwaiter().GetResult();
        return session;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

file sealed class SequenceAudioSource(IReadOnlyList<AudioFrame> frames) : IAudioSource
{
    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        int deviceNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }
    }
}
