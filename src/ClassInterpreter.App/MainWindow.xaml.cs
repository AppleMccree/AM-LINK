using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ClassInterpreter.Core.Configuration;
using ClassInterpreter.Core.Learning;
using ClassInterpreter.Core.Sessions;
using ClassInterpreter.Infrastructure.Secrets;
using ClassInterpreter.Infrastructure.Audio;
using ClassInterpreter.Infrastructure.Configuration;
using ClassInterpreter.Infrastructure.Qwen;
using ClassInterpreter.Infrastructure.Timeline;
using ClassInterpreter.Core.Slides;
using ClassInterpreter.Infrastructure.Slides;
using Microsoft.Win32;
using ClassInterpreter.Infrastructure.StudyPacks;
using ClassInterpreter.Infrastructure.Learning;
using System.Net.WebSockets;
using System.Net.Http;
using System.Windows.Media.Imaging;
using ClassInterpreter.Core.Demo;
using ClassInterpreter.Core.Speech;
using System.Windows.Input;
using System.Diagnostics;
using ClassInterpreter.Core.Classrooms;
using ClassInterpreter.Infrastructure.Classrooms;

namespace ClassInterpreter.App;

public partial class MainWindow : Window
{
    private readonly ISecretStore _secretStore = new WindowsCredentialSecretStore();
    private readonly AppPaths _paths = AppPaths.Create(AppRootResolver.ResolveDefault());
    private readonly AppSettingsFileStore _settingsStore;
    private CancellationTokenSource? _microphoneTestCancellation;
    private CancellationTokenSource? _classCancellation;
    private SlideDocument? _slideDocument;
    private int _currentSlidePage = 1;
    private readonly SlideMatcher _slideMatcher = new();
    private readonly SlideFollowController _slideFollow = new();
    private readonly List<string> _stableSlideTranscriptWindow = [];
    private SlideCandidate? _pendingSlideCandidate;
    private Guid? _pendingSlideSegmentId;
    private bool _followLiveSubtitles = true;
    private long _subtitleRenderVersion;
    private int _studyPackGenerationRunning;
    private readonly TaskCompletionSource _initializationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SqliteTimelineRepository _repository;
    private Course? _selectedCourse;
    private LessonRecord? _selectedHistoryLesson;
    private string? _materialPath;
    private Session? _activeSession;
    private string? _activeLessonDirectory;
    private int _historyLoadVersion;
    private IReadOnlyList<TranscriptSegment> _historyTranscripts = [];
    private IReadOnlyList<AiQuestionRecord> _historyQuestions = [];
    private string? _historyStudyPackPath;
    private IReadOnlyList<LessonRecordingItem> _historyRecordings = [];
    private AiTutorWindow? _aiTutorWindow;
    private QuickTranslatorWindow? _quickTranslatorWindow;
    private string? _resumeLessonKey;
    private int? _resumeLessonNumber;
    private string? _resumeLessonDirectory;
    private IReadOnlyList<string> _resumeConfirmedTranslations = [];
    private int? _resumeSlidePage;
    private bool _loadingNavigation;
    private readonly List<string> _confirmedTranslations = [];
    private readonly IClassroomSyncService _classroomSync;
    private ClassroomWindow? _classroomWindow;
    private AiUsageWindow? _aiUsageWindow;
    private string _classroomServerUrl = "https://classroom.am-link.app";

    public Task InitializationCompleted => _initializationCompleted.Task;

    public MainWindow()
    {
        _settingsStore = new AppSettingsFileStore(Path.Combine(_paths.Root, "data", "settings.json"));
        _repository = new SqliteTimelineRepository(Path.Combine(_paths.DatabaseDirectory, "timeline.db"));
        _classroomSync = new CloudClassroomSyncService(Path.Combine(_paths.Root, "data", "classroom-outbox.json"));
        InitializeComponent();
        SlideFollowModeBox.ItemsSource = new[]
        {
            new SlideFollowModeOption(SlideFollowMode.Manual, "手动浏览（默认）"),
            new SlideFollowModeOption(SlideFollowMode.Suggest, "智能提示"),
            new SlideFollowModeOption(SlideFollowMode.Automatic, "自动跟随（实验）")
        };
        SlideFollowModeBox.SelectedIndex = 0;
        DataRootText.Text = $"数据位置：{_paths.Root}";
        Loaded += MainWindow_Loaded;
        _classroomSync.ConnectionStatusChanged += (_, text) => Dispatcher.Invoke(() => ClassroomStatusText.Text = text);
        _classroomSync.BroadcastReceived += (_, item) => Dispatcher.Invoke(() => StatusText.Text = $"老师广播：{item.Message}");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = SettingsDrawer.Visibility != Visibility.Visible;
        SettingsDrawer.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Content = opening ? "关闭设置" : "设置";
    }

