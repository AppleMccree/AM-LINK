using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using ClassInterpreter.Core.Audio;
using ClassInterpreter.Core.Speech;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Infrastructure.Audio;
using ClassInterpreter.Infrastructure.Qwen;

namespace ClassInterpreter.App;

public partial class QuickTranslatorWindow : Window
{
    private static readonly TimeSpan RecognitionWatchdog = TimeSpan.FromSeconds(18);
    private readonly string _workspaceId;
    private readonly string _apiKey;
    private readonly int _microphoneIndex;
    private readonly string _recordDirectory;
    private readonly List<SavedTurn> _leftTurns = [];
    private readonly List<SavedTurn> _rightTurns = [];
    private readonly HashSet<string> _processedFinalItems = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private string? _recordPath;
    private DateTimeOffset _startedAt;
    private readonly Func<AiUsageRecord, Task>? _recordUsage;
    private readonly VoiceLanguageProfile _voiceProfile = new();
    private readonly List<TimedVoiceFingerprint> _recentVoice = [];
    private readonly object _voiceGate = new();
    private readonly string _voiceProfilePath;
    private RoutingMode _routingMode = RoutingMode.Automatic;
    private RoutingMode _routingModeBeforeHold = RoutingMode.Automatic;
    private CancellationTokenSource? _recognitionSwitch;
    private TimeSpan _latestAudioPosition;
    private TimeSpan _routeStartedAt;
    // Conversation mode always keeps the source below its translation.  Hiding it made
    // it too hard to verify what was actually heard during a live exchange.
    private bool _showOriginal = true;

    public QuickTranslatorWindow(string workspaceId, string apiKey, int microphoneIndex, string recordDirectory, Func<AiUsageRecord, Task>? recordUsage = null)
    {
        InitializeComponent();
        _workspaceId = workspaceId;
        _apiKey = apiKey;
        _microphoneIndex = microphoneIndex;
        _recordDirectory = recordDirectory;
        _voiceProfilePath = Path.Combine(recordDirectory, "åŒå‘åŒä¼ -éŸ³è‰²è¯­è¨€ç”»åƒ.json");
        _recordUsage = recordUsage;
        LanguagePairBox.ItemsSource = new[]
        {
            TranslationDirection.JapaneseChineseBidirectional,
            TranslationDirection.EnglishChineseBidirectional
        };
        LanguagePairBox.SelectedIndex = 0;
        var microphones = MicrophoneAudioSource.GetDeviceNames();
        MicrophoneBox.ItemsSource = microphones;
        MicrophoneBox.SelectedIndex = microphones.Count == 0
            ? -1
            : Math.Clamp(microphoneIndex, 0, microphones.Count - 1);
        LoadVoiceProfile();
        Closed += (_, _) => _cancellation?.Cancel();
    }

    private TranslationDirection SelectedPair =>
        LanguagePairBox.SelectedItem as TranslationDirection
        ?? TranslationDirection.JapaneseChineseBidirectional;

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation;
        if (MicrophoneBox.SelectedIndex < 0)
        {
            _cancellation.Dispose();
            _cancellation = null;
            SaveStatusText.Text = "è¯·é€‰æ‹©ç°åœºéº¦å…‹é£";
            return;
        }
        var selected = SelectedPair;
        _startedAt = DateTimeOffset.Now;
        _recordPath = null;
        _leftTurns.Clear();
        _rightTurns.Clear();
        _processedFinalItems.Clear();
        LeftHistoryBox.Clear();
        RightHistoryBox.Clear();
        LanguagePairBox.IsEnabled = false;
        MicrophoneBox.IsEnabled = false;
        ForeignRoutingButton.IsEnabled = true;
        AutoRoutingButton.IsEnabled = true;
        HoldToTalkButton.IsEnabled = true;
        ChineseRoutingButton.IsEnabled = true;
        StartButton.Content = "åœæ­¢åŒå‘åŒä¼ ";
        SaveStatusText.Text = "æ­£åœ¨åŒä¼ ï¼›ç¡®è®¤å­—å¹•ä¼šè‡ªåŠ¨ä¿å­˜";

