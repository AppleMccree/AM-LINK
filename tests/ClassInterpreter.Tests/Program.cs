using System.Text.Json;
using System.Net.Http;
using System.Runtime.CompilerServices;
using ClassInterpreter.Core.Configuration;
using ClassInterpreter.Infrastructure.Classrooms;
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
    ("Headless demo service writes SQLite and Markdown", HeadlessDemoWritesArtifacts),
    ("Transcript segment ids are scoped to their session", TranscriptSegmentIdsAreSessionScoped),
    ("Error log writer redacts secrets before writing", ErrorLogWriterRedactsSecrets),
    ("Classroom outbox drops rejected messages but retries outages", ClassroomOutboxPolicyIsSelective),
    ("Timeline backup creates a restorable rotated copy", TimelineBackupCreatesRestorableCopy),
    ("Deleting a lesson also removes its AI questions", DeletingLessonRemovesAiQuestions),
    ("Application registers a global exception guard", ApplicationRegistersGlobalExceptionGuard),
    ("Classroom server avoids wildcard credentialed CORS and rate limits joins", ClassroomServerIsHardened)
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
        var question = new AiQuestionRecord(Guid.NewGuid(), "lesson-1", null, DateTimeOffset.Now, "什么意思？", "これは質問です",
            "意思是一个问题。[00:05]", 1, "00:05", "qwen-flash", AiQuestionStatus.Completed, null);
        var path = LessonAiBundleWriter.WriteAsync(root, session, "lesson-1", slides, [transcript], [question]).GetAwaiter().GetResult();
        var json = File.ReadAllText(path);
        Require(json.Contains("课件正文", StringComparison.Ordinal), "Slide text is missing.");
        Require(json.Contains("これは質問です", StringComparison.Ordinal), "Source transcript is missing.");
        Require(json.Contains("意思是一个问题", StringComparison.Ordinal), "AI answer is missing.");
        Require(File.Exists(Path.Combine(root, "问AI记录.md")), "Readable Q&A file is missing.");
        Require(!json.Contains("sk-secret", StringComparison.Ordinal), "Bundle leaked an API key.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void QuickTranslatorChoosesBothDirections()
{
    Require(QuickTranslationDirectionResolver.Resolve("请问车站在哪里？", "日文") == TranslationDirection.ChineseToJapanese, "Chinese to Japanese was not selected.");
    Require(QuickTranslationDirectionResolver.Resolve("駅はどこですか", "日文") == TranslationDirection.JapaneseToChinese, "Japanese to Chinese was not selected.");
    Require(QuickTranslationDirectionResolver.Resolve("我们开始吧", "英文") == TranslationDirection.ChineseToEnglish, "Chinese to English was not selected.");
    Require(QuickTranslationDirectionResolver.Resolve("Shall we begin?", "英文") == TranslationDirection.EnglishToChinese, "English to Chinese was not selected.");
}

static void BidirectionalClassroomRoutesBothSpeakers()
{
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "ja", "説明します") == TranslationDirection.JapaneseToChinese,
        "Japanese teacher speech was not routed to Chinese.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "zh", "我想提问") == TranslationDirection.ChineseToJapanese,
        "Chinese student speech was not routed to Japanese.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.EnglishChineseBidirectional, "en", "I have a question") == TranslationDirection.EnglishToChinese,
        "English teacher speech was not routed to Chinese.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.EnglishChineseBidirectional, "zh", "请再解释一下") == TranslationDirection.ChineseToEnglish,
        "Chinese student speech was not routed to English.");
    Require(TranslationDirection.JapaneseChineseBidirectional.AsrLanguage is null,
        "Bidirectional ASR must auto-detect both speakers.");
}

static void BidirectionalRoutingAvoidsAmbiguousHan()
{
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "unknown", "大学") is null,
        "Ambiguous Han-only speech was incorrectly forced into the Chinese pane.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "ja", "大学") == TranslationDirection.JapaneseToChinese,
        "A reliable Japanese language label was ignored.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "unknown", "请再说一次") == TranslationDirection.ChineseToJapanese,
        "Strong Simplified Chinese evidence was not routed to Japanese.");
}

static void BidirectionalRoutingResistsWrongLabels()
{
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "zh", "これは説明です") == TranslationDirection.JapaneseToChinese,
        "Japanese kana was overridden by a wrong Chinese provider label.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "ja", "请再给我解释一下") == TranslationDirection.ChineseToJapanese,
        "Strong Chinese evidence was overridden by a wrong Japanese provider label.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.EnglishChineseBidirectional, "en", "我想问一个问题") == TranslationDirection.ChineseToEnglish,
        "Chinese text was overridden by a wrong English provider label.");
}

static void BidirectionalRoutingHoldsUnsafeSegments()
{
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "ja", "嗯嗯") is null,
        "A short Chinese backchannel polluted the Japanese side.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "zh", "うん") is null,
        "A short Japanese backchannel polluted the Chinese side.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.JapaneseChineseBidirectional, "ja", "そうですね，请继续") is null,
        "Mixed overlapping speech was forced into one pane.");
    Require(BidirectionalTranslationRouter.Resolve(TranslationDirection.EnglishChineseBidirectional, "en", "okay 我知道了") is null,
        "Mixed English and Chinese speech was forced into one pane.");
}

static void VoiceLanguageProfileLearnsConservatively()
{
    var profile = new VoiceLanguageProfile();
    var chinese = new VoiceFingerprint(0.10, 0.05, 0.08);
    var japanese = new VoiceFingerprint(0.07, 0.24, 0.27);
    profile.Observe(TranslationDirection.ChineseToJapanese, chinese);
    profile.Observe(TranslationDirection.JapaneseToChinese, japanese);
    Require(profile.Resolve(TranslationDirection.JapaneseChineseBidirectional, chinese) is null,
        "Voice profile decided with too little training data.");
    for (var i = 0; i < 3; i++)
    {
        profile.Observe(TranslationDirection.ChineseToJapanese, chinese);
        profile.Observe(TranslationDirection.JapaneseToChinese, japanese);
    }
    Require(profile.Resolve(TranslationDirection.JapaneseChineseBidirectional, new(0.095, 0.052, 0.082)) == TranslationDirection.ChineseToJapanese,
        "Learned Chinese speaker profile was not used.");
    Require(profile.Resolve(TranslationDirection.JapaneseChineseBidirectional, new(0.072, 0.235, 0.265)) == TranslationDirection.JapaneseToChinese,
        "Learned Japanese speaker profile was not used.");
    Require(profile.ResolveSelf(TranslationDirection.JapaneseChineseBidirectional, new(0.095, 0.052, 0.082)) == TranslationDirection.ChineseToJapanese,
        "The one-to-many router did not recognize the learned local speaker.");
    Require(profile.ResolveSelf(TranslationDirection.JapaneseChineseBidirectional, new(0.072, 0.235, 0.265)) is null,
        "A foreign participant was mistaken for the learned local speaker.");
}

static void RecognitionTextRemovesRunawayRepeats()
{
    Equal("OKEY", RecognitionTextNormalizer.Sanitize("OKEYOKEYOKEYOKEYOKEY"));
    Equal("okay", RecognitionTextNormalizer.Sanitize("okay okay okay okay"));
    Equal("うん、それは、", RecognitionTextNormalizer.Sanitize("...ん、それは、うん、それは、うん、それは、うん、それは、うん、それは、"));
    Equal("今日は大丈夫です", RecognitionTextNormalizer.Merge("今日は大丈夫", "大丈夫です"));
    Equal("The model is ready", RecognitionTextNormalizer.Merge("The model ", "is ready"));
}

static void TemporaryInterpreterShowsTargetsOnly()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml.cs"));
    // 产品决定已更新：对话模式默认保留原文（放在译文下方），方便当场核对听到了什么。
    Require(xaml.Contains("LeftCurrentSourceText", StringComparison.Ordinal), "Left pane lost its source line.");
    Require(xaml.Contains("RightCurrentSourceText", StringComparison.Ordinal), "Right pane lost its source line.");
    Require(code.Contains("_showOriginal = true", StringComparison.Ordinal), "Conversation mode no longer keeps the heard source visible for verification.");
    Require(code.Contains("$\"[{turn.At:HH:mm:ss}] {turn.Translation}\"", StringComparison.Ordinal), "Pane history still renders both languages.");
    Require(code.Contains("_processedFinalItems", StringComparison.Ordinal), "Repeated final recognition events are not filtered.");
}

static void MicrophoneStopCallbackIsLifecycleSafe()
{
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.Infrastructure", "Audio", "MicrophoneAudioSource.cs"));
    Require(code.Contains("Volatile.Read(ref captureClosed)", StringComparison.Ordinal), "Late recording callbacks are not guarded.");
    Require(code.Contains("catch (ObjectDisposedException)", StringComparison.Ordinal), "Disposed stop signals can still terminate the app.");
    Require(code.Contains("microphone.RecordingStopped -= RecordingStopped", StringComparison.Ordinal), "Recording callback is not detached during cleanup.");
}

static void TemporaryInterpreterIsLiveAndEphemeral()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml.cs"));
    Require(xaml.Contains("开始双向同传", StringComparison.Ordinal), "Independent live start control is missing.");
    Require(xaml.Contains("LeftHistoryBox", StringComparison.Ordinal) && xaml.Contains("RightHistoryBox", StringComparison.Ordinal),
        "Chinese-to-foreign and foreign-to-Chinese panes are not separate.");
    Require(xaml.Contains("MicrophoneBox", StringComparison.Ordinal)
            && !xaml.Contains("SelfMicrophoneBox", StringComparison.Ordinal)
            && !xaml.Contains("OpponentMicrophoneBox", StringComparison.Ordinal),
        "Single-microphone discussion should expose exactly one capture device.");
    Require(code.Contains("MicrophoneAudioSource", StringComparison.Ordinal), "Temporary interpreter does not use the microphone.");
    Require(code.Contains("QwenRealtimeAsrClient", StringComparison.Ordinal), "Temporary interpreter does not use realtime Qwen ASR.");
    Require(code.Contains("SaveRecordAsync", StringComparison.Ordinal), "Confirmed bidirectional subtitles are not saved.");
    Require(code.Contains("sourceLanguage: null", StringComparison.Ordinal),
        "Automatic discussion mode no longer asks ASR to detect the spoken language.");
    Require(!code.Contains("SqliteTimelineRepository", StringComparison.Ordinal), "Independent interpreter should not mix records into classroom history.");
    Require(!code.Contains("WaveSegmentWriter", StringComparison.Ordinal), "Temporary interpreter records audio.");
}

static void BidirectionalPushToTalkLocksAsrLanguage()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml.cs"));
    Require(xaml.Contains("按住我来说", StringComparison.Ordinal), "Push-to-talk control is missing.");
    Require(xaml.Contains("AI 自动判断", StringComparison.Ordinal) && xaml.Contains("对方连续发言", StringComparison.Ordinal),
        "Automatic and foreign-speaker override modes are missing.");
    Require(code.Contains("RunAutomaticRecognitionAsync", StringComparison.Ordinal)
            && code.Contains("ResolveAutomaticRoute", StringComparison.Ordinal)
            && !code.Contains("RunParallelAutomaticRecognitionAsync", StringComparison.Ordinal),
        "Automatic mode must emit one ASR result and route it once instead of duplicating one microphone into two streams.");
    Require(code.Contains("route.AsrLanguage, silenceDurationMs: 500", StringComparison.Ordinal),
        "Manual speaker locks no longer constrain the ASR language.");
    Require(code.Contains("HoldToTalkButton_Down", StringComparison.Ordinal) && code.Contains("HoldToTalkButton_Up", StringComparison.Ordinal),
        "Push-to-talk does not switch both on press and release.");
    Require(code.Contains("_voiceProfile.ResolveSelf", StringComparison.Ordinal)
            && code.Contains("BidirectionalTranslationRouter.Resolve", StringComparison.Ordinal),
        "Automatic routing no longer combines recognized language, script evidence, and learned local voice.");
}

static void ExperimentalOverlapModeIsRemoved()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "QuickTranslatorWindow.xaml.cs"));
    Require(!xaml.Contains("ExperimentalSeparationBox", StringComparison.Ordinal)
            && !xaml.Contains("单麦双人分离", StringComparison.Ordinal),
        "The removed overlap-separation switch is still visible.");
    Require(!code.Contains("LocalTwoSpeakerSeparator", StringComparison.Ordinal)
            && !code.Contains("TwoSpeakerAudioBuffer", StringComparison.Ordinal)
            && !code.Contains("SpatialSeparationStatus", StringComparison.Ordinal),
        "The removed overlap-separation runtime is still wired into the window.");
}

static void LiveSubtitlesPreserveReadingPosition()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "MainWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    Require(xaml.Contains("NewSubtitleButton", StringComparison.Ordinal), "New-subtitle notice is missing.");
    Require(xaml.Contains("TranslationScrollViewer_ScrollChanged", StringComparison.Ordinal), "Scroll state is not observed.");
    Require(code.Contains("if (_followLiveSubtitles)", StringComparison.Ordinal), "Append does not respect reading position.");
    Require(code.Contains("NewSubtitleButton.Visibility = Visibility.Visible", StringComparison.Ordinal), "User cannot return to the latest subtitle.");
}

static void AutomaticSlideFocusIsGuarded()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "MainWindow.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    Require(xaml.Contains("SlideFollowModeBox", StringComparison.Ordinal)
            && xaml.Contains("ResumeSlideFollowButton", StringComparison.Ordinal)
            && xaml.Contains("ApplySlideSuggestionButton", StringComparison.Ordinal),
        "Follow modes and student controls are missing.");
    Require(code.Contains("LinkFinalSubtitleToSlideAsync", StringComparison.Ordinal)
            && code.Contains("SlideFollowMode.Automatic", StringComparison.Ordinal)
            && code.Contains("PauseForManualNavigation", StringComparison.Ordinal),
        "Live slide linking is not guarded by optional mode and manual-reading pause.");
}

static void AppPathsRemainUnderRoot()
{
    var root = @"D:\AM-LINK";
    var paths = AppPaths.Create(root);
    var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

    foreach (var path in paths.AllDirectories)
    {
        Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), $"Path escaped root: {path}");
    }

    Equal(Path.Combine(root, "data", "db"), paths.DatabaseDirectory);
    Equal(Path.Combine(root, "data", "audio"), paths.AudioDirectory);
    Equal(Path.Combine(root, "data", "trash"), paths.TrashDirectory);
    Equal(Path.Combine(root, "data", "cache"), paths.CacheDirectory);
    Equal(Path.Combine(root, "data", "exports"), paths.ExportDirectory);
    Equal(Path.Combine(root, "logs"), paths.LogDirectory);
}

static void RejectInvalidRoot(string root)
{
    try
    {
        _ = AppPaths.Create(root);
        throw new InvalidOperationException("Expected ArgumentException.");
    }
    catch (ArgumentException exception)
    {
        Require(exception.Message.Contains("数据目录", StringComparison.Ordinal), "Expected a Chinese data-directory error.");
    }
}

static void AppPathsAcceptPortableRoot()
{
    var root = Path.Combine(Path.GetTempPath(), "AM-LINK-portable");
    var paths = AppPaths.Create(root);
    Require(paths.Root.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "Portable root was rejected.");
}

static void RootResolverPreservesWritableDDrive()
{
    var result = AppRootResolver.Resolve(@"C:\Apps\AM-LINK", @"C:\Users\Test\AppData\Local", path => path.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase), true);
    Equal(@"D:\AM-LINK", result);
}

static void RootResolverUsesExecutableDirectory()
{
    var result = AppRootResolver.Resolve(@"C:\Apps\AM-LINK", @"C:\Users\Test\AppData\Local", path => path == @"C:\Apps\AM-LINK", false);
    Equal(@"C:\Apps\AM-LINK", result);
}

static void RootResolverUsesLocalAppData()
{
    var result = AppRootResolver.Resolve(@"C:\Program Files\AM-LINK", @"C:\Users\Test\AppData\Local", path => path == @"C:\Users\Test\AppData\Local\AM-LINK", false);
    Equal(@"C:\Users\Test\AppData\Local\AM-LINK", result);
}

static void DefaultRootResolverCanDisableDDrive()
{
    var previous = Environment.GetEnvironmentVariable("AM_LINK_DISABLE_D_DRIVE");
    try
    {
        Environment.SetEnvironmentVariable("AM_LINK_DISABLE_D_DRIVE", "1");
        var result = AppRootResolver.ResolveDefault();
        Require(!result.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase), "D drive was used despite the simulation flag.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("AM_LINK_DISABLE_D_DRIVE", previous);
    }
}

static void SettingsNeverContainApiKey()
{
    var settings = new AppSettings
    {
        DataRoot = @"D:\AM-LINK",
        QwenEndpoint = "wss://dashscope-intl.aliyuncs.com",
        AudioRetentionDays = 14
    };
    var json = JsonSerializer.Serialize(settings);

    Require(!json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "Serialized JSON contains ApiKey.");
    Require(!json.Contains("secret", StringComparison.OrdinalIgnoreCase), "Serialized JSON contains secret.");
    Require(json.Contains("\"AudioRetentionDays\":14", StringComparison.Ordinal), "Retention was not serialized.");
}

static void AppPathsCreateDirectories()
{
    var root = TestDirectories.Create("test-runtime");
    var paths = AppPaths.Create(root);

    paths.EnsureDirectories();

    foreach (var path in paths.AllDirectories)
    {
        Require(Directory.Exists(path), $"Directory was not created: {path}");
    }

    Directory.Delete(root, recursive: true);
}

static void AppPathsReportDirectoryFailure()
{
    var root = TestDirectories.Create("test-runtime");
    Directory.CreateDirectory(Path.GetDirectoryName(root)!);
    File.WriteAllText(root, "collision");

    try
    {
        var exception = Expect<IOException>(() => AppPaths.Create(root).EnsureDirectories());
        Require(exception.Message.Contains("无法创建数据目录", StringComparison.Ordinal), "Expected a Chinese directory error.");
    }
    finally
    {
        File.Delete(root);
    }
}

static void SensitiveLogValuesAreRedacted()
{
    const string input = "Authorization: Bearer abc123 api_key=very-secret-token";
    var output = SensitiveDataRedactor.Redact(input);

    Require(!output.Contains("abc123", StringComparison.Ordinal), "Bearer token leaked.");
    Require(!output.Contains("very-secret-token", StringComparison.Ordinal), "API key leaked.");
    Require(output.Contains("[REDACTED]", StringComparison.Ordinal), "Redaction marker missing.");
}

static void QwenCredentialTargetIsStable()
{
    Equal("ClassInterpreter/QwenApiKey", CredentialTargets.QwenApiKey);
}

static void WindowsCredentialStoreRoundTripsSecret()
{
    var target = $"ClassInterpreter/Test/{Guid.NewGuid():N}";
    ISecretStore store = new WindowsCredentialSecretStore();

    try
    {
        store.SaveAsync(target, "temporary-secret").GetAwaiter().GetResult();
        var loaded = store.ReadAsync(target).GetAwaiter().GetResult();
        Equal("temporary-secret", loaded ?? string.Empty);
    }
    finally
    {
        store.DeleteAsync(target).GetAwaiter().GetResult();
    }

    Require(store.ReadAsync(target).GetAwaiter().GetResult() is null, "Credential was not deleted.");
}

static void AudioYoungerThanRetentionRemains()
{
    using var fixture = RetentionFixture.Create();
    var audio = fixture.CreateAudio("recent.wav", fixture.Now.AddDays(-13));

    fixture.Service.SweepAsync(fixture.Now).GetAwaiter().GetResult();

    Require(File.Exists(audio), "Recent audio was removed.");
}

static void ExpiredAudioMovesToTrash()
{
    using var fixture = RetentionFixture.Create();
    var audio = fixture.CreateAudio("expired.wav", fixture.Now.AddDays(-14));

    fixture.Service.SweepAsync(fixture.Now).GetAwaiter().GetResult();

    Require(!File.Exists(audio), "Expired audio remained active.");
    Require(Directory.EnumerateFiles(fixture.Paths.TrashDirectory, "expired*.wav").Count() == 1, "Expired audio was not moved to trash.");
}

static void LockedAudioIsRetained()
{
    using var fixture = RetentionFixture.Create();
    var audio = fixture.CreateAudio("locked.wav", fixture.Now.AddDays(-30));
    File.WriteAllText(audio + ".keep", "locked");

    fixture.Service.SweepAsync(fixture.Now).GetAwaiter().GetResult();

    Require(File.Exists(audio), "Locked audio was removed.");
}

static void TrashDeletesAfterGracePeriod()
{
    using var fixture = RetentionFixture.Create();
    var audio = fixture.CreateAudio("old.wav", fixture.Now.AddDays(-14));
    fixture.Service.SweepAsync(fixture.Now).GetAwaiter().GetResult();
    var trashed = Directory.EnumerateFiles(fixture.Paths.TrashDirectory, "old*.wav").Single();

    fixture.Service.SweepAsync(fixture.Now.AddHours(23)).GetAwaiter().GetResult();
    Require(File.Exists(trashed), "Trash was permanently deleted before 24 hours.");

    fixture.Service.SweepAsync(fixture.Now.AddHours(24)).GetAwaiter().GetResult();
    Require(!File.Exists(trashed), "Trash was not permanently deleted at 24 hours.");
}

static void TimelineInitializesEmptyDatabase()
{
    using var fixture = TimelineFixture.Create();
    fixture.Repository.InitializeAsync().GetAwaiter().GetResult();
    Require(File.Exists(fixture.DatabasePath), "SQLite database was not created.");
}

static void TimelineOrdersSegments()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    fixture.Repository.UpsertTranscriptAsync(Segment(session.Id, "second", 2, true)).GetAwaiter().GetResult();
    fixture.Repository.UpsertTranscriptAsync(Segment(session.Id, "first", 1, true)).GetAwaiter().GetResult();

    var segments = fixture.Repository.GetTranscriptsAsync(session.Id).GetAwaiter().GetResult();

    Equal("first", segments[0].SourceText);
    Equal("second", segments[1].SourceText);
}

static void FinalTranscriptReplacesInterim()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    var id = Guid.NewGuid();
    var interim = Segment(session.Id, "hello wor", 1, false) with { Id = id };
    var final = interim with { SourceText = "hello world", IsFinal = true };

    fixture.Repository.UpsertTranscriptAsync(interim).GetAwaiter().GetResult();
    fixture.Repository.UpsertTranscriptAsync(final).GetAwaiter().GetResult();
    var segments = fixture.Repository.GetTranscriptsAsync(session.Id).GetAwaiter().GetResult();

    Require(segments.Count == 1, "Interim and final were duplicated.");
    Equal("hello world", segments[0].SourceText);
    Require(segments[0].IsFinal, "Final state was not persisted.");
}

static void TimelinePreservesDirection()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    var segment = Segment(session.Id, "请开始实验。", 1, true) with
    {
        TargetText = "実験を始めてください。",
        TranslationDirectionId = TranslationDirection.ChineseToJapanese.Id,
        ViewedSlidePage = 4,
        CandidateSlidePage = 5,
        SlideMatchConfidence = 0.81,
        SlideMatchEvidence = "实验、开始、步骤",
        SlideFollowAction = SlideFollowAction.Suggested
    };
    fixture.Repository.UpsertTranscriptAsync(segment).GetAwaiter().GetResult();

    var actual = fixture.Repository.GetTranscriptsAsync(session.Id).GetAwaiter().GetResult().Single();
    Equal("実験を始めてください。", actual.TargetText ?? string.Empty);
    Equal(TranslationDirection.ChineseToJapanese.Id, actual.TranslationDirectionId);
    Require(actual.ViewedSlidePage == 4, "Viewed slide page was not persisted.");
    Require(actual.CandidateSlidePage == 5, "Candidate slide page was not persisted.");
    Require(actual.SlideMatchConfidence is > 0.8, "Slide-link confidence was not persisted.");
    Require(actual.SlideFollowAction == SlideFollowAction.Suggested, "Slide-link action was not persisted.");
}

static void HistoryFormatterMergesFragments()
{
    var sessionId = Guid.NewGuid();
    var segments = new[]
    {
        new TranscriptSegment(Guid.NewGuid(), sessionId, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Today we discuss", "今天我们讨论", true, "en", 1) { TargetText = "今天我们讨论" },
        new TranscriptSegment(Guid.NewGuid(), sessionId, 2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "machine learning.", "机器学习。", true, "en", 1) { TargetText = "机器学习。" },
        new TranscriptSegment(Guid.NewGuid(), sessionId, 3, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "Next topic", "下一个主题", true, "en", 1) { TargetText = "下一个主题" }
    };

    var translated = TranscriptHistoryFormatter.Format(segments, false);
    var source = TranscriptHistoryFormatter.Format(segments, true);
    Require(translated.Contains("今天我们讨论机器学习。", StringComparison.Ordinal), "Translated fragments were not merged.");
    Require(source.Contains("Today we discuss machine learning.", StringComparison.Ordinal), "English fragments were not spaced and merged.");
}

static void HistoryFormatterHonorsTimeGaps()
{
    var sessionId = Guid.NewGuid();
    var segments = new[]
    {
        new TranscriptSegment(Guid.NewGuid(), sessionId, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "first", null, true, "en", 1) { TargetText = "第一段" },
        new TranscriptSegment(Guid.NewGuid(), sessionId, 2, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(16), "second", null, true, "en", 1) { TargetText = "第二段" }
    };
    var merged = TranscriptHistoryFormatter.Format(segments, false, true);
    Require(merged.Contains("[00:00] 第一段", StringComparison.Ordinal) && merged.Contains("[00:15] 第二段", StringComparison.Ordinal),
        "A long silence was incorrectly merged into one paragraph.");
}

static void MergedLessonTimelineIsContinuous()
{
    var first = new Session(Guid.NewGuid(), "course", new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero), null, SessionStatus.Interrupted);
    var second = new Session(Guid.NewGuid(), "course", new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero), null, SessionStatus.Completed);
    var combined = LessonTranscriptTimeline.Combine(new[]
    {
        (first, (IReadOnlyList<TranscriptSegment>)new[]
        {
            new TranscriptSegment(Guid.NewGuid(), first.Id, 1, TimeSpan.FromMinutes(38), TimeSpan.FromMinutes(38) + TimeSpan.FromSeconds(13), "first", null, true, "ja", 1)
        }),
        (second, (IReadOnlyList<TranscriptSegment>)new[]
        {
            new TranscriptSegment(Guid.NewGuid(), second.Id, 1, TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(28), TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(35), "second", null, true, "ja", 1)
        })
    });

    Require(combined[0].SourceText == "first" && combined[1].SourceText == "second", "Merged sessions were ordered by reset sequence numbers.");
    Require(combined[1].Start < TimeSpan.FromMinutes(40), "Wall-clock downtime leaked into the continuous lesson timeline.");
    var formatted = TranscriptHistoryFormatter.Format(combined.Reverse().ToArray(), true, false);
    Require(formatted.IndexOf("first", StringComparison.Ordinal) < formatted.IndexOf("second", StringComparison.Ordinal),
        "History formatter did not order merged captions by timestamp.");
}

static void OpenSessionsRecoverAsInterrupted()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    var recoveredAt = session.StartedAt.AddMinutes(5);

    fixture.Repository.MarkOpenSessionsInterruptedAsync(recoveredAt).GetAwaiter().GetResult();
    var recovered = fixture.Repository.GetSessionAsync(session.Id).GetAwaiter().GetResult();

    Require(recovered is not null, "Session disappeared.");
    Require(recovered!.Status == SessionStatus.Interrupted, "Open session was not marked interrupted.");
    Require(recovered.EndedAt == recoveredAt, "Recovery end time was not recorded.");
}

static TranscriptSegment Segment(Guid sessionId, string text, long sequence, bool isFinal) => new(
    Guid.NewGuid(), sessionId, sequence, TimeSpan.FromSeconds(sequence), TimeSpan.FromSeconds(sequence + 1),
    text, null, isFinal, "mixed", 0.9);

static void PcmLevelMonitorReportsPeak()
{
    EqualDouble(0d, PcmLevelMonitor.Peak([0, 0, 0, 0]));
    EqualDouble(1d, PcmLevelMonitor.Peak([0xFF, 0x7F]));
}

static void AudioTimestampsRemainMonotonic()
{
    var normalizer = new MonotonicAudioClock(TimeSpan.FromMilliseconds(20));
    var first = normalizer.Normalize(TimeSpan.FromMilliseconds(100));
    var second = normalizer.Normalize(TimeSpan.FromMilliseconds(90));

    Require(first == TimeSpan.FromMilliseconds(100), "First timestamp changed.");
    Require(second == TimeSpan.FromMilliseconds(120), "Regressing timestamp was not normalized.");
}

static void WaveSegmentWriterProducesWav()
{
    var root = TestDirectories.Create("audio-tests");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "segment.wav");
    try
    {
        using (var writer = new WaveSegmentWriter(path, AudioFormat.ClassroomDefault))
        {
            writer.Write([0, 0, 1, 0, 2, 0, 3, 0]);
        }

        var bytes = File.ReadAllBytes(path);
        Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Require(bytes.Length > 44, "WAV contains no PCM payload.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void BoundedAudioQueueDropsOldest()
{
    var format = AudioFormat.ClassroomDefault;
    var queue = new BoundedAudioFrameQueue(2);
    queue.TryWrite(new AudioFrame(new byte[] { 1 }, TimeSpan.FromMilliseconds(10), format));
    queue.TryWrite(new AudioFrame(new byte[] { 2 }, TimeSpan.FromMilliseconds(20), format));
    queue.TryWrite(new AudioFrame(new byte[] { 3 }, TimeSpan.FromMilliseconds(30), format));

    var first = queue.ReadAsync().AsTask().GetAwaiter().GetResult();
    var second = queue.ReadAsync().AsTask().GetAwaiter().GetResult();

    Require(first.Pcm.Span[0] == 2, "Oldest frame was not dropped.");
    Require(second.Pcm.Span[0] == 3, "Newest frame was not preserved.");
}

static void QwenSessionUpdateConfiguresPcm()
{
    var json = QwenAsrProtocol.CreateSessionUpdate(AudioFormat.ClassroomDefault);

    Require(json.Contains("\"type\":\"session.update\"", StringComparison.Ordinal), "Missing session.update.");
    Require(json.Contains("\"input_audio_format\":\"pcm\"", StringComparison.Ordinal), "PCM was not configured.");
    Require(json.Contains("\"sample_rate\":16000", StringComparison.Ordinal), "Sample rate was not configured.");
    Require(json.Contains("\"type\":\"server_vad\"", StringComparison.Ordinal), "Server VAD was not configured.");
    Require(json.Contains("\"silence_duration_ms\":1200", StringComparison.Ordinal), "Classroom VAD should wait for a natural pause.");
}

static void QwenInterimCombinesTextAndStash()
{
    const string json = """
        {"type":"conversation.item.input_audio_transcription.text","item_id":"item-1","text":"The model ","stash":"について説明します","language":"ja","emotion":"neutral"}
        """;

    var result = QwenAsrProtocol.ParseServerEvent(json, TimeSpan.FromSeconds(3));

    Require(result is RecognitionEvent, "Interim event was not parsed.");
    var recognition = (RecognitionEvent)result!;
    Equal("The model について説明します", recognition.Text);
    Equal("ja", recognition.Language);
    Require(!recognition.IsFinal, "Interim event was marked final.");
}

static void QwenCompletedBecomesFinal()
{
    const string json = """
        {"type":"conversation.item.input_audio_transcription.completed","item_id":"item-1","transcript":"This is 最終結果。","language":"ja","emotion":"neutral"}
        """;

    var result = QwenAsrProtocol.ParseServerEvent(json, TimeSpan.FromSeconds(4));
    var recognition = result as RecognitionEvent ?? throw new InvalidOperationException("Final event was not parsed.");

    Equal("This is 最終結果。", recognition.Text);
    Require(recognition.IsFinal, "Completed event was not marked final.");
    Require(recognition.AudioPosition == TimeSpan.FromSeconds(4), "Client audio timestamp was not attached.");
}

static void QwenErrorsAreSafe()
{
    const string json = """
        {"type":"error","error":{"code":"invalid_api_key","message":"Authorization: Bearer should-not-leak"}}
        """;

    var exception = Expect<QwenProviderException>(() => QwenAsrProtocol.ParseServerEvent(json, TimeSpan.Zero));

    Require(!exception.Message.Contains("should-not-leak", StringComparison.Ordinal), "Provider secret leaked into error text.");
    Equal("invalid_api_key", exception.Code);
}

static void QwenEndpointIsSafe()
{
    var endpoint = QwenEndpoint.Singapore("ws-abc123");
    Equal("wss://ws-abc123.ap-southeast-1.maas.aliyuncs.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime", endpoint.ToString());

    _ = Expect<ArgumentException>(() => QwenEndpoint.Singapore("evil.example.com/path"));
}

static void SettingsFileRoundTrips()
{
    var root = TestDirectories.Create("settings-tests");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "settings.json");
    try
    {
        var store = new AppSettingsFileStore(path);
        var expected = new AppSettings { WorkspaceId = "ws-test123", AudioRetentionDays = 14 };
        store.SaveAsync(expected).GetAwaiter().GetResult();
        var actual = store.LoadAsync().GetAwaiter().GetResult();

        Equal("ws-test123", actual.WorkspaceId);
        var json = File.ReadAllText(path);
        Require(!json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "Settings file contains an API key field.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void QwenMtTargetsChinese()
{
    var json = QwenMtProtocol.CreateRequest("Today は attention mechanism を説明します。", TranslationDirection.MixedToChinese);
    Require(json.Contains("\"model\":\"qwen-mt-flash\"", StringComparison.Ordinal), "Wrong translation model.");
    Require(json.Contains("\"source_lang\":\"auto\"", StringComparison.Ordinal), "Source language is not automatic.");
    Require(json.Contains("\"target_lang\":\"Chinese\"", StringComparison.Ordinal), "Target language is not Chinese.");
}

static void TranslationDirectionsExposeMetadata()
{
    Equal("Chinese", TranslationDirection.MixedToChinese.TargetLanguage);
    Equal("Japanese", TranslationDirection.ChineseToJapanese.TargetLanguage);
    Equal("English", TranslationDirection.ChineseToEnglish.TargetLanguage);
    Require(TranslationDirection.MixedToChinese.EnableSlideFollowing, "Listening mode should follow slides.");
    Require(!TranslationDirection.ChineseToJapanese.EnableSlideFollowing, "Speaking mode should not follow slides.");
    Equal("日语翻译", TranslationDirection.ChineseToJapanese.OutputLabel);
}

static void ListeningModesUseExplicitLanguages()
{
    Equal("Japanese", TranslationDirection.JapaneseToChinese.SourceLanguage);
    Equal("English", TranslationDirection.EnglishToChinese.SourceLanguage);
    Equal("Chinese", TranslationDirection.JapaneseToChinese.TargetLanguage);
    Require(TranslationDirection.JapaneseToChinese.EnableSlideFollowing, "Japanese listening should follow slides.");
    Require(TranslationDirection.EnglishToChinese.EnableSlideFollowing, "English listening should follow slides.");
    Equal("ja", TranslationDirection.JapaneseToChinese.AsrLanguage ?? string.Empty);
    Equal("en", TranslationDirection.EnglishToChinese.AsrLanguage ?? string.Empty);
    Require(TranslationDirection.MixedToChinese.AsrLanguage is null, "Mixed listening should let ASR auto-detect language.");
    Require(TranslationInputPolicy.ShouldTranslate(TranslationDirection.JapaneseToChinese, "ja", "説明します"), "Japanese mode rejected Japanese.");
    Require(!TranslationInputPolicy.ShouldTranslate(TranslationDirection.JapaneseToChinese, "en", "explain"), "Japanese mode accepted English.");
}

static void QwenAsrReceivesSourceLanguage()
{
    var japanese = QwenAsrProtocol.CreateSessionUpdate(AudioFormat.ClassroomDefault, TranslationDirection.JapaneseToChinese.AsrLanguage);
    var english = QwenAsrProtocol.CreateSessionUpdate(AudioFormat.ClassroomDefault, TranslationDirection.EnglishToChinese.AsrLanguage);
    var mixed = QwenAsrProtocol.CreateSessionUpdate(AudioFormat.ClassroomDefault, TranslationDirection.MixedToChinese.AsrLanguage);
    Require(japanese.Contains("\"language\":\"ja\"", StringComparison.Ordinal), "Japanese ASR language is missing.");
    Require(english.Contains("\"language\":\"en\"", StringComparison.Ordinal), "English ASR language is missing.");
    Require(!mixed.Contains("\"language\"", StringComparison.Ordinal), "Mixed ASR should omit language for auto detection.");
}

static void CoursesPersistNavigation()
{
    var root = TestDirectories.Create("course-navigation");
    Directory.CreateDirectory(root);
    try
    {
        var repository = new SqliteTimelineRepository(Path.Combine(root, "timeline.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var course = new Course(Guid.NewGuid(), "日本经济", DateTimeOffset.Now, false);
        repository.UpsertCourseAsync(course).GetAwaiter().GetResult();
        var first = new Session(Guid.NewGuid(), course.Name, DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, SessionStatus.Completed) { CourseId = course.Id };
        var latest = new Session(Guid.NewGuid(), course.Name, DateTimeOffset.Now, DateTimeOffset.Now, SessionStatus.Completed) { CourseId = course.Id, StudyPackPath = "study.md" };
        repository.UpsertSessionAsync(first).GetAwaiter().GetResult();
        repository.UpsertSessionAsync(latest).GetAwaiter().GetResult();
        var sessions = repository.GetSessionsForCourseAsync(course.Id).GetAwaiter().GetResult();
        Equal(latest.Id.ToString("D"), sessions[0].Id.ToString("D"));
        Equal("study.md", sessions[0].StudyPackPath ?? string.Empty);
        Require(sessions[0].LessonNumber == 2, "Latest classroom was not numbered as lesson two.");
        Require(sessions[1].LessonNumber == 1, "Oldest classroom was not numbered as lesson one.");
        repository.UpsertCourseAsync(course with { Name = "日本经济学", IsArchived = true }).GetAwaiter().GetResult();
        Require(repository.GetCoursesAsync(false).GetAwaiter().GetResult().Count == 0, "Archived course remained active.");
        Equal("日本经济学", repository.GetCoursesAsync(true).GetAwaiter().GetResult()[0].Name);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}

static void NearbyRecordsBecomeOneLesson()
{
    var courseId = Guid.NewGuid();
    var start = DateTimeOffset.Parse("2026-07-13T13:53:00+09:00");
    var sessions = new[]
    {
        new Session(Guid.NewGuid(), "课堂", start, start.AddMinutes(2), SessionStatus.Interrupted) { CourseId = courseId },
        new Session(Guid.NewGuid(), "课堂", start.AddMinutes(4), start.AddMinutes(7), SessionStatus.Interrupted) { CourseId = courseId },
        new Session(Guid.NewGuid(), "课堂", start.AddHours(2), start.AddHours(3), SessionStatus.Completed) { CourseId = courseId }
    };
    var merged = LessonRecord.Build(sessions, true);
    var raw = LessonRecord.Build(sessions, false);
    Require(merged.Count == 2 && merged.Single(item => item.LessonNumber == 1).Sessions.Count == 2,
        "Nearby interrupted records were not grouped into the same lesson.");
    Require(raw.Count == 3, "Raw record option still grouped sessions.");

    var continuedKey = Guid.NewGuid().ToString("N");
    var continued = new[]
    {
        sessions[0] with { LessonKey = continuedKey },
        sessions[2] with { LessonKey = continuedKey }
    };
    Require(LessonRecord.Build(continued, true).Count == 1,
        "An explicitly continued lesson was split because of its time gap.");

    var crashRestart = new[]
    {
        sessions[0] with { LessonKey = "before-crash" },
        sessions[1] with { LessonKey = "after-restart", Status = SessionStatus.Live }
    };
    Require(LessonRecord.Build(crashRestart, true).Count == 1,
        "A nearby restart after an interrupted session was split by its new lesson key.");
}

static void LessonsCanBeMergedManually()
{
    var root = TestDirectories.Create("merge-lessons");
    try
    {
        var repository = new SqliteTimelineRepository(Path.Combine(root, "timeline.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var course = new Course(Guid.NewGuid(), "人AI交互", DateTimeOffset.Now, false);
        var first = new Session(Guid.NewGuid(), course.Name, DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now.AddHours(-1), SessionStatus.Completed)
            { CourseId = course.Id, LessonKey = "lesson-first", StudyPackPath = "old-first.md" };
        var second = new Session(Guid.NewGuid(), course.Name, DateTimeOffset.Now, DateTimeOffset.Now, SessionStatus.Completed)
            { CourseId = course.Id, LessonKey = "lesson-second", StudyPackPath = "old-second.md" };
        repository.UpsertCourseAsync(course).GetAwaiter().GetResult();
        repository.UpsertSessionAsync(first).GetAwaiter().GetResult();
        repository.UpsertSessionAsync(second).GetAwaiter().GetResult();
        repository.UpsertAiQuestionAsync(new AiQuestionRecord(
            Guid.NewGuid(), "lesson-second", course.Id, DateTimeOffset.Now, "考试范围是什么？", null, "第三章", 5, "10:00",
            "qwen-flash", AiQuestionStatus.Completed, null)).GetAwaiter().GetResult();

        repository.MergeLessonsAsync(course.Id, "lesson-second", "lesson-first").GetAwaiter().GetResult();
        var sessions = repository.GetSessionsForCourseAsync(course.Id).GetAwaiter().GetResult();
        Require(LessonRecord.Build(sessions, true).Count == 1, "Merged lessons are still displayed separately.");
        Require(sessions.All(item => item.LessonKey == "lesson-first"), "Session lesson keys were not unified.");
        Require(sessions.All(item => item.StudyPackPath is null), "A stale pre-merge study pack remains linked.");
        Require(repository.GetAiQuestionsAsync("lesson-first").GetAwaiter().GetResult().Count == 1,
            "AI questions from the merged lesson were lost.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void ClassroomRecordCanBeDeleted()
{
    var root = TestDirectories.Create("delete-session");
    Directory.CreateDirectory(root);
    try
    {
        var repository = new SqliteTimelineRepository(Path.Combine(root, "timeline.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var course = new Course(Guid.NewGuid(), "保留课程", DateTimeOffset.Now, false);
        var session = new Session(Guid.NewGuid(), course.Name, DateTimeOffset.Now, DateTimeOffset.Now, SessionStatus.Completed) { CourseId = course.Id };
        repository.UpsertCourseAsync(course).GetAwaiter().GetResult();
        repository.UpsertSessionAsync(session).GetAwaiter().GetResult();
        repository.UpsertTranscriptAsync(new TranscriptSegment(Guid.NewGuid(), session.Id, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "test", "测试", true, "en", 1)).GetAwaiter().GetResult();
        repository.DeleteSessionAsync(session.Id).GetAwaiter().GetResult();
        Require(repository.GetSessionAsync(session.Id).GetAwaiter().GetResult() is null, "Deleted classroom still exists.");
        Require(repository.GetTranscriptsAsync(session.Id).GetAwaiter().GetResult().Count == 0, "Deleted classroom transcripts still exist.");
        Require(repository.GetCoursesAsync(false).GetAwaiter().GetResult().Any(item => item.Id == course.Id), "Deleting a classroom deleted its course.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}

static void OldSettingsDefaultDirection()
{
    var settings = JsonSerializer.Deserialize<AppSettings>("{\"WorkspaceId\":\"ws-legacy\"}")
        ?? throw new InvalidOperationException("Settings did not deserialize.");
    Equal(TranslationDirection.MixedToChinese.Id, settings.TranslationDirectionId);
}

static void TranslationDirectionLocksWhileRunning()
{
    Require(TranslationModeState.DirectionSelectorEnabled(false), "Direction selector should be enabled while idle.");
    Require(!TranslationModeState.DirectionSelectorEnabled(true), "Direction selector should be locked while live.");
    Require(TranslationModeState.ShouldFollowSlides(TranslationDirection.MixedToChinese), "Listening mode should follow slides.");
    Require(!TranslationModeState.ShouldFollowSlides(TranslationDirection.ChineseToEnglish), "Speaking mode should pause slide following.");
}

static void ChineseModesRequireChineseInput()
{
    Require(TranslationInputPolicy.ShouldTranslate(TranslationDirection.MixedToChinese, "en", "attention mechanism"), "Listening mode should accept English.");
    Require(TranslationInputPolicy.ShouldTranslate(TranslationDirection.ChineseToJapanese, "zh", "请开始实验。"), "Chinese speech should be translated.");
    Require(TranslationInputPolicy.ShouldTranslate(TranslationDirection.ChineseToEnglish, "en", "请开始实验。"), "Chinese text evidence should override an uncertain ASR label.");
    Require(!TranslationInputPolicy.ShouldTranslate(TranslationDirection.ChineseToJapanese, "en", "start experiment"), "English speech should be retained but not translated in Chinese mode.");
}

static void QwenMtTargetsJapanese()
{
    var json = QwenMtProtocol.CreateRequest("请在下周前完成实验。", TranslationDirection.ChineseToJapanese);
    Require(json.Contains("\"source_lang\":\"Chinese\"", StringComparison.Ordinal), "Source language is not Chinese.");
    Require(json.Contains("\"target_lang\":\"Japanese\"", StringComparison.Ordinal), "Target language is not Japanese.");
    Require(json.Contains("technical terms", StringComparison.Ordinal), "Term preservation guidance is missing.");
}

static void QwenMtTargetsEnglish()
{
    var json = QwenMtProtocol.CreateRequest("验证准确率提高了三个百分点。", TranslationDirection.ChineseToEnglish);
    Require(json.Contains("\"source_lang\":\"Chinese\"", StringComparison.Ordinal), "Source language is not Chinese.");
    Require(json.Contains("\"target_lang\":\"English\"", StringComparison.Ordinal), "Target language is not English.");
}

static void QwenMtExtractsContent()
{
    const string json = """
        {"choices":[{"message":{"role":"assistant","content":"今天讲解注意力机制。"}}],"usage":{"total_tokens":12}}
        """;
    Equal("今天讲解注意力机制。", QwenMtProtocol.ParseResponse(json));
}

static void PptxExtractorPreservesSlides()
{
    var root = TestDirectories.Create("slide-tests");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "lecture.pptx");
    try
    {
        CreatePresentation(path, "Introduction to Attention", "Self-attention and Q K V");
        var document = new PptxSlideExtractor().Extract(path);

        Require(document.Pages.Count == 2, "Wrong slide count.");
        Require(document.Pages[0].PageNumber == 1, "First page number is wrong.");
        Equal("Introduction to Attention", document.Pages[0].Text);
        Equal("Self-attention and Q K V", document.Pages[1].Text);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void CreatePresentation(string path, params string[] slideTexts)
{
    using var presentation = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
    var presentationPart = presentation.AddPresentationPart();
    presentationPart.Presentation = new P.Presentation(new P.SlideIdList());
    uint id = 256;
    foreach (var slideText in slideTexts)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.Slide = new P.Slide(new P.CommonSlideData(
            new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2U, Name = "Text" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text(slideText))))))));
        slidePart.Slide.Save();
        presentationPart.Presentation.SlideIdList!.Append(new P.SlideId
        {
            Id = id++,
            RelationshipId = presentationPart.GetIdOfPart(slidePart)
        });
    }

    presentationPart.Presentation.Save();
}

static void PdfExtractorPreservesPages()
{
    var root = TestDirectories.Create("slide-tests");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "lecture.pdf");
    try
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Page one attention", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PageSize.A4).AddText("Page two transformer", 12, new PdfPoint(25, 700), font);
        File.WriteAllBytes(path, builder.Build());

        var document = new PdfSlideExtractor().Extract(path);

        Require(document.Pages.Count == 2, "Wrong PDF page count.");
        Require(document.Pages[0].Text.Contains("Page one attention", StringComparison.Ordinal), "First PDF page text missing.");
        Require(document.Pages[1].Text.Contains("Page two transformer", StringComparison.Ordinal), "Second PDF page text missing.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void PdfRendererExportsPageImages()
{
    var root = Path.Combine(Path.GetTempPath(), "class-interpreter-pdf-render", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var pdfPath = Path.Combine(root, "lesson.pdf");
        using (var builder = new PdfDocumentBuilder())
        {
            var page = builder.AddPage(PageSize.A4);
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText("Rendered lesson page", 18, new PdfPoint(50, 750), font);
            File.WriteAllBytes(pdfPath, builder.Build());
        }

        var renderer = new PdfPageImageRenderer();
        var first = renderer.Render(pdfPath, Path.Combine(root, "cache"), 1);
        var timestamp = File.GetLastWriteTimeUtc(first[1]);
        var second = renderer.Render(pdfPath, Path.Combine(root, "cache"), 1);
        Require(File.Exists(first[1]) && new FileInfo(first[1]).Length > 100, "PDF page image was not created.");
        Equal(first[1], second[1]);
        Require(timestamp == File.GetLastWriteTimeUtc(second[1]), "Cached PDF page was rendered again.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void SlideMatcherFocusesRelevantPage()
{
    var document = MatchingDocument();
    var result = new SlideMatcher().Match(new SlideMatchContext(document, 1, ["Now we explain transformer self attention Q K V"]));

    Require(result.Candidates[0].PageNumber == 2, "Relevant page was not ranked first.");
    Require(result.Candidates[0].AutoFocusAllowed, "High confidence nearby page was not allowed to focus.");
}

static void SlideMatcherHandlesCjkText()
{
    var document = new SlideDocument("fixture.pptx",
    [
        new SlidePage(1, "课程介绍", "人工智能课程目标与学习安排", string.Empty),
        new SlidePage(2, "提问环节", "关于课堂问题的提问与老师解答", string.Empty),
        new SlidePage(3, "モデル評価", "精度と再現率によるモデル評価", string.Empty)
    ]);
    var result = new SlideMatcher().Match(new SlideMatchContext(document, 1, ["下面进入提问环节，老师会解答课堂问题"]));

    Require(result.Candidates[0].PageNumber == 2, "Chinese slide text was not ranked first.");
    Require(result.Candidates[0].AutoFocusAllowed, "Strong Chinese evidence did not allow an adjacent turn.");
}

static void SlideMatcherPrefersRecentSpeech()
{
    var document = MatchingDocument();
    var result = new SlideMatcher().Match(new SlideMatchContext(document, 2,
    [
        "transformer self attention query key value",
        "attention mechanism",
        "now model training loss optimizer",
        "training process and optimizer"
    ]));

    Require(result.Candidates[0].PageNumber == 3, "Older slide content outweighed the recent topic.");
}

static void SlideMatcherRefusesDistantJump()
{
    var document = MatchingDocument();
    var result = new SlideMatcher().Match(new SlideMatchContext(document, 4, ["introduction overview"]));

    var pageOne = result.Candidates.Single(candidate => candidate.PageNumber == 1);
    Require(!pageOne.AutoFocusAllowed, "Brief distant reference caused an automatic jump.");
}

static void SlideMatcherReturnsThreeCandidates()
{
    var result = new SlideMatcher().Match(new SlideMatchContext(MatchingDocument(), 2, ["model training attention results"]));
    Require(result.Candidates.Count <= 3, "More than three candidates were returned.");
}

static void SlideFollowRequiresStableEvidence()
{
    var controller = new SlideFollowController();
    controller.SetMode(SlideFollowMode.Automatic);
    var high = new SlideMatchResult(
    [
        new SlideCandidate(3, 0.81, ["transformer", "attention", "query"], true),
        new SlideCandidate(2, 0.44, ["attention"], false)
    ]);
    var first = controller.Evaluate(high, 2, DateTimeOffset.Parse("2026-07-22T10:00:00+09:00"));
    var second = controller.Evaluate(high, 2, DateTimeOffset.Parse("2026-07-22T10:00:03+09:00"));
    Require(first.Kind != SlideFollowDecisionKind.AutoNavigate, "One matching subtitle triggered an automatic page jump.");
    Require(second.Kind == SlideFollowDecisionKind.AutoNavigate && second.Candidate?.PageNumber == 3,
        "Stable high-confidence matching did not enable a guarded automatic follow.");

    var low = new SlideMatchResult(
    [
        new SlideCandidate(4, 0.40, ["model"], false),
        new SlideCandidate(3, 0.37, ["model"], false)
    ]);
    controller.SetMode(SlideFollowMode.Automatic);
    Require(controller.Evaluate(low, 3, DateTimeOffset.Now).Kind == SlideFollowDecisionKind.None,
        "Low-confidence match produced a follow action.");
}

static void SlideFollowPausesForStudent()
{
    var controller = new SlideFollowController();
    controller.SetMode(SlideFollowMode.Suggest);
    controller.PauseForManualNavigation();
    var result = new SlideMatchResult([new SlideCandidate(4, 0.91, ["term", "topic", "title"], true)]);
    var paused = controller.Evaluate(result, 3, DateTimeOffset.Now);
    Require(paused.Kind == SlideFollowDecisionKind.None && controller.IsPausedByStudent,
        "Manual browsing did not pause slide following.");
    controller.Resume();
    Require(!controller.IsPausedByStudent, "Resume did not restore follow eligibility.");
}

static SlideDocument MatchingDocument() => new("fixture.pptx",
[
    new SlidePage(1, "Introduction", "introduction overview course", string.Empty),
    new SlidePage(2, "Self Attention", "transformer self attention query key value Q K V", string.Empty),
    new SlidePage(3, "Training", "model training loss optimizer", string.Empty),
    new SlidePage(4, "Results", "experiment results accuracy evaluation", string.Empty)
]);

static void StudyPackRequestHasSections()
{
    var json = QwenStudyPackProtocol.CreateRequest("[00:01] hello こんにちは -> 你好");
    Require(json.Contains("\"model\":\"qwen3.7-plus\"", StringComparison.Ordinal), "The stronger study-summary model is not selected.");
    Require(json.Contains("考试、作业与成绩评定", StringComparison.Ordinal), "Assessment information is not prioritized.");
    Require(json.Contains("成绩构成", StringComparison.Ordinal) && json.Contains("出勤要求", StringComparison.Ordinal),
        "Grading and attendance requirements are not requested.");
    Require(json.Contains("核心概念", StringComparison.Ordinal), "Core concepts were not requested.");
    Require(json.Contains("五道带答案的复习题", StringComparison.Ordinal), "Review questions were not requested.");
    Require(json.Contains("绝不推测", StringComparison.Ordinal), "Invented assessment details were not prohibited.");
    var analyzer = File.ReadAllText(Path.Combine("src", "ClassInterpreter.Infrastructure", "StudyPacks", "QwenStudyPackAnalyzer.cs"));
    Require(analyzer.Contains("MaxAttempts = 3", StringComparison.Ordinal)
            && analyzer.Contains("Timeout = TimeSpan.FromMinutes(5)", StringComparison.Ordinal)
            && analyzer.Contains("新加坡备用地址均已自动重试", StringComparison.Ordinal),
        "Study summary requests do not retry with a readable recovery message.");
}

static void MarkdownStudyPackIncludesTranscript()
{
    var session = new Session(Guid.NewGuid(), "AI Seminar", DateTimeOffset.Parse("2026-07-11T10:00:00+09:00"), DateTimeOffset.Parse("2026-07-11T11:00:00+09:00"), SessionStatus.Completed);
    var segment = new TranscriptSegment(Guid.NewGuid(), session.Id, 1, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "attention について", "关于注意力机制", true, "mixed", 0.9);
    var markdown = MarkdownStudyPackWriter.Render(session, "## 课堂摘要\n重点介绍注意力。", [segment]);

    Require(markdown.Contains("AI 课堂学习包：AI Seminar", StringComparison.Ordinal), "Course title missing.");
    Require(markdown.Contains("[00:03]", StringComparison.Ordinal), "Timestamp missing.");
    Require(markdown.Contains("attention について", StringComparison.Ordinal), "Source transcript missing.");
    Require(markdown.Contains("关于注意力机制", StringComparison.Ordinal), "Chinese transcript missing.");
}

static void MarkdownStudyPackIncludesDirection()
{
    var session = new Session(Guid.NewGuid(), "Lab", DateTimeOffset.Now, DateTimeOffset.Now, SessionStatus.Completed);
    var segment = new TranscriptSegment(Guid.NewGuid(), session.Id, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "请开始实验。", null, true, "zh", 1)
    {
        TargetText = "実験を始めてください。",
        TranslationDirectionId = TranslationDirection.ChineseToJapanese.Id
    };
    var markdown = MarkdownStudyPackWriter.Render(session, "## 摘要\n双向演示。", [segment]);
    Require(markdown.Contains("中文 → 日语", StringComparison.Ordinal), "Direction label missing.");
    Require(markdown.Contains("実験を始めてください。", StringComparison.Ordinal), "Target translation missing.");
}

static void LiveAudioHubRecordsIndependently()
{
    var root = TestDirectories.Create("audio-hub-tests");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "hub.wav");
    try
    {
        var format = AudioFormat.ClassroomDefault;
        var source = new SequenceAudioSource(
        [
            new AudioFrame(new byte[] { 0, 0 }, TimeSpan.Zero, format),
            new AudioFrame(new byte[] { 1, 0 }, TimeSpan.FromMilliseconds(20), format),
            new AudioFrame(new byte[] { 2, 0 }, TimeSpan.FromMilliseconds(40), format)
        ]);
        var hub = new LiveAudioSession(source, 0, path, format);
        var pump = hub.PumpAsync();
        var frames = new List<AudioFrame>();
        var read = Task.Run(async () =>
        {
            await foreach (var frame in hub.ReadAllAsync())
            {
                frames.Add(frame);
            }
        });
        Task.WhenAll(pump, read).GetAwaiter().GetResult();

        Require(frames.Count == 3, "Audio hub lost frames.");
        Require(File.Exists(path) && new FileInfo(path).Length > 44, "Audio hub did not write WAV independently.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void PowerPointRendererExportsThumbnails()
{
    var root = Path.Combine(@"D:\AM-LINK", "data", "office-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "office-fixture.pptx");
    object? app = null;
    object? deck = null;
    try
    {
        var type = Type.GetTypeFromProgID("PowerPoint.Application")
            ?? throw new InvalidOperationException("PowerPoint COM is unavailable.");
        app = Activator.CreateInstance(type);
        dynamic powerpoint = app!;
        deck = powerpoint.Presentations.Add(0);
        dynamic presentation = deck;
        for (var page = 1; page <= 2; page++)
        {
            object? slide = null;
            object? shape = null;
            try
            {
                slide = presentation.Slides.Add(page, 12);
                shape = ((dynamic)slide).Shapes.AddTextbox(1, 40, 40, 800, 100);
                ((dynamic)shape).TextFrame.TextRange.Text = $"Integration slide {page}";
            }
            finally
            {
                ReleaseCom(shape);
                ReleaseCom(slide);
            }
        }

        presentation.SaveAs(path);
        presentation.Close();
        ReleaseCom(deck);
        deck = null;
        powerpoint.Quit();
        ReleaseCom(app);
        app = null;

        var thumbnails = new PowerPointThumbnailRenderer().Render(path, Path.Combine(root, "cache"));
        Require(thumbnails.Count == 2, "PowerPoint thumbnail page count is wrong.");
        Require(thumbnails.Values.All(file => File.Exists(file) && new FileInfo(file).Length > 1000), "A rendered PNG is missing or empty.");
    }
    finally
    {
        try { if (deck is not null) ((dynamic)deck).Close(); } catch { }
        try { if (app is not null) ((dynamic)app).Quit(); } catch { }
        ReleaseCom(deck);
        ReleaseCom(app);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DemoScenarioCoversCoreFlow()
{
    var scenario = DemoScenario.Create();
    Require(scenario.Slides.Pages.Count >= 3, "Demo has too few slides.");
    Require(scenario.Utterances.Any(item => item.Language == "mixed"), "Demo lacks mixed English/Japanese speech.");
    Require(scenario.Utterances.All(item => !string.IsNullOrWhiteSpace(item.Chinese)), "Demo lacks Chinese translations.");
    Require(scenario.Utterances.Select(item => item.TargetPage).Distinct().Count() >= 3, "Demo does not exercise slide changes.");
}

static void DemoScenarioCoversSpeakingDirections()
{
    var scenario = DemoScenario.Create();
    var utterance = scenario.Utterances.First();
    Require(!string.IsNullOrWhiteSpace(utterance.TargetFor(TranslationDirection.ChineseToJapanese)), "Japanese demo text missing.");
    Require(!string.IsNullOrWhiteSpace(utterance.TargetFor(TranslationDirection.ChineseToEnglish)), "English demo text missing.");
}

static void UiRendererWaitsForInitialization()
{
    var root = FindProjectRoot();
    var windowCode = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    var appCode = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "App.xaml.cs"));
    Require(windowCode.Contains("InitializationCompleted", StringComparison.Ordinal), "Window initialization signal missing.");
    Require(appCode.Contains("await window.InitializationCompleted", StringComparison.Ordinal), "UI renderer does not await initialization.");
}

static void ApplicationUsesPortableRootResolver()
{
    var root = FindProjectRoot();
    var appCode = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "App.xaml.cs"));
    var windowCode = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(appCode.Contains("AppRootResolver.ResolveDefault()", StringComparison.Ordinal), "App startup still bypasses the resolver.");
    Require(windowCode.Contains("AppRootResolver.ResolveDefault()", StringComparison.Ordinal), "Main window still bypasses the resolver.");
    Require(!appCode.Contains("AppPaths.Create(@\"D:", StringComparison.Ordinal) && !windowCode.Contains("AppPaths.Create(@\"D:", StringComparison.Ordinal),
        "A hard-coded D application root remains.");
    Require(xaml.Contains("x:Name=\"DataRootText\"", StringComparison.Ordinal), "Resolved data path is not visible in settings.");
}

static void MacInputsCenterContent()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "App.xaml"));
    Require(xaml.Split("x:Name=\"PART_ContentHost\" Margin=\"12,0\" VerticalAlignment=\"Center\"", StringSplitOptions.None).Length == 3,
        "TextBox and PasswordBox content hosts are not vertically centered.");
}

static void AmLinkBrandingIsPresent()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(xaml.Contains("Text=\"AM-LINK\"", StringComparison.Ordinal), "AM-LINK title missing.");
    Require(xaml.Contains("Developed by AppleMccree", StringComparison.Ordinal), "Developer credit missing.");
}

static void ClassroomWorkspacePrioritizesContent()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(xaml.Contains("x:Name=\"LessonWorkspaceGrid\"", StringComparison.Ordinal), "Main lesson workspace missing.");
    Require(xaml.Contains("Width=\"3*\"", StringComparison.Ordinal) && xaml.Contains("Width=\"2*\"", StringComparison.Ordinal),
        "Slides and subtitles are not arranged in the approved 60/40 layout.");
}

static void SettingsDrawerIsInteractive()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(xaml.Contains("x:Name=\"SettingsDrawer\"", StringComparison.Ordinal) && xaml.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal),
        "Settings drawer is not collapsed by default.");
    Require(xaml.Contains("x:Name=\"SettingsButton\"", StringComparison.Ordinal) && xaml.Contains("Click=\"SettingsButton_Click\"", StringComparison.Ordinal),
        "Settings does not have a real click action.");
}

static void SlideNavigationIsInteractive()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(xaml.Contains("x:Name=\"PreviousSlideButton\"", StringComparison.Ordinal) && xaml.Contains("Click=\"PreviousSlideButton_Click\"", StringComparison.Ordinal),
        "Previous-page button is missing or decorative.");
    Require(xaml.Contains("x:Name=\"NextSlideButton\"", StringComparison.Ordinal) && xaml.Contains("Click=\"NextSlideButton_Click\"", StringComparison.Ordinal),
        "Next-page button is missing or decorative.");
}

static void HistoryViewIsUsable()
{
    var root = FindProjectRoot();
    var xaml = File.ReadAllText(Path.Combine(root, "src", "ClassInterpreter.App", "MainWindow.xaml"));
    Require(xaml.Contains("x:Name=\"HistoryTranscriptBox\"", StringComparison.Ordinal) &&
            xaml.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal) &&
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
    return directory?.FullName ?? @"D:\AM-LINK";
}

static void TranscriptSegmentIdsAreSessionScoped()
{
    var sessionA = Guid.NewGuid();
    var sessionB = Guid.NewGuid();
    var first = TranscriptIdentity.CreateSegmentId(sessionA, "item_1");
    Require(first == TranscriptIdentity.CreateSegmentId(sessionA, "item_1"), "Segment id is not stable within a session.");
    Require(first != TranscriptIdentity.CreateSegmentId(sessionB, "item_1"), "Provider segment ids collide across sessions.");
    Require(first != TranscriptIdentity.CreateSegmentId(sessionA, "item_2"), "Different provider segments collide inside one session.");

    var windowCode = File.ReadAllText(Path.Combine(FindProjectRoot(), "src", "ClassInterpreter.App", "MainWindow.xaml.cs"));
    Require(windowCode.Contains("TranscriptIdentity.CreateSegmentId(sessionId, recognition.SegmentId)", StringComparison.Ordinal),
        "The classroom loop no longer derives segment ids from the session.");
}

static void ErrorLogWriterRedactsSecrets()
{
    var root = TestDirectories.Create("error-log-tests");
    try
    {
        var path = ErrorLogWriter.Append(root, "单元测试", new InvalidOperationException("请求失败 api_key=sk-super-secret-value"));
        Require(path is not null && File.Exists(path), "Error log file was not written.");
        var content = File.ReadAllText(path!);
        Require(!content.Contains("sk-super-secret-value", StringComparison.Ordinal), "Error log leaked a secret.");
        Require(content.Contains("[REDACTED]", StringComparison.Ordinal), "Error log did not redact the secret.");
        Require(content.Contains("单元测试", StringComparison.Ordinal), "Error log lost its source label.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void ClassroomOutboxPolicyIsSelective()
{
    Require(ClassroomOutboxPolicy.Decide(new ClassroomServerException("课堂已结束", System.Net.HttpStatusCode.NotFound), 0) == OutboxRetryDecision.Drop,
        "A rejected message must be dropped instead of retried forever.");
    Require(ClassroomOutboxPolicy.Decide(new ClassroomServerException("请求过快", System.Net.HttpStatusCode.TooManyRequests), 0) == OutboxRetryDecision.Retry,
        "Rate limiting is transient and must be retried.");
    Require(ClassroomOutboxPolicy.Decide(new ClassroomServerException("服务器内部错误", System.Net.HttpStatusCode.InternalServerError), 0) == OutboxRetryDecision.Retry,
        "Server outages are transient and must be retried.");
    Require(ClassroomOutboxPolicy.Decide(new HttpRequestException("断网"), 0) == OutboxRetryDecision.Retry,
        "Network failures must keep the message queued.");
    Require(ClassroomOutboxPolicy.Decide(new HttpRequestException("长时间断网"), 100_000) == OutboxRetryDecision.Retry,
        "A long outage must never delete a locally queued classroom event.");
    Require(ClassroomOutboxPolicy.RetryDelay(100) <= TimeSpan.FromSeconds(30),
        "Offline retry backoff exceeded its cap.");
}

static void TimelineBackupCreatesRestorableCopy()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    var backups = Path.Combine(fixture.Root, "backups");
    var path = fixture.Repository.BackupAsync(backups, keepCount: 2).GetAwaiter().GetResult();
    Require(path is not null && File.Exists(path), "Backup file was not created.");

    var restored = new SqliteTimelineRepository(path!);
    restored.InitializeAsync().GetAwaiter().GetResult();
    Require(restored.GetSessionAsync(session.Id).GetAwaiter().GetResult() is not null, "Backup lost the saved session.");
    SqliteConnection.ClearAllPools();

    File.WriteAllText(Path.Combine(backups, "timeline-backup-20200101.db"), "old");
    File.WriteAllText(Path.Combine(backups, "timeline-backup-20200102.db"), "old");
    fixture.Repository.BackupAsync(backups, keepCount: 2).GetAwaiter().GetResult();
    Require(Directory.GetFiles(backups, "timeline-backup-*.db").Length == 2, "Backup rotation kept the wrong number of copies.");
}

static void DeletingLessonRemovesAiQuestions()
{
    using var fixture = TimelineFixture.CreateInitialized();
    var session = fixture.StartSession();
    fixture.Repository.UpsertSessionAsync(session with { LessonKey = "lesson-del" }).GetAwaiter().GetResult();
    fixture.Repository.UpsertAiQuestionAsync(new AiQuestionRecord(
        Guid.NewGuid(), "lesson-del", null, DateTimeOffset.Now, "这个概念是什么？", null, "答案",
        null, null, "qwen-flash", AiQuestionStatus.Completed, null)).GetAwaiter().GetResult();
    fixture.Repository.DeleteSessionAsync(session.Id).GetAwaiter().GetResult();
    fixture.Repository.DeleteAiQuestionsForLessonAsync("lesson-del").GetAwaiter().GetResult();
    Require(fixture.Repository.GetAiQuestionsAsync("lesson-del").GetAwaiter().GetResult().Count == 0,
        "AI questions survived after their lesson was deleted.");
}

static void ApplicationRegistersGlobalExceptionGuard()
{
    var appCode = File.ReadAllText(Path.Combine(FindProjectRoot(), "src", "ClassInterpreter.App", "App.xaml.cs"));
    Require(appCode.Contains("DispatcherUnhandledException", StringComparison.Ordinal), "UI-thread exception guard is missing.");
    Require(appCode.Contains("TaskScheduler.UnobservedTaskException", StringComparison.Ordinal), "Background task exception guard is missing.");
    Require(appCode.Contains("ErrorLogWriter.Append", StringComparison.Ordinal), "Unhandled exceptions are not written to the error log.");
    Require(appCode.Contains("IsRecoverableUiException", StringComparison.Ordinal), "UI exceptions are still swallowed without a recoverability check.");
}

static void ClassroomServerIsHardened()
{
    var serverCode = File.ReadAllText(Path.Combine(FindProjectRoot(), "src", "ClassInterpreter.ClassroomServer", "Program.cs"));
    Require(!serverCode.Contains("SetIsOriginAllowed(_ => true)", StringComparison.Ordinal),
        "Wildcard credentialed CORS came back.");
    Require(serverCode.Contains("UseForwardedHeaders", StringComparison.Ordinal), "Reverse-proxy client addresses are not restored before rate limiting.");
    Require(serverCode.Contains("SlidingWindowRateLimiter", StringComparison.Ordinal), "Join rate limiting is missing.");
    Require(serverCode.Contains("MaxParticipantsPerLesson", StringComparison.Ordinal), "Per-lesson participant cap is missing.");
    Require(serverCode.Contains("CountActiveParticipantsAsync", StringComparison.Ordinal), "Participant cap still counts stale historical joins.");
    Require(serverCode.Contains("'{text}", StringComparison.Ordinal) || serverCode.Contains("$\"'{", StringComparison.Ordinal),
        "CSV formula-injection guard is missing.");
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