    private void AiUsageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiUsageWindow is { IsVisible: true })
        {
            _aiUsageWindow.Activate();
            return;
        }
        _aiUsageWindow = new AiUsageWindow(async () => await _repository.GetAiUsageAsync()) { Owner = this };
        _aiUsageWindow.Closed += (_, _) => _aiUsageWindow = null;
        _aiUsageWindow.Show();
    }

    private async void PreviousSlideButton_Click(object sender, RoutedEventArgs e) => await NavigateSlideAsync(-1);

    private async void NextSlideButton_Click(object sender, RoutedEventArgs e) => await NavigateSlideAsync(1);

    private async void JumpToPageButton_Click(object sender, RoutedEventArgs e) => await JumpToRequestedPageAsync();

    private async void JumpPageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await JumpToRequestedPageAsync();
        e.Handled = true;
    }

    private async Task JumpToRequestedPageAsync()
    {
        if (_slideDocument is null || !int.TryParse(JumpPageBox.Text.Trim(), out var pageNumber))
        {
            SlideImportStatusText.Text = "请输入有效页码";
            JumpPageBox.SelectAll();
            return;
        }

        if (pageNumber < 1 || pageNumber > _slideDocument.Pages.Count)
        {
            SlideImportStatusText.Text = $"页码范围：1－{_slideDocument.Pages.Count}";
            JumpPageBox.SelectAll();
            return;
        }

        await SelectSlidePageAsync(pageNumber, true, $"已快速跳到第 {pageNumber} / {_slideDocument.Pages.Count} 页");
        JumpPageBox.SelectAll();
    }

    private async Task NavigateSlideAsync(int delta)
    {
        if (_slideDocument is null || _slideDocument.Pages.Count == 0)
        {
            return;
        }

        var target = Math.Clamp(_currentSlidePage + delta, 1, _slideDocument.Pages.Count);
        await SelectSlidePageAsync(target, true, $"第 {target} / {_slideDocument.Pages.Count} 页");
    }

    private async Task SelectSlidePageAsync(int pageNumber, bool studentInitiated, string status)
    {
        if (_slideDocument is null) return;
        _currentSlidePage = Math.Clamp(pageNumber, 1, _slideDocument.Pages.Count);
        ShowSlide(_slideDocument.Pages.First(page => page.PageNumber == _currentSlidePage));
        await PersistCurrentSlidePageAsync();
        if (studentInitiated && _slideFollow.Mode != SlideFollowMode.Manual)
        {
            _slideFollow.PauseForManualNavigation();
            _pendingSlideCandidate = null;
            _pendingSlideSegmentId = null;
            ApplySlideSuggestionButton.IsEnabled = false;
            IgnoreSlideSuggestionButton.IsEnabled = false;
            ResumeSlideFollowButton.IsEnabled = true;
            SlideFollowStatusText.Text = "你正在手动浏览，跟随已暂停；可点击“恢复跟随”继续";
        }
        else
        {
            _slideFollow.RecordManualOrAcceptedNavigation(DateTimeOffset.Now);
            SlideImportStatusText.Text = status;
        }
    }

    private void SlideFollowModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlideFollowStatusText is null || SlideFollowModeBox.SelectedItem is not SlideFollowModeOption option) return;
        _slideFollow.SetMode(option.Mode);
        _pendingSlideCandidate = null;
        _pendingSlideSegmentId = null;
        ApplySlideSuggestionButton.IsEnabled = false;
        IgnoreSlideSuggestionButton.IsEnabled = false;
        ResumeSlideFollowButton.IsEnabled = false;
        SlideFollowStatusText.Text = option.Mode switch
        {
            SlideFollowMode.Manual => "手动浏览：后台会建立字幕与课件页码关联，不提示也不自动跳页",
            SlideFollowMode.Suggest => "智能提示：稳定匹配后只提示，不会自动跳页",
            _ => "自动跟随（实验）：只在高置信度、稳定匹配时跳页；手动翻页会暂停"
        };
    }

    private void ResumeSlideFollowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_slideFollow.Mode == SlideFollowMode.Manual) return;
        _slideFollow.Resume();
        _pendingSlideCandidate = null;
        _pendingSlideSegmentId = null;
        ApplySlideSuggestionButton.IsEnabled = false;
        ResumeSlideFollowButton.IsEnabled = false;
        SlideFollowStatusText.Text = "已恢复课件跟随，正在等待稳定字幕匹配";
    }

    private async void ApplySlideSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSlideCandidate is null || _slideDocument is null) return;
        var candidate = _pendingSlideCandidate;
        await SelectSlidePageAsync(candidate.PageNumber, false, $"已跳转到建议的第 {candidate.PageNumber} 页");
        if (_pendingSlideSegmentId is Guid segmentId)
            await _repository.UpdateTranscriptSlideLinkAsync(segmentId, _currentSlidePage, SlideFollowAction.Accepted);
        _pendingSlideCandidate = null;
        _pendingSlideSegmentId = null;
        ApplySlideSuggestionButton.IsEnabled = false;
        IgnoreSlideSuggestionButton.IsEnabled = false;
        SlideFollowStatusText.Text = $"已采用第 {candidate.PageNumber} 页建议；将继续建立课堂关联";
    }

    private async void IgnoreSlideSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSlideCandidate is null) return;
        var candidate = _pendingSlideCandidate;
        if (_pendingSlideSegmentId is Guid segmentId)
            await _repository.UpdateTranscriptSlideLinkAsync(segmentId, _currentSlidePage, SlideFollowAction.Ignored);
        _pendingSlideCandidate = null;
        _pendingSlideSegmentId = null;
        ApplySlideSuggestionButton.IsEnabled = false;
        IgnoreSlideSuggestionButton.IsEnabled = false;
        SlideFollowStatusText.Text = $"已忽略第 {candidate.PageNumber} 页建议；同一页不会重复提示";
    }

    private async Task PersistCurrentSlidePageAsync()
    {
        if (_activeSession is null || _slideDocument is null) return;
        _activeSession = _activeSession with { LastSlidePage = _currentSlidePage };
        await _repository.UpsertSessionAsync(_activeSession);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _paths.EnsureDirectories();
        await _repository.InitializeAsync();
        var existing = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
        var settings = await _settingsStore.LoadAsync();
        _classroomServerUrl = settings.ClassroomServerUrl;
        WorkspaceIdBox.Text = settings.WorkspaceId;
        TranslationDirectionBox.ItemsSource = TranslationDirection.All;
        TranslationDirectionBox.SelectedItem = TranslationDirection.FromId(settings.TranslationDirectionId);
        ApplyDirectionLabels(SelectedDirection);
        KeyStatusText.Text = existing is null
            ? "尚未保存 Key。Key 只会进入 Windows 凭据库。"
            : "已检测到安全保存的千问 Key。";

        var devices = MicrophoneAudioSource.GetDeviceNames();
        MicrophoneBox.ItemsSource = devices;
        if (devices.Count > 0)
        {
            MicrophoneBox.SelectedIndex = 0;
        }
        else
        {
            MicrophoneStatusText.Text = "没有检测到可用麦克风。";
            MicrophoneTestButton.IsEnabled = false;
        }

        await RefreshCoursesAsync();

        var recentInterrupted = (SessionListBox.ItemsSource as IEnumerable<LessonRecord>)?
            .FirstOrDefault(lesson => lesson.Status == SessionStatus.Interrupted
                                      && DateTimeOffset.Now - lesson.StartedAt < TimeSpan.FromHours(12));
        if (recentInterrupted is not null)
        {
            _loadingNavigation = true;
            SessionListBox.SelectedItem = recentInterrupted;
            _loadingNavigation = false;
            await ShowHistoryAsync(recentInterrupted);
            StatusText.Text = "已恢复刚才意外中断的课堂、字幕和课件";
        }

        _initializationCompleted.TrySetResult();
    }

    private void ClassroomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_classroomWindow is { IsVisible: true }) { _classroomWindow.Activate(); return; }
        _classroomWindow = new ClassroomWindow(_classroomSync, _classroomServerUrl) { Owner = this };
        _classroomWindow.Closed += async (_, _) =>
        {
            if (_classroomWindow is null) return;
            _classroomServerUrl = _classroomWindow.ServerUrl;
            _classroomWindow = null;
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { ClassroomServerUrl = _classroomServerUrl });
        };
        _classroomWindow.Show();
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _classroomSync.DisposeAsync();
        base.OnClosed(e);
    }

    private async Task RefreshCoursesAsync(Guid? selectCourseId = null)
    {
        _loadingNavigation = true;
        var courses = await _repository.GetCoursesAsync(ShowArchivedCheckBox.IsChecked == true);
        CourseListBox.ItemsSource = courses;
        var target = courses.FirstOrDefault(course => course.Id == (selectCourseId ?? _selectedCourse?.Id)) ?? courses.FirstOrDefault();
        Cour�]x��$z{-���jם ? new PptxSlideExtractor().Extract(fileName)
                : new PdfSlideExtractor().Extract(fileName);
            if (isPptx)
            {
                SlideImportStatusText.Text = "正在本机生成 PPT 页面预览……";
                try
                {
                    var thumbnails = await Task.Run(() => new PowerPointThumbnailRenderer().Render(fileName, _paths.CacheDirectory));
                    slideDocument = slideDocument with
                    {
                        Pages = slideDocument.Pages.Select(page =>
                            page with { ThumbnailPath = thumbnails.GetValueOrDefault(page.PageNumber) }).ToArray()
                    };
                }
                catch (Exception thumbnailException)
                {
                    previewWarning = $"；页面图片暂不可用，使用文本预览：{thumbnailException.Message}";
                }
            }
            else
            {
                SlideImportStatusText.Text = "正在生成 PDF 页面预览……";
                try
                {
                    var thumbnails = await Task.Run(() =>
                        new PdfPageImageRenderer().Render(fileName, _paths.CacheDirectory, slideDocument.Pages.Count));
                    slideDocument = slideDocument with
                    {
                        Pages = slideDocument.Pages.Select(page =>
                            page with { ThumbnailPath = thumbnails.GetValueOrDefault(page.PageNumber) }).ToArray()
                    };
                }
                catch (Exception thumbnailException)
                {
                    previewWarning = $"；PDF 页面暂时无法显示，已保留文字：{thumbnailException.Message}";
                }
            }
            if (historyLoadVersion is not null && historyLoadVersion != _historyLoadVersion) return;
            _slideDocument = slideDocument;
            _currentSlidePage = Math.Clamp(initialPage ?? 1, 1, slideDocument.Pages.Count);
            SlideImportStatusText.Text = announceCopy
                ? $"已保存并绑定本节课 · {_slideDocument.Pages.Count} 页 · 副本：{fileName}{previewWarning}"
                : $"已打开本节课保存的课件 · {_slideDocument.Pages.Count} 页 · {Path.GetFileName(fileName)}{previewWarning}";
            ShowSlide(_slideDocument.Pages.FirstOrDefault(page => page.PageNumber == _currentSlidePage));
        }
        catch (Exception exception)
        {
            SlideImportStatusText.Text = $"课件打开失败：{exception.Message}";
        }
    }

    private void ShowSlide(SlidePage? page)
    {
        if (page is null)
        {
            CurrentSlideTitleText.Text = "当前课件：无页面";
            CurrentSlideBodyText.Text = string.Empty;
            CurrentSlideImage.Source = null;
            PreviousSlideButton.IsEnabled = false;
            NextSlideButton.IsEnabled = false;
            JumpPageButton.IsEnabled = false;
            JumpPageBox.IsEnabled = false;
            return;
        }

        CurrentSlideTitleText.Text = $"第 {page.PageNumber} 页 · {page.Title}";
        CurrentSlideBodyText.Text = page.Text;
        CurrentSlideImage.Source = string.IsNullOrWhiteSpace(page.ThumbnailPath)
            ? null
            : new BitmapImage(new Uri(page.ThumbnailPath, UriKind.Absolute));
        CurrentSlideBodyText.Visibility = CurrentSlideImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        PreviousSlideButton.IsEnabled = _slideDocument is not null && page.PageNumber > 1;
        NextSlideButton.IsEnabled = _slideDocument is not null && page.PageNumber < _slideDocument.Pages.Count;
        JumpPageButton.IsEnabled = _slideDocument is not null;
        JumpPageBox.IsEnabled = _slideDocument is not null;
        JumpPageBox.Text = page.PageNumber.ToString();
    }

    private async Task TranslateInterimAsync(
        QwenMtTranslator translator,
        string sourceText,
        CancellationToken cancellationToken,
        long renderVersion,
        string outputPrefix = "")
    {
        try
        {
            await Task.Delay(550, cancellationToken);
            var translated = await TranslateTrackedAsync(translator, sourceText, false, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (renderVersion != Volatile.Read(ref _subtitleRenderVersion)) return;
                ChineseSubtitleText.Text = $"{outputPrefix}{translated}";
                TranslationStatusText.Text = string.Empty;
                // Interim translation updates share this scroll surface with the transcript.
                // Do not disturb a student who is reading an earlier confirmed subtitle.
            });
            // Interim text is disposable. If the ASR stream stops before a final segment
            // arrives, clear it instead of leaving a large repeated filler phrase frozen.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (renderVersion == Volatile.Read(ref _subtitleRenderVersion))
                {
                    ChineseSubtitleText.Text = "正在聆听……";
                    TranslationStatusText.Text = "识别暂未确认，正在等待下一段语音。";
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => TranslationStatusText.Text = FriendlyTranslationError(exception));
        }
    }

    private void ConfigureTranslationContext(QwenMtTranslator translator)
    {
        translator.DomainHint = SlideTerminology.BuildDomainHint(_slideDocument, _currentSlidePage);
        translator.PreservedTerms = SlideTerminology.PreservedTerms(_slideDocument, _currentSlidePage);
    }

    private async Task<SlideLink> LinkFinalSubtitleToSlideAsync(
        string sourceText,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        if (_slideDocument is null || _slideDocument.Pages.Count == 0)
            return new(null, null, null, null, SlideFollowAction.None);

        _stableSlideTranscriptWindow.Add(sourceText);
        if (_stableSlideTranscriptWindow.Count > 5) _stableSlideTranscriptWindow.RemoveAt(0);

        var result = _slideMatcher.Match(new SlideMatchContext(
            _slideDocument, _currentSlidePage, _stableSlideTranscriptWindow));
        var decision = _slideFollow.Evaluate(result, _currentSlidePage, DateTimeOffset.Now);
        var candidate = decision.Candidate;
        var evidence = candidate is null ? null : string.Join("、", candidate.EvidenceTerms.Take(6));
        var action = _slideFollow.Mode == SlideFollowMode.Manual
            ? SlideFollowAction.ManuallyViewed
            : SlideFollowAction.None;

        switch (decision.Kind)
        {
            case SlideFollowDecisionKind.Suggest when candidate is not null:
                _pendingSlideCandidate = candidate;
                _pendingSlideSegmentId = segmentId;
                ApplySlideSuggestionButton.IsEnabled = true;
                IgnoreSlideSuggestionButton.IsEnabled = true;
                SlideFollowStatusText.Text = $"{decision.Status} · 证据：{evidence}";
                action = SlideFollowAction.Suggested;
                break;

            case SlideFollowDecisionKind.AutoNavigate when candidate is not null:
                await SelectSlidePageAsync(candidate.PageNumber, false, $"已自动跟随至第 {candidate.PageNumber} 页");
                ApplySlideSuggestionButton.IsEnabled = false;
                IgnoreSlideSuggestionButton.IsEnabled = false;
                SlideFollowStatusText.Text = $"{decision.Status} · 证据：{evidence}";
                action = SlideFollowAction.AutoFollowed;
                break;

            default:
                if (candidate is not null)
                    SlideFollowStatusText.Text = $"{decision.Status} · 候选第 {candidate.PageNumber} 页 · {candidate.Score:P0}";
                else
                    SlideFollowStatusText.Text = decision.Status;
                break;
        }

        return new SlideLink(
            _currentSlidePage,
            candidate?.PageNumber,
            candidate?.Score,
            evidence,
            action);
    }

    private async ValueTask<string> TranslateTrackedAsync(
        QwenMtTranslator translator,
        string sourceText,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        try
        {
            var translated = await translator.TranslateAsync(sourceText, isFinal, cancellationToken);
            await _repository.RecordAiUsageAsync(new AiUsageRecord(
                DateOnly.FromDateTime(DateTime.Now), AiUsageKind.Translation, QwenMtProtocol.Model,
                1, 0, sourceText.Length, translated.Length,
                AiUsageRecord.EstimateTokens(sourceText), AiUsageRecord.EstimateTokens(translated), 0), cancellationToken);
            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await _repository.RecordAiUsageAsync(new AiUsageRecord(
                DateOnly.FromDateTime(DateTime.Now), AiUsageKind.Translation, QwenMtProtocol.Model,
                1, 1, sourceText.Length, 0, AiUsageRecord.EstimateTokens(sourceText), 0, 0));
            throw;
        }
    }

    private void AppendConfirmedTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_confirmedTranslations.Count == 0 || !string.Equals(_confirmedTranslations[^1], text, StringComparison.Ordinal))
            _confirmedTranslations.Add(text.Trim());
        while (_confirmedTranslations.Count > 30) _confirmedTranslations.RemoveAt(0);
        var selectionStart = ConfirmedTranslationText.SelectionStart;
        var selectionLength = ConfirmedTranslationText.SelectionLength;
        var previousOffset = TranslationScrollViewer.VerticalOffset;
        ConfirmedTranslationText.Text = string.Join("\n\n", _confirmedTranslations);
        if (selectionLength > 0 && selectionStart + selectionLength <= ConfirmedTranslationText.Text.Length)
            ConfirmedTranslationText.Select(selectionStart, selectionLength);
        FollowOrNotifyNewSubtitle(previousOffset);
    }

    private static string CompactLiveSource(string text, int maxCharacters = 260)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maxCharacters ? compact : $"…{compact[^maxCharacters..]}";
    }

    private void FollowOrNotifyNewSubtitle(double? preservedOffset = null)
    {
        if (_followLiveSubtitles)
        {
            TranslationScrollViewer.ScrollToEnd();
            NewSubtitleButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            NewSubtitleButton.Visibility = Visibility.Visible;
            if (preservedOffset is not null)
                Dispatcher.BeginInvoke(() => TranslationScrollViewer.ScrollToVerticalOffset(
                    Math.Min(preservedOffset.Value, TranslationScrollViewer.ScrollableHeight)));
        }
    }

    private void TranslationScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange != 0) return;
        _followLiveSubtitles = TranslationScrollViewer.ScrollableHeight - TranslationScrollViewer.VerticalOffset <= 20;
        if (_followLiveSubtitles) NewSubtitleButton.Visibility = Visibility.Collapsed;
    }

    private void NewSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        _followLiveSubtitles = true;
        TranslationScrollViewer.ScrollToEnd();
        NewSubtitleButton.Visibility = Visibility.Collapsed;
    }

    private static string FriendlyTranslationError(Exception exception)
    {
        if (exception is QwenProviderException provider && provider.Code.Contains("429", StringComparison.OrdinalIgnoreCase))
            return "翻译请求过快或额度暂时受限，正在降低请求频率；原文和录音已保存。";
        return $"翻译暂不可用，原文已保存：{exception.Message}";
    }

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool IsRetryable(QwenProviderException exception) =>
        !exception.Code.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        && !exception.Code.Contains("401", StringComparison.OrdinalIgnoreCase)
        && !exception.Code.Contains("balance", StringComparison.OrdinalIgnoreCase);

    private async void MicrophoneTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_microphoneTestCancellation is not null)
        {
            _microphoneTestCancellation.Cancel();
            return;
        }

        if (MicrophoneBox.SelectedIndex < 0)
        {
            MicrophoneStatusText.Text = "请先选择麦克风。";
            return;
        }

        _microphoneTestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = _microphoneTestCancellation;
        var output = Path.Combine(_paths.AudioDirectory, $"microphone-test-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        MicrophoneTestButton.Content = "停止测试录音";
        MicrophoneStatusText.Text = "正在录音，请用课堂距离正常说话……";
        MicrophoneBox.IsEnabled = false;

        try
        {
            var source = new MicrophoneAudioSource();
            using var writer = new WaveSegmentWriter(output, ClassInterpreter.Core.Audio.AudioFormat.ClassroomDefault);
            await foreach (var frame in source.CaptureAsync(MicrophoneBox.SelectedIndex, cancellation.Token))
            {
                writer.Write(frame.Pcm.Span);
                AudioLevelBar.Value = PcmLevelMonitor.Peak(frame.Pcm.Span);
            }
        }
        catch (OperationCanceledException)
        {
            MicrophoneStatusText.Text = $"测试完成：{output}";
        }
        catch (Exception exception)
        {
            MicrophoneStatusText.Text = $"麦克风测试失败：{exception.Message}";
        }
        finally
        {
            cancellation.Dispose();
            _microphoneTestCancellation = null;
            AudioLevelBar.Value = 0;
            MicrophoneBox.IsEnabled = true;
            MicrophoneTestButton.Content = "录制 30 秒测试音频";
        }
    }

    private sealed record SlideFollowModeOption(SlideFollowMode Mode, string Label)
    {
        public override string ToString() => Label;
    }
}