        try
        {
            var incoming = selected == TranslationDirection.JapaneseChineseBidirectional
                ? TranslationDirection.JapaneseToChinese : TranslationDirection.EnglishToChinese;
            var outgoing = selected == TranslationDirection.JapaneseChineseBidirectional
                ? TranslationDirection.ChineseToJapanese : TranslationDirection.ChineseToEnglish;
            var speechEndpoint = QwenEndpoint.Singapore(_workspaceId);
            var translationEndpoint = QwenEndpoint.SingaporeTranslation(_workspaceId);
            using var translationHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var incomingTranslator = new QwenMtTranslator(translationEndpoint, _apiKey, incoming, translationHttpClient);
            using var outgoingTranslator = new QwenMtTranslator(translationEndpoint, _apiKey, outgoing, translationHttpClient);
            var audioBuffer = new SwitchableAudioBuffer(
                new MicrophoneAudioSource(preferredChannels: 1), MicrophoneBox.SelectedIndex);
            audioBuffer.FrameCaptured += OnAudioFrameCaptured;
            var audioPump = audioBuffer.PumpAsync(cancellation.Token);
            try
            {
                SetRoutingMode(RoutingMode.Automatic);
                while (!cancellation.IsCancellationRequested)
                {
                    var route = ForcedRoute(selected);
                    using var recognitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    _recognitionSwitch = recognitionCancellation;
                    try
                    {
                        if (route is null)
                        {
                            var recognizer = new QwenRealtimeAsrClient(
                                speechEndpoint, _apiKey, sourceLanguage: null, silenceDurationMs: 500);
                            await RunAutomaticRecognitionAsync(
                                recognizer, audioBuffer.ReadFromAsync(_routeStartedAt, recognitionCancellation.Token),
                                selected, incomingTranslator, outgoingTranslator, recognitionCancellation.Token);
                        }
                        else
                        {
                            var recognizer = new QwenRealtimeAsrClient(
                                speechEndpoint, _apiKey, route.AsrLanguage, silenceDurationMs: 500);
                            await RunExplicitRecognitionAsync(
                                recognizer,
                                audioBuffer.ReadFromAsync(_routeStartedAt, recognitionCancellation.Token),
                                selected, route,
                                incomingTranslator, outgoingTranslator, recognitionCancellation.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    catch (Exception exception) when (!cancellation.IsCancellationRequested)
                    {
                        SaveStatusText.Text = $"è¯†åˆ«è¿æ¥å¡ä½ï¼Œæ­£åœ¨è‡ªåŠ¨é‡è¿ï¼š{exception.Message}";
                        _routeStartedAt = _latestAudioPosition > TimeSpan.FromSeconds(1.5)
                            ? _latestAudioPosition - TimeSpan.FromSeconds(1.5)
                            : TimeSpan.Zero;
                        await Task.Delay(700, cancellation.Token);
                    }
                    finally
                    {
                        if (ReferenceEquals(_recognitionSwitch, recognitionCancellation)) _recognitionSwitch = null;
                    }
                }
            }
            finally
            {
                audioBuffer.FrameCaptured -= OnAudioFrameCaptured;
                try { await audioPump; } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"åŒå‘åŒä¼ ä¸­æ–­ï¼š{exception.Message}";
        }
        finally
        {
            if (_recordUsage is not null)
                await _recordUsage(new AiUsageRecord(
                    DateOnly.FromDateTime(DateTime.Now), AiUsageKind.SpeechRecognition, QwenAsrProtocol.Model,
                    1, 0, 0, 0, 0, 0, Math.Max(0, (long)(DateTimeOffset.Now - _startedAt).TotalMilliseconds)));
            cancellation.Dispose();
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            LanguagePairBox.IsEnabled = true;
            MicrophoneBox.IsEnabled = true;
            ForeignRoutingButton.IsEnabled = true;
            AutoRoutingButton.IsEnabled = true;
            HoldToTalkButton.IsEnabled = true;
            ChineseRoutingButton.IsEnabled = true;
            StartButton.Content = "å¼€å§‹åŒå‘åŒä¼ ";
            AudioLevelBar.Value = 0;
            LanguageStatusText.Text = "å·²åœæ­¢";
            if (_recordPath is not null) SaveStatusText.Text = "æœ¬æ¬¡åŒå‘å­—å¹•å·²ä¿å­˜";
        }
    }

    private async Task RunAutomaticRecognitionAsync(
        QwenRealtimeAsrClient recognizer,
        IAsyncEnumerable<AudioFrame> audioFrames,
        TranslationDirection selected,
        QwenMtTranslator incomingTranslator,
        QwenMtTranslator outgoingTranslator,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? interim = null;
        try
        {
            await foreach (var recognition in WithRecognitionWatchdog(
                               recognizer.RecognizeAsync(audioFrames, cancellationToken), cancellationToken))
            {
                var recognizedText = RecognitionTextNormalizer.Sanitize(recognition.Text);
                if (string.IsNullOrWhiteSpace(recognizedText)) continue;
                var fingerprint = VoiceFingerprintAt(recognition.AudioPosition);
                var route = ResolveAutomaticRoute(selected, recognition.Language, recognizedText, fingerprint);
                interim?.Cancel();
                interim?.Dispose();
                interim = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (route is null)
                {
                    LanguageStatusText.Text = BidirectionalTranslationRouter.IsShortBackchannel(recognizedText)
                        ? "AI è‡ªåŠ¨åˆ¤æ–­ Â· ç®€çŸ­é™„å’Œä¸è¿›å…¥ä»»ä¸€ä¾§"
                        : "AI è‡ªåŠ¨åˆ¤æ–­ä¸­ Â· è¯æ®ä¸è¶³ï¼Œæš‚ä¸æ”¾å…¥å·¦å³å­—å¹•";
                    continue;
                }

                var isLeft = route.SourceLanguage == "Chinese";
                var side = isLeft ? "æˆ‘æ–¹" : "å¯¹æ–¹å¤šäºº";
                LanguageStatusText.Text = $"AI è‡ªåŠ¨åˆ¤æ–­ï¼š{side} Â· {route.DisplayName} Â· {(recognition.IsFinal ? "ç¨³å®š" : "æ­£åœ¨è¯´")}";
                SetCurrentSource(isLeft, recognizedText);
                ShowActivePane(isLeft);
                var translator = route.TargetLanguage == "Chinese" ? incomingTranslator : outgoingTranslator;
                if (recognition.IsFinal)
                {
                    lock (_processedFinalItems)
                        if (!_processedFinalItems.Add($"auto:{recognition.SegmentId}")) continue;
                    try
                    {
                        // Do not learn an opponent's foreign voice as the user's voice. A polluted
                        // profile was able to send later foreign speech into the left-hand pane.
                        if (route.SourceLanguage == "Chinese"
                            && BidirectionalTranslationRouter.HasStrongEvidence(route, recognition.Language, recognizedText))
                        {
                            _voiceProfile.Observe(route, fingerprint);
                            SaveVoiceProfile();
                        }
                        var translated = await TranslateTrackedAsync(translator, recognizedText, true, cancellationToken);
                        AppendConfirmed(isLeft, recognizedText, RecognitionTextNormalizer.Sanitize(translated), route, fingerprint);
                        ClearCurrent(isLeft);
                        await SaveRecordAsync(selected);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        SaveStatusText.Text = $"ç¿»è¯‘æš‚ä¸å¯ç”¨ï¼š{exception.Message}";
                        ClearCurrent(isLeft);
                    }
                }
                else
                {
                    _ = TranslateInterimAsync(translator, recognizedText, isLeft, interim.Token);
                }
            }
        }
        finally
        {
            interim?.Cancel();
            interim?.Dispose();
        }
    }

    private TranslationDirection? ResolveAutomaticRoute(
        TranslationDirection selected,
        string? recognizedLanguage,
        string text,
        VoiceFingerprint fingerprint)
    {
        if (BidirectionalTranslationRouter.IsShortBackchannel(text)) return null;
        var textRoute = BidirectionalTranslationRouter.Resolve(selected, recognizedLanguage, text);
        // Clear text/ASR evidence always wins. The local profile is only a fallback for
        // unclassifiable fragments; it cannot override a confirmed foreign-language segment.
        if (textRoute is not null && BidirectionalTranslationRouter.HasStrongEvidence(textRoute, recognizedLanguage, text))
            return textRoute;
        if (textRoute is not null) return textRoute;
        return _voiceProfile.ResolveSelf(selected, fingerprint);
    }

    private async Task RunExplicitRecognitionAsync(
        QwenRealtimeAsrClient recognizer,
        IAsyncEnumerable<AudioFrame> audioFrames,
        TranslationDirection selected,
        TranslationDirection route,
        QwenMtTranslator incomingTranslator,
        QwenMtTranslator outgoingTranslator,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? interim = null;
        try
        {
            await foreach (var recognition in WithRecognitionWatchdog(
                               recognizer.RecognizeAsync(audioFrames, cancellationToken), cancellationToken))
            {
                var recognizedText = RecognitionTextNormalizer.Sanitize(recognition.Text);
                if (string.IsNullOrWhiteSpace(recognizedText)) continue;
                var fingerprint = VoiceFingerprintAt(recognition.AudioPosition);
                var voiceHint = _voiceProfile.Resolve(selected, fingerprint);
                var hintText = voiceH×N´¶‰ËkºwµçZ[Û‘\™Xİ[Û‹’˜\[™\ÙPÚ[™\ÙPšY\™Xİ[Û˜[ˆÈ˜[œÛ][Û‘\™Xİ[Û‹Ú[™\ÙUÒ˜\[™\ÙHˆ˜[œÛ][Û‘\™Xİ[Û‹Ú[™\ÙUÑ[™Û\Úˆ›İ][™Ó[ÙK‘›Ü™ZYÛ”ÜXZÙ\ˆOˆÙ[XİYOH˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙPÚ[™\ÙPšY\™Xİ[Û˜[ˆÈ˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙUĞÚ[™\ÙHˆ˜[œÛ][Û‘\™Xİ[Û‹‘[™Û\ÚĞÚ[™\ÙKˆÈOˆ[ˆNÂ‚ˆš]˜]H›ÚYØY›ÚXÙT›Ùš[J
BˆÂˆBˆÂˆYˆ
Qš[K‘^\İÊİ›ÚXÙT›Ùš[T]
JH™]\›Âˆİ›ÚXÙT›Ùš[K”™\İÜ™JœÛÛ”Ù\šX[^™\‹‘\Ù\šX[^™OXİ[Û˜\Oİš[™Ë›ÚXÙT›Ùš[TÛ˜\ÚİŠš[K”™XY[^
İ›ÚXÙT›Ùš[T]
JJNÂˆBˆØ]Ú
^Ù\[Ûˆ^Ù\[ÛŠHÚ[ˆ
^Ù\[Ûˆ\ÈSÑ^Ù\[ÛˆÜˆ[˜]]Üš^™YXØÙ\ÜÑ^Ù\[ÛˆÜˆœÛÛ‘^Ù\[ÛŠBˆÂˆØ]™Tİ]\Õ^•^H	ºgìú"l¹å.ù`ãù¦ ¹§*º/oyai{ï&Ù^Ù\[Û‹“Y\ÜØYÙ_HÂˆBˆB‚ˆš]˜]H›ÚYØ]™U›ÚXÙT›Ùš[J
BˆÂˆBˆÂˆ\™XİÜKÜ™X]Q\™XİÜJÜ™XÛÜ™\™XİÜJNÂˆš[K•Üš]P[^
İ›ÚXÙT›Ùš[T]œÛÛ”Ù\šX[^™\‹”Ù\šX[^™Jİ›ÚXÙT›Ùš[K”Û˜\Úİ

K™]ÈœÛÛ”Ù\šX[^™\“Ü[ÛœÈÈÜš]R[™[YHYHJK[˜ÛÙ[™Ë•U
NÂˆBˆØ]Ú
^Ù\[Ûˆ^Ù\[ÛŠHÚ[ˆ
^Ù\[Ûˆ\ÈSÑ^Ù\[ÛˆÜˆ[˜]]Üš^™YXØÙ\ÜÑ^Ù\[ÛŠBˆÂˆØ]™Tİ]\Õ^•^H	ºgìú"l¹å.ù`ãù¦ ¹§*¹/çykf;ï&Ù^Ù\[Û‹“Y\ÜØYÙ_HÂˆBˆB‚ˆš]˜]H›ÚYÙ]›İ][™Ó[ÙJ›İ][™Ó[ÙH[ÙJBˆÂˆ˜\ˆÚ[™ÙYHÜ›İ][™Ó[ÙHOH[ÙNÂˆÜ›İ][™Ó[ÙHH[ÙNÂˆÚ[™\ÙT›İ][™Ğ]Û‹”İ[HH
İ[JQš[™™\Ûİ\˜ÙJ[ÙHOH›İ][™Ó[ÙKÚ[™\ÙTÜXZÙ\ˆÈ”[]Û”İ[Hˆˆ”ÙXÛÛ™\T[]Û”İ[HŠNÂˆ›Ü™ZYÛ”›İ][™Ğ]Û‹”İ[HH
İ[JQš[™™\Ûİ\˜ÙJ[ÙHOH›İ][™Ó[ÙK‘›Ü™ZYÛ”ÜXZÙ\ˆÈ”[]Û”İ[Hˆˆ”ÙXÛÛ™\T[]Û”İ[HŠNÂˆ]]Ô›İ][™Ğ]Û‹”İ[HH
İ[JQš[™™\Ûİ\˜ÙJ[ÙHOH›İ][™Ó[ÙK]]ÛX]XÈÈ”[]Û”İ[Hˆˆ”ÙXÛÛ™\T[]Û”İ[HŠNÂˆ˜\ˆX\›™YHİ›ÚXÙT›Ùš[K”Û˜\Úİ

K•˜[Y\Ë”İ[J][HOˆ][KÛİ[
NÂˆ[™İXYÙTİ]\Õ^•^H[ÙHİÚ]ÚˆÂˆ›İ][™Ó[ÙKÚ[™\ÙTÜXZÙ\ˆOˆ	¹.+y¥¡ÈTÔˆ9mìºe yk¦»ï&ù¢$y¥®ycäz* :/æùaiymé¹/©È0­È:gìú"l¹å.ù`ãÈÛX\›™YH9«­H‹ˆ›İ][™Ó[ÙK‘›Ü™ZYÛ”ÜXZÙ\ˆOˆ	¹i%º+ëHTÔˆ9mìºe yk¦»ï&ùkîy¥®yi&¹.®¹cäz* :/æùaiycìù/©È0­È:gìú"l¹å.ù`ãÈÛX\›™YH9«­H‹ˆÈOˆ	RH:!ê¹bª9b)9¥«ymì¹o 9d+ûï&º+á¹b*ú+ëz* 
È9¥¡ùkeÈ
È9¢$yæ¡:gìú"lˆ0­È9mì¹ki¹.hÛX\›™YH9«­H‚ˆNÂˆYˆ
Ú[™ÙY
BˆÂˆÜ›İ]Tİ\Y]HÛ]\İ]Y[ÔÜÚ][ÛÂˆØÚÈ
İ›ÚXÙQØ]JHÜ™XÙ[›ÚXÙKÛX\Š
NÂˆÜ™XÛÙÛš][Û”İÚ]ÚËØ[˜Ù[

NÂˆBˆB‚ˆš]˜]H›ÚYÚ[™\ÙT›İ][™Ğ]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJHOˆÙ]›İ][™Ó[ÙJ›İ][™Ó[ÙKÚ[™\ÙTÜXZÙ\ŠNÂˆš]˜]H›ÚY›Ü™ZYÛ”›İ][™Ğ]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJHOˆÙ]›İ][™Ó[ÙJ›İ][™Ó[ÙK‘›Ü™ZYÛ”ÜXZÙ\ŠNÂˆš]˜]H›ÚY]]Ô›İ][™Ğ]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJHOˆÙ]›İ][™Ó[ÙJ›İ][™Ó[ÙK]]ÛX]XÊNÂˆš]˜]H›ÚYÛÕ[Ğ]Û—ÑİÛŠØš™XİÙ[™\‹[İ\ÙP]Û‘]™[\™ÜÈJBˆÂˆK’[™YHYNÂˆÜ›İ][™Ó[ÙP™Y›Ü™RÛHÜ›İ][™Ó[ÙNÂˆÛÕ[Ğ]Û‹Ø\\™S[İ\ÙJ
NÂˆÙ]›İ][™Ó[ÙJ›İ][™Ó[ÙKÚ[™\ÙTÜXZÙ\ŠNÂˆBˆš]˜]H›ÚYÛÕ[Ğ]Û—Õ\
Øš™XİÙ[™\‹[İ\ÙP]Û‘]™[\™ÜÈJBˆÂˆK’[™YHYNÂˆÛÕ[Ğ]Û‹”™[X\ÙS[İ\ÙPØ\\™J
NÂˆÙ]›İ][™Ó[ÙJÜ›İ][™Ó[ÙP™Y›Ü™RÛ
NÂˆBˆš]˜]H›ÚYÛÕ[Ğ]Û—ÓÜİØ\\™JØš™XİÙ[™\‹[İ\ÙQ]™[\™ÜÈJBˆÂˆYˆ
[İ\ÙK“Y]ÛˆOH[İ\ÙP]Û”İ]K”™\ÜÙY
HÙ]›İ][™Ó[ÙJÜ›İ][™Ó[ÙP™Y›Ü™RÛ
NÂˆB‚ˆš]˜]H\Ş[˜È\ÚÈ˜[œÛ]R[\š[P\Ş[˜Ê]Ù[“]˜[œÛ]Üˆ˜[œÛ]Ü‹İš[™ÈÛİ\˜ÙK›ÛÛ\ÓYØ[˜Ù[][Û•ÚÙ[ˆØ[˜Ù[][Û•ÚÙ[ŠBˆÂˆBˆÂˆ]ØZ]\ÚË‘[^JMLØ[˜Ù[][Û•ÚÙ[ŠNÂˆ˜\ˆ˜[œÛ]YH]ØZ]˜[œÛ]U˜XÚÙY\Ş[˜Ê˜[œÛ]Ü‹Ûİ\˜ÙK˜[ÙKØ[˜Ù[][Û•ÚÙ[ŠNÂˆ]ØZ]\Ü]Ú\‹’[›ÚÙP\Ş[˜Ê

HOˆÙ]İ\œ™[˜[œÛ][ÛŠ\ÓY™XÛÙÛš][Û•^›Ü›X[^™\‹”Ø[š]^™J˜[œÛ]Y
JJNÂˆBˆØ]Ú
Ü\˜][ÛØ[˜Ù[Y^Ù\[ÛŠHÈBˆØ]Ú
^Ù\[Ûˆ^Ù\[ÛŠBˆÂˆ]ØZ]\Ü]Ú\‹’[›ÚÙP\Ş[˜Ê

HO‚ˆÂˆØ]™Tİ]\Õ^•^H	¹ïîú+äy¦ ¹.#ycëùå*;ï&Ù^Ù\[Û‹“Y\ÜØYÙ_HÂˆÛX\İ\œ™[
\ÓY
NÂˆJNÂˆBˆB‚ˆš]˜]Hİ]XÈ\Ş[˜ÈP\Ş[˜Ñ[[Y\˜X›O™XÛÙÛš][Û‘]™[ˆÚ]™XÛÙÛš][Û•Ø]ÚÙÊˆP\Ş[˜Ñ[[Y\˜X›O™XÛÙÛš][Û‘]™[ˆÛİ\˜ÙKˆÑ[[Y\˜]ÜØ[˜Ù[][Û—HØ[˜Ù[][Û•ÚÙ[ˆØ[˜Ù[][Û•ÚÙ[ŠBˆÂˆ]ØZ]\Ú[™È˜\ˆ[[Y\˜]ÜˆHÛİ\˜ÙK‘Ù]\Ş[˜Ñ[[Y\˜]ÜŠØ[˜Ù[][Û•ÚÙ[ŠNÂˆÚ[H
YJBˆÂˆ›ÛÛ\Ó™^ÂˆBˆÂˆ\Ó™^H]ØZ][[Y\˜]Ü‹“[İ™S™^\Ş[˜Ê
K\Õ\ÚÊ
Bˆ•ØZ]\Ş[˜Ê™XÛÙÛš][Û•Ø]ÚÙËØ[˜Ù[][Û•ÚÙ[ŠNÂˆBˆØ]Ú
[Y[İ]^Ù\[Ûˆ^Ù\[ÛŠBˆÂˆ›İÈ™]È[Y[İ]^Ù\[ÛŠŒN9éä¹§*¹¥-¹b,9¥¬9æ¡:+á¹b*ù.¢ù.íˆ‹^Ù\[ÛŠNÂˆB‚ˆYˆ
Z\Ó™^
HZY[œ™XZÎÂˆZY[™]\›ˆ[[Y\˜]Ü‹İ\œ™[ÂˆBˆB‚ˆš]˜]H\Ş[˜È˜[YU\ÚÏİš[™Ïˆ˜[œÛ]U˜XÚÙY\Ş[˜Êˆ]Ù[“]˜[œÛ]Üˆ˜[œÛ]Ü‹İš[™ÈÛİ\˜ÙK›ÛÛ\Ñš[˜[Ø[˜Ù[][Û•ÚÙ[ˆØ[˜Ù[][Û•ÚÙ[ŠBˆÂˆBˆÂˆ˜\ˆ˜[œÛ]YH]ØZ]˜[œÛ]Ü‹•˜[œÛ]P\Ş[˜ÊÛİ\˜ÙK\Ñš[˜[Ø[˜Ù[][Û•ÚÙ[ŠNÂˆYˆ
Ü™XÛÜ™\ØYÙH\È›İ[
Bˆ]ØZ]Ü™XÛÜ™\ØYÙJ™]ÈZU\ØYÙT™XÛÜ™
ˆ]SÛ›K‘œ›ÛQ]U[YJ]U[YK“›İÊKZU\ØYÙRÚ[™•˜[œÛ][Û‹]Ù[“]›İØÛÛ“[Ù[ˆKÛİ\˜ÙK“[™İ˜[œÛ]Y“[™İˆZU\ØYÙT™XÛÜ™‘\İ[X]UÚÙ[œÊÛİ\˜ÙJKZU\ØYÙT™XÛÜ™‘\İ[X]UÚÙ[œÊ˜[œÛ]Y
K
JNÂˆ™]\›ˆ˜[œÛ]YÂˆBˆØ]Ú
Ü\˜][ÛØ[˜Ù[Y^Ù\[ÛŠHÈ›İÎÈBˆØ]ÚˆÂˆYˆ
Ü™XÛÜ™\ØYÙH\È›İ[
Bˆ]ØZ]Ü™XÛÜ™\ØYÙJ™]ÈZU\ØYÙT™XÛÜ™
ˆ]SÛ›K‘œ›ÛQ]U[YJ]U[YK“›İÊKZU\ØYÙRÚ[™•˜[œÛ][Û‹]Ù[“]›İØÛÛ“[Ù[ˆKKÛİ\˜ÙK“[™İZU\ØYÙT™XÛÜ™‘\İ[X]UÚÙ[œÊÛİ\˜ÙJK
JNÂˆ›İÎÂˆBˆB‚ˆš]˜]H›ÚY\[™ÛÛ™š\›YY
›ÛÛ\ÓYİš[™ÈÛİ\˜ÙKİš[™È˜[œÛ][Û‹˜[œÛ][Û‘\™Xİ[Ûˆ\™Xİ[Û‹›ÚXÙQš[™Ù\œš[š[™Ù\œš[
BˆÂˆ˜\ˆ\›ˆH™]ÈØ]™Y\›Š]U[YSÙ™œÙ]“›İËÛİ\˜ÙK˜[œÛ][Û‹\™Xİ[Û‹š[™Ù\œš[
NÂˆ˜\ˆ\›œÈH\ÓYÈÛY\›œÈˆÜšYÚ\›œÎÂˆ˜\ˆ™]š[İ\ÈHÛY\›œËÛÛ˜Ø]
ÜšYÚ\›œÊK“Ü™\Q\ØÙ[™[™Ê][HOˆ][K]
K‘š\œİÜ‘Y˜][

NÂˆYˆ
™]š[İ\È\È›İ[ˆ	‰ˆ\›‹]H™]š[İ\Ë][YTÜ[‹‘œ›ÛTÙXÛÛ™ÊLŠBˆ	‰ˆ
İš[™Ë‘\]X[Ê™]š[İ\Ë”Ûİ\˜ÙK\›‹”Ûİ\˜ÙKİš[™ĞÛÛ\\š\ÛÛ‹“Ü™[˜[YÛ›Ü™PØ\ÙJBˆİš[™Ë‘\]X[Ê™]š[İ\Ë•˜[œÛ][Û‹\›‹•˜[œÛ][Û‹İš[™ĞÛÛ\\š\ÛÛ‹“Ü™[˜[YÛ›Ü™PØ\ÙJJJBˆ™]\›Âˆ\›œËY
\›ŠNÂˆ˜\ˆ^H™[™\”[™J\›œÊNÂˆYˆ
\ÓY
BˆÂˆ\]R\İÜT[™JY\İÜP›Ş^
NÂˆBˆ[ÙBˆÂˆ\]R\İÜT[™JšYÚ\İÜP›Ş^
NÂˆBˆB‚ˆš]˜]H\Ş[˜È\ÚÈØ]™T™XÛÜ™\Ş[˜Ê˜[œÛ][Û‘\™Xİ[ÛˆÙ[XİY
BˆÂˆ\™XİÜKÜ™X]Q\™XİÜJÜ™XÛÜ™\™XİÜJNÂˆÜ™XÛÜ™]ÏÏH]ÛÛXš[™JÜ™XÛÜ™\™XİÜK	¹cã9d$yd#9/(^×Üİ\Y]^^^SSYR[\ÜßK›YŠNÂˆ˜\ˆZ[\ˆH™]Èİš[™ĞZ[\Š
NÂˆZ[\‹\[™[™JˆÈ9cã9d$yd#9/(:+¬9oeHŠK\[™[™J
Bˆ\[™[™J	‹H9o 9iâûï&×Üİ\Y]^^^KSSKY›[NœÜßHŠBˆ\[™[™J	‹H9ª(yo#ûï&ÜÙ[XİY‘\Ü^S˜[Y_HŠK\[™[™J
Bˆ\[™[™JˆÈÈ9.+y¥¡È8¡¤ˆ9i%º+ëHŠK\[™[™J
NÂˆ\[™X\šÙİÛ•\›œÊZ[\‹ÛY\›œÊNÂˆZ[\‹\[™[™JˆÈÈ9i%º+ëH8¡¤ˆ9.+y¥¡ÈŠK\[™[™J
NÂˆ\[™X\šÙİÛ•\›œÊZ[\‹ÜšYÚ\›œÊNÂˆ]ØZ]š[K•Üš]P[^\Ş[˜ÊÜ™XÛÜ™]Z[\‹•Ôİš[™Ê
K[˜ÛÙ[™Ë•U
NÂˆ™XÛÜ™]^•^HÜ™XÛÜ™]Âˆ™XÛÜ™]^•ÛÛ\HÜ™XÛÜ™]ÂˆÜ[”™XÛÜ™]Û‹’\Ñ[˜X›YHYNÂˆB‚ˆš]˜]Hİš[™È™[™\”[™JQ[[Y\˜X›OØ]™Y\›ˆ\›œÊHOˆİš[™Ë’›Ú[Š————ˆ‹\›œË”Ù[Xİ
\›ˆO‚ˆÜÚİÓÜšYÚ[˜[ˆÈ	–Şİ\›‹]’›[NœÜßWHİ\›‹•˜[œÛ][ÛŸW—¹c§ù¥¡ûï&İ\›‹”Ûİ\˜Ù_H‚ˆˆ	–Şİ\›‹]’›[NœÜßWHİ\›‹•˜[œÛ][ÛŸHŠJNÂ‚ˆš]˜]H›ÚY\]R\İÜT[™JŞ\İ[K•Ú[™İÜËÛÛ›ÛË•^›Ş^›Şİš[™È^
BˆÂˆ˜\ˆšY]Ù\ˆHš[™ØÜ›ÛšY]Ù\Š^›Ş
NÂˆ˜\ˆ›ÛİÈHšY]Ù\ˆ\È[šY]Ù\‹”ØÜ›ÛX›RZYÚHHšY]Ù\‹•™\XØ[Ù™œÙ]HšY]Ù\‹”ØÜ›ÛX›RZYÚHŒÂˆ˜\ˆÙ™œÙ]HšY]Ù\Ë•™\XØ[Ù™œÙ]ÏÈÂˆ^›Ş•^H^Âˆ\Ü]Ú\‹™YÚ[’[›ÚÙJ

HO‚ˆÂˆ˜\ˆ\]YšY]Ù\ˆHš[™ØÜ›ÛšY]Ù\Š^›Ş
NÂˆYˆ
›ÛİÊH^›Ş”ØÜ›ÛÑ[™

NÂˆ[ÙBˆÂˆ\]YšY]Ù\Ë”ØÜ›ÛÕ™\XØ[Ù™œÙ]
X]“Z[ŠÙ™œÙ]\]YšY]Ù\‹”ØÜ›ÛX›RZYÚ
JNÂˆ]ZXÚÓ™]ÔİX]P]Û‹•š\ÚXš[]HHš\ÚXš[]K•š\ÚX›NÂˆBˆJNÂˆB‚ˆš]˜]Hİ]XÈØÜ›ÛšY]Ù\Èš[™ØÜ›ÛšY]Ù\Š\[™[˜ŞSØš™Xİ\™[
BˆÂˆ›Üˆ
˜\ˆ[™^HÈ[™^š\İX[™YR[\‹‘Ù]Ú[™[Ûİ[
\™[
NÈ[™^
ÊÊBˆÂˆ˜\ˆÚ[Hš\İX[™YR[\‹‘Ù]Ú[
\™[[™^
NÂˆYˆ
Ú[\ÈØÜ›ÛšY]Ù\ˆšY]Ù\ŠH™]\›ˆšY]Ù\Âˆ˜\ˆ™\İYHš[™ØÜ›ÛšY]Ù\ŠÚ[
NÂˆYˆ
™\İY\È›İ[
H™]\›ˆ™\İYÂˆBˆ™]\›ˆ[ÂˆB‚ˆš]˜]H›ÚY]ZXÚÓ™]ÔİX]P]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJBˆÂˆY\İÜP›Ş”ØÜ›ÛÑ[™

NÂˆšYÚ\İÜP›Ş”ØÜ›ÛÑ[™

NÂˆ]ZXÚÓ™]ÔİX]P]Û‹•š\ÚXš[]HHš\ÚXš[]KÛÛ\ÙYÂˆB‚ˆš]˜]Hİ]XÈ›ÚY\[™X\šÙİÛ•\›œÊİš[™ĞZ[\ˆZ[\‹Q[[Y\˜X›OØ]™Y\›ˆ\›œÊBˆÂˆ›Ü™XXÚ
˜\ˆ\›ˆ[ˆ\›œÊBˆZ[\‹\[™[™J	ˆÈÈÈİ\›‹]’›[NœÜßHŠK\[™[™J
Bˆ\[™[™J	‹H9c§ú+ç{ï&İ\›‹”Ûİ\˜Ù_HŠBˆ\[™[™J	‹H9ïîú+ä{ï&İ\›‹•˜[œÛ][ÛŸHŠK\[™[™J
NÂˆB‚ˆš]˜]H›ÚYÙ]İ\œ™[Ûİ\˜ÙJ›ÛÛ\ÓYİš[™È^
BˆÂˆYˆ
\ÓY
HYİ\œ™[Ûİ\˜ÙU^•^H^Âˆ[ÙHšYÚİ\œ™[Ûİ\˜ÙU^•^H^ÂˆB‚ˆš]˜]H›ÚYÙ]İ\œ™[˜[œÛ][ÛŠ›ÛÛ\ÓYİš[™È^
BˆÂˆYˆ
\ÓY
HYİ\œ™[˜[œÛ][Û•^•^H^Âˆ[ÙHšYÚİ\œ™[˜[œÛ][Û•^•^H^ÂˆB‚ˆš]˜]H›ÚYÚİĞXİ]™T[™J›ÛÛ\ÓY
BˆÂˆYˆ
\ÓY
BˆÂˆYˆ
İš[™Ë’\Ó[Ü•Ú]TÜXÙJYİ\œ™[˜[œÛ][Û•^•^
HYİ\œ™[˜[œÛ][Û•^•^”İ\ÕÚ]
¹ëbyo¡H‹İš[™ĞÛÛ\\š\ÛÛ‹“Ü™[˜[
JBˆYİ\œ™[˜[œÛ][Û•^•^H¹«hùg*9ïîú+äy..¹kîy¥®z+ëz* 8 )¸ )ˆÂˆšYÚİ\œ™[˜[œÛ][Û•^•^H¹ëbyo¡ykîy¥®ycäz* 8 )¸ )ˆÂˆBˆ[ÙBˆÂˆYˆ
İš[™Ë’\Ó[Ü•Ú]TÜXÙJšYÚİ\œ™[˜[œÛ][Û•^•^
HšYÚİ\œ™[˜[œÛ][Û•^•^”İ\ÕÚ]
¹ëbyo¡H‹İš[™ĞÛÛ\\š\ÛÛ‹“Ü™[˜[
JBˆšYÚİ\œ™[˜[œÛ][Û•^•^H¹«hùg*9ïîú+äy..¹.+y¥¡ø )¸ )ˆÂˆYİ\œ™[˜[œÛ][Û•^•^H¹ëbyo¡y¢$y¥®ycäz* 8 )¸ )ˆÂˆBˆB‚ˆš]˜]H›ÚYÛX\İ\œ™[
›ÛÛ\ÓY
BˆÂˆYˆ
\ÓY
HYİ\œ™[˜[œÛ][Û•^•^H¹ëbyo¡y¢$y¥®ycäz* 8 )¸ )ˆÂˆ[ÙHšYÚİ\œ™[˜[œÛ][Û•^•^H¹ëbyo¡ykîy¥®ycäz* 8 )¸ )ˆÂˆB‚ˆš]˜]H›ÚY[™İXYÙTZ\›ŞÔÙ[Xİ[ÛÚ[™ÙY
Øš™XİÙ[™\‹Ş\İ[K•Ú[™İÜËÛÛ›ÛË”Ù[Xİ[ÛÚ[™ÙY]™[\™ÜÈJBˆÂˆYˆ
Y]U^\È[
H™]\›Âˆ˜\ˆ›Ü™ZYÛˆHÙ[XİYZ\ˆOH˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙPÚ[™\ÙPšY\™Xİ[Û˜[È¹¥éy¥¡Èˆˆº"ìy¥¡ÈÂˆY]U^•^H	¹¢$z+í9.+y¥¡È8¡¤ˆ9îæykîy¥®^Ù›Ü™ZYÛŸHÂˆšYÚ]U^•^H	¹kîy¥®z+íÙ›Ü™ZYÛŸH8¡¤ˆ9îæy¢$y.+y¥¡ÈÂˆYİ\œ™[˜[œÛ][Û•^•^H	¹ëbyo¡y.+y¥¡ùnm¹ïîú+äy..Ù›Ü™ZYÛŸx )¸ )ˆÂˆšYÚİ\œ™[˜[œÛ][Û•^•^H	¹ëbyo¡^Ù›Ü™ZYÛŸynm¹ïîú+äy..¹.+y¥¡ø )¸ )ˆÂˆB‚ˆš]˜]H›ÚYÜ[”™XÛÜ™]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJBˆÂˆ˜\ˆ]HÜ™XÛÜ™]ÂˆYˆ
]\È›İ[	‰ˆš[K‘^\İÊ]
JBˆ›ØÙ\ÜË”İ\
™]È›ØÙ\ÜÔİ\[™›Ê]
HÈ\ÙTÚ[^Xİ]HHYHJNÂˆB‚ˆš]˜]H\Ş[˜È›ÚYÛÜœ™Xİ\İ]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJBˆÂˆ˜\ˆ\İHÛY\›œËÛÛ˜Ø]
ÜšYÚ\›œÊK“Ü™\Q\ØÙ[™[™Ê][HOˆ][K]
K‘š\œİÜ‘Y˜][

NÂˆYˆ
\İ\È[
BˆÂˆØ]™Tİ]\Õ^•^Hº/æ9¬¨y§"ycëù.éyî¨9«hùæ¡9èkº+©9keùneHÂˆ™]\›ÂˆBˆ˜\ˆÙ[XİYHÙ[XİYZ\Âˆ˜\ˆÛÜœ™XİYH\İ‘\™Xİ[Û‹”Ûİ\˜ÙS[™İXYÙHOHÚ[™\ÙH‚ˆÈ
Ù[XİYOH˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙPÚ[™\ÙPšY\™Xİ[Û˜[È˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙUĞÚ[™\ÙHˆ˜[œÛ][Û‘\™Xİ[Û‹‘[™Û\ÚĞÚ[™\ÙJBˆˆ
Ù[XİYOH˜[œÛ][Û‘\™Xİ[Û‹’˜\[™\ÙPÚ[™\ÙPšY\™Xİ[Û˜[È˜[œÛ][Û‘\™Xİ[Û‹Ú[™\ÙUÒ˜\[™\ÙHˆ˜[œÛ][Û‘\™Xİ[Û‹Ú[™\ÙUÑ[™Û\Ú
NÂˆÛÜœ™Xİ\İ]Û‹’\Ñ[˜X›YH˜[ÙNÂˆØ]™Tİ]\Õ^•^H¹«hùg*9£"y«hùèkº+í:+çy.®ºaãy¥¬9ïîú+äy."¹. 9céx )¸ )ˆÂˆBˆÂˆ\Ú[™È˜\ˆ˜[œÛ]ÜˆH™]È]Ù[“]˜[œÛ]ÜŠ]Ù[‘[™Ú[”Ú[™Ø\Ü™U˜[œÛ][ÛŠİÛÜšÜÜXÙRY
KØ\RÙ^KÛÜœ™XİY
NÂˆ˜\ˆ˜[œÛ]YH]ØZ]˜[œÛ]U˜XÚÙY\Ş[˜Ê˜[œÛ]Ü‹\İ”Ûİ\˜ÙKYKØ[˜Ù[][Û•ÚÙ[‹“›Û™JNÂˆÛY\›œË”™[[İ™J\İ
NÂˆÜšYÚ\›œË”™[[İ™J\İ
NÂˆYˆ
ÛÜœ™XİY”Ûİ\˜ÙS[™İXYÙHOHÚ[™\ÙHŠBˆÂˆ›Üˆ
˜\ˆ[™^HÈ[™^ÎÈ[™^
ÊÊHİ›ÚXÙT›Ùš[K“ØœÙ\™JÛÜœ™XİY\İ‘š[™Ù\œš[
NÂˆØ]™U›ÚXÙT›Ùš[J
NÂˆBˆ\[™ÛÛ™š\›YY
ÛÜœ™XİY”Ûİ\˜ÙS[™İXYÙHOHÚ[™\ÙH‹\İ”Ûİ\˜ÙK™XÛÙÛš][Û•^›Ü›X[^™\‹”Ø[š]^™J˜[œÛ]Y
KÛÜœ™XİY\İ‘š[™Ù\œš[
NÂˆ\]R\İÜT[™JY\İÜP›Ş™[™\”[™JÛY\›œÊJNÂˆ\]R\İÜT[™JšYÚ\İÜP›Ş™[™\”[™JÜšYÚ\›œÊJNÂˆ]ØZ]Ø]™T™XÛÜ™\Ş[˜ÊÙ[XİY
NÂˆØ]™Tİ]\Õ^•^H¹mìºaãy¥¬9b!¹­`{ï#9nm¹å*9.£¹§+9g,:gìú"lº+ëz* 9å.ù`ãùki¹.hÂˆBˆØ]Ú
^Ù\[Ûˆ^Ù\[ÛŠBˆÂˆØ]™Tİ]\Õ^•^H	¹î¨9«hùi,z-){ï#9c§ú+¬9oey§*¹¥.ybª;ï&Ù^Ù\[Û‹“Y\ÜØYÙ_HÂˆBˆš[˜[HÈÛÜœ™Xİ\İ]Û‹’\Ñ[˜X›YHYNÈBˆB‚ˆš]˜]H›ÚYÛÜÙP]Û—ĞÛXÚÊØš™XİÙ[™\‹›İ]Y]™[\™ÜÈJHOˆÛÜÙJ
NÂ‚ˆš]˜]HÙX[Y™XÛÜ™Ø]™Y\›Š]U[YSÙ™œÙ]]İš[™ÈÛİ\˜ÙKİš[™È˜[œÛ][Û‹˜[œÛ][Û‘\™Xİ[Ûˆ\™Xİ[Û‹›ÚXÙQš[™Ù\œš[š[™Ù\œš[
NÂˆš]˜]HÙX[Y™XÛÜ™[YY›ÚXÙQš[™Ù\œš[
[YTÜ[ˆ]›ÚXÙQš[™Ù\œš[˜[YJNÂˆš]˜]H[[H›İ][™Ó[ÙHÈ]]ÛX]XËÚ[™\ÙTÜXZÙ\‹›Ü™ZYÛ”ÜXZÙ\ˆBŸB