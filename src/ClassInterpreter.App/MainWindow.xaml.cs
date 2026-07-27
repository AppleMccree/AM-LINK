using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Runtime.CompilerServices;
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
        CourseListBox.SelectedItem = target;
        _selectedCourse = target;
        CourseNameBox.Text = target?.Name ?? "请在左侧选择或新建课程";
        ArchiveCourseButton.Content = target?.IsArchived == true ? "恢复" : "归档";
        await RefreshSessionListAsync();
        _loadingNavigation = false;
    }

    private async Task RefreshSessionListAsync()
    {
        if (_selectedCourse is null)
        {
            SessionListBox.ItemsSource = Array.Empty<LessonRecord>();
            return;
        }

        var sessions = await _repository.GetSessionsForCourseAsync(_selectedCourse.Id);
        SessionListBox.ItemsSource = LessonRecord.Build(sessions, MergeNearbySessionsCheckBox.IsChecked == true);
    }

    private async void CreateCourseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeCourse()) return;
        var name = NewCourseNameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请输入课程名称"; return; }
        var course = new Course(Guid.NewGuid(), name, DateTimeOffset.Now, false);
        await _repository.UpsertCourseAsync(course);
        ResetLessonWorkspace();
        NewCourseNameBox.Clear();
        await RefreshCoursesAsync(course.Id);
        StatusText.Text = $"已新建课程：{name}";
    }

    private async void RenameCourseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeCourse() || _selectedCourse is null) return;
        var name = NewCourseNameBox.Text.Trim();
        if (name.Length == 0) { NewCourseNameBox.Text = _selectedCourse.Name; NewCourseNameBox.SelectAll(); StatusText.Text = "在上方输入新名称，再点一次改名"; return; }
        _selectedCourse = _selectedCourse with { Name = name };
        await _repository.UpsertCourseAsync(_selectedCourse);
        NewCourseNameBox.Clear();
        await RefreshCoursesAsync(_selectedCourse.Id);
        StatusText.Text = $"课程已改名为：{name}";
    }

    private async void ArchiveCourseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeCourse() || _selectedCourse is null) return;
        _selectedCourse = _selectedCourse with { IsArchived = !_selectedCourse.IsArchived };
        await _repository.UpsertCourseAsync(_selectedCourse);
        StatusText.Text = _selectedCourse.IsArchived ? "课程已归档，资料仍完整保留" : "课程已恢复";
        await RefreshCoursesAsync();
    }

    private async void ShowArchivedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshCoursesAsync();
    }

    private async void MergeNearbySessionsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _classCancellation is not null) return;
        _historyLoadVersion++;
        ExitHistoryView();
        await RefreshSessionListAsync();
        StatusText.Text = MergeNearbySessionsCheckBox.IsChecked == true
            ? "已按时间合并同一节课的中断记录"
            : "已显示每一次原始记录";
    }

    private async void CourseListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingNavigation) return;
        if (!CanChangeCourse()) { _loadingNavigation = true; CourseListBox.SelectedItem = _selectedCourse; _loadingNavigation = false; return; }
        _selectedCourse = CourseListBox.SelectedItem as Course;
        CourseNameBox.Text = _selectedCourse?.Name ?? "请在左侧选择或新建课程";
        ArchiveCourseButton.Content = _selectedCourse?.IsArchived == true ? "恢复" : "归档";
        await RefreshSessionListAsync();
        ResetLessonWorkspace();
        ExitHistoryView();
    }

    private void ResetLessonWorkspace()
    {
        _materialPath = null;
        _slideDocument = null;
        _currentSlidePage = 1;
        ShowSlide(null);
        SlideImportStatusText.Text = "尚未导入课件";
    }

    private async void SessionListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DeleteSessionButton.IsEnabled = SessionListBox.SelectedItem is LessonRecord && _classCancellation is null;
        MergeWithPreviousLessonButton.IsEnabled = SessionListBox.SelectedItem is LessonRecord selectedLesson
                                                       && selectedLesson.LessonNumber > 1
                                                       && _classCancellation is null;
        if (_loadingNavigation || SessionListBox.SelectedItem is not LessonRecord lesson) return;
        if (!CanChangeCourse()) return;
        await ShowHistoryAsync(lesson);
    }

    private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeCourse() || SessionListBox.SelectedItem is not LessonRecord lesson) return;
        var confirmation = MessageBox.Show(
            $"确定删除第 {lesson.LessonNumber} 节课吗？\n\n其中 {lesson.Sessions.Count} 条中断记录、字幕和问AI记录会被删除，课程本身和磁盘课件副本不会删除。",
            "删除课堂记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        var lessonKey = lesson.Sessions.Select(session => session.LessonKey).FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
        foreach (var session in lesson.Sessions) await _repository.DeleteSessionAsync(session.Id);
        if (!string.IsNullOrWhiteSpace(lessonKey)) await _repository.DeleteAiQuestionsForLessonAsync(lessonKey);
        _selectedHistoryLesson = null;
        DeleteSessionButton.IsEnabled = false;
        ResetLessonWorkspace();
        ExitHistoryView();
        await RefreshSessionListAsync();
        StatusText.Text = "课堂记录已删除";
    }

    private async void MergeWithPreviousLessonButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeCourse() || _selectedCourse is null || SessionListBox.SelectedItem is not LessonRecord selected) return;
        var sessions = await _repository.GetSessionsForCourseAsync(_selectedCourse.Id);
        var lessons = LessonRecord.Build(sessions, MergeNearbySessionsCheckBox.IsChecked == true);
        var previous = lessons
            .Where(item => item.StartedAt < selected.StartedAt)
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        if (previous is null)
        {
            StatusText.Text = "这已经是本课程最早的一节课，前面没有可合并的记录";
            return;
        }

        var confirmation = MessageBox.Show(
            $"把第 {selected.LessonNumber} 节课合并到第 {previous.LessonNumber} 节课吗？\n\n字幕、问AI记录和课件关联都会保留；原有学习总结会失效，需要重新总结。",
            "合并课堂记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        var targetKey = await EnsureLessonKeyAsync(previous);
        var sourceKey = await EnsureLessonKeyAsync(selected);
        await _repository.MergeLessonsAsync(_selectedCourse.Id, sourceKey, targetKey);
        _selectedHistoryLesson = null;
        ExitHistoryView();
        await RefreshSessionListAsync();
        StatusText.Text = "两节课已合并；打开合并后的课堂即可查看全部字幕，需要时点击“重新总结”";
    }

    private async Task ShowHistoryAsync(LessonRecord lesson)
    {
        var loadVersion = ++_historyLoadVersion;
        _selectedHistoryLesson = lesson;
        var lessonKey = await EnsureLessonKeyAsync(lesson);
        var transcriptBatches = new List<(Session Session, IReadOnlyList<TranscriptSegment> Segments)>();
        foreach (var session in lesson.Sessions.OrderBy(item => item.StartedAt))
        {
            var sessionTranscripts = await _repository.GetTranscriptsAsync(session.Id);
            transcriptBatches.Add((session, sessionTranscripts));
        }
        var transcripts = LessonTranscriptTimeline.Combine(transcriptBatches);
        if (loadVersion != _historyLoadVersion) return;
        _confirmedTranslations.Clear();
        LiveSubtitlePanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
        HistoryTitleText.Text = $"第 {lesson.LessonNumber} 节课 · 学习记录";
        var materialName = string.IsNullOrWhiteSpace(lesson.MaterialPath) ? "未保存课件" : Path.GetFileName(lesson.MaterialPath);
        HistoryMetaText.Text = $"{lesson.StartedAt:yyyy-MM-dd HH:mm} · {lesson.Sessions.Count} 段记录已合并 · {materialName}";
        HistoryMetaText.ToolTip = string.IsNullOrWhiteSpace(lesson.MaterialPath) ? "这节课没有绑定课件" : $"课件副本：{lesson.MaterialPath}";
        _historyTranscripts = transcripts;
        _historyQuestions = await _repository.GetAiQuestionsAsync(lessonKey);
        _historyStudyPackPath = lesson.StudyPackPath;
        RenderHistoryTranscript();
        HistoryTranscriptBox.ScrollToHome();
        StartClassButton.IsEnabled = false;
        ImportSlidesButton.IsEnabled = false;
        DemoButton.IsEnabled = false;
        OpenStudyPackButton.IsEnabled = !string.IsNullOrWhiteSpace(lesson.StudyPackPath) && File.Exists(lesson.StudyPackPath);
        _historyRecordings = FindLessonRecordings(lesson);
        OpenRecordingsButton.IsEnabled = _historyRecordings.Count > 0;
        OpenRecordingsButton.Content = _historyRecordings.Count > 0
            ? $"查看本节课录音（{_historyRecordings.Count} 段）"
            : "本节课没有录音";
        if (!string.IsNullOrWhiteSpace(lesson.MaterialPath) && File.Exists(lesson.MaterialPath)) await LoadSlidesAsync(lesson.MaterialPath, false, loadVersion, lesson.LastSlidePage);
        else { _slideDocument = null; ShowSlide(null); SlideImportStatusText.Text = "这次课堂没有保存课件"; }
        if (loadVersion != _historyLoadVersion) return;
        await EnsureLessonArchiveAsync(lesson, lessonKey, transcripts, _historyQuestions, _slideDocument);
        StatusText.Text = "正在查看历史课堂；选择课程或开始新课堂可返回";
    }

    private void ExitHistoryView()
    {
        _selectedHistoryLesson = null;
        SessionListBox.SelectedItem = null;
        HistoryPanel.Visibility = Visibility.Collapsed;
        LiveSubtitlePanel.Visibility = Visibility.Visible;
        StartClassButton.IsEnabled = _selectedCourse is not null && !_selectedCourse.IsArchived;
        ImportSlidesButton.IsEnabled = StartClassButton.IsEnabled;
        DemoButton.IsEnabled = StartClassButton.IsEnabled;
        OpenStudyPackButton.IsEnabled = false;
        OpenRecordingsButton.IsEnabled = false;
        OpenRecordingsButton.Content = "查看本节课录音";
        _historyRecordings = [];
        _historyStudyPackPath = null;
        DeleteSessionButton.IsEnabled = false;
        MergeWithPreviousLessonButton.IsEnabled = false;
        ApplyDirectionLabels(SelectedDirection);
    }

    private void ExitHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ResetLessonWorkspace();
        ExitHistoryView();
        StatusText.Text = "已返回当前课堂";
    }

    private async void ContinueLessonButton_Click(object sender, RoutedEventArgs e)
    {
        var lesson = _selectedHistoryLesson;
        if (lesson is null || _selectedCourse is null || _classCancellation is not null) return;
        var lessonKey = lesson.Sessions
            .Select(session => session.LessonKey)
            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key))
            ?? lesson.Sessions.OrderBy(session => session.StartedAt).First().Id.ToString("N");
        foreach (var session in lesson.Sessions)
        {
            if (!string.Equals(session.LessonKey, lessonKey, StringComparison.Ordinal))
            {
                await _repository.UpsertSessionAsync(session with { LessonKey = lessonKey });
            }
        }

        _resumeLessonKey = lessonKey;
        _resumeLessonNumber = lesson.LessonNumber;
        _resumeLessonDirectory = GetLessonDirectory(lesson);
        _resumeSlidePage = lesson.LastSlidePage;
        _resumeConfirmedTranslations = _historyTranscripts
            .Where(segment => segment.IsFinal)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.Sequence)
            .Select(segment => segment.TargetText ?? segment.ChineseText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .Distinct(StringComparer.Ordinal)
            .TakeLast(30)
            .ToArray();
        _materialPath = lesson.MaterialPath;
        ExitHistoryView();
        StatusText.Text = $"正在继续第 {lesson.LessonNumber} 节课同传……";
        StartClassButton_Click(sender, e);
    }

    private void CopyHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HistoryTranscriptBox.Text)) return;
        Clipboard.SetText(HistoryTranscriptBox.Text);
        StatusText.Text = "整节课字幕已复制";
    }

    private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedHistoryLesson is null || _historyRecordings.Count == 0)
        {
            StatusText.Text = "本节课没有可用录音";
            return;
        }

        var window = new LessonRecordingsWindow(
            $"第 {_selectedHistoryLesson.LessonNumber} 节课录音",
            _historyRecordings) { Owner = this };
        window.Show();
    }

    private IReadOnlyList<LessonRecordingItem> FindLessonRecordings(LessonRecord lesson)
    {
        if (_selectedCourse is null) return [];
        var courseDirectory = GetCourseDirectory(_selectedCourse);
        var found = new Dictionary<string, LessonRecordingItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in lesson.Sessions.OrderBy(item => item.StartedAt))
        {
            var stamp = session.StartedAt.ToString("yyyyMMdd-HHmmss");
            if (Directory.Exists(courseDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(courseDirectory, $"*-{stamp}"))
                foreach (var path in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
                    found[path] = LessonRecordingItem.From(path, session.StartedAt);
            }

            if (Directory.Exists(_paths.AudioDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_paths.AudioDirectory, $"class-{stamp}-*.wav", SearchOption.TopDirectoryOnly))
                    found[path] = LessonRecordingItem.From(path, session.StartedAt);
            }
        }
        return found.Values.OrderBy(item => item.StartedAt).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void MergeTranscriptCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _selectedHistoryLesson is null) return;
        RenderHistoryTranscript();
    }

    private void RenderHistoryTranscript()
    {
        var merge = MergeTranscriptCheckBox.IsChecked == true;
        HistoryTranscriptBox.Text = $"【翻译字幕】\r\n\r\n{TranscriptHistoryFormatter.Format(_historyTranscripts, false, merge)}" +
                                    $"\r\n\r\n────────────\r\n\r\n【识别原文】\r\n\r\n{TranscriptHistoryFormatter.Format(_historyTranscripts, true, merge)}" +
                                    $"\r\n\r\n────────────\r\n\r\n【问AI记录】\r\n\r\n{RenderAiQuestions(_historyQuestions)}";
        HistoryTranscriptBox.ScrollToHome();
    }

    private static string RenderAiQuestions(IReadOnlyList<AiQuestionRecord> questions)
    {
        if (questions.Count == 0) return "（这节课还没有询问AI）";
        return string.Join("\r\n\r\n", questions.Select(item =>
            $"[{item.AskedAt:HH:mm:ss}] 问：{item.Question}\r\n答：{item.Answer ?? $"（{item.Error ?? item.Status.ToString()}）"}"));
    }

    private void OpenStudyPackButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _historyStudyPackPath ?? _selectedHistoryLesson?.StudyPackPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void RegenerateStudyPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedHistoryLesson is null || _selectedCourse is null) return;
        if (Interlocked.CompareExchange(ref _studyPackGenerationRunning, 1, 0) != 0)
        {
            StatusText.Text = "学习总结正在后台生成，请继续查看课堂记录。";
            return;
        }
        RegenerateStudyPackButton.IsEnabled = false;
        try
        {
            var apiKey = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("请先在设置中保存千问 API Key");
            var lessonKey = await EnsureLessonKeyAsync(_selectedHistoryLesson);
            var directory = GetLessonDirectory(_selectedHistoryLesson);
            var representative = _selectedHistoryLesson.Sessions.OrderBy(item => item.StartedAt).First() with
            {
                EndedAt = _selectedHistoryLesson.EndedAt,
                LessonKey = lessonKey
            };
            Directory.CreateDirectory(directory);
            await LessonAiBundleWriter.WriteAsync(
                directory, representative, lessonKey, _slideDocument, _historyTranscripts, _historyQuestions);
            StatusText.Text = "学习总结正在后台生成；你可以继续查看记录或做其他操作。";
            _ = CompleteStudyPackGenerationAsync(
                representative, apiKey, WorkspaceIdBox.Text.Trim(), directory,
                _historyTranscripts, _slideDocument, _historyQuestions,
                _selectedHistoryLesson.Sessions, lessonKey);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _studyPackGenerationRunning, 0);
            StatusText.Text = $"重新生成失败：{exception.Message}";
        }
        finally
        {
            RegenerateStudyPackButton.IsEnabled = true;
        }
    }

    private async Task CompleteStudyPackGenerationAsync(
        Session representative, string apiKey, string workspaceId, string directory,
        IReadOnlyList<TranscriptSegment> transcripts, SlideDocument? slides,
        IReadOnlyList<AiQuestionRecord> questions, IReadOnlyList<Session> sessions, string lessonKey)
    {
        try
        {
            var output = await GenerateStudyPackAsync(
                _repository, representative, apiKey, workspaceId, directory, transcripts, slides, questions);
            if (output is null) return;
            foreach (var session in sessions)
                await _repository.UpsertSessionAsync(session with { StudyPackPath = output, LessonKey = lessonKey });
            await Dispatcher.InvokeAsync(() =>
            {
                if (_selectedHistoryLesson is not null)
                {
                    _historyStudyPackPath = output;
                    OpenStudyPackButton.IsEnabled = true;
                }
                StatusText.Text = "学习总结已在后台生成，可以打开 AI 学习包。";
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = $"后台生成学习总结失败：{exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _studyPackGenerationRunning, 0);
        }
    }

    private async void AskAiButton_Click(object sender, RoutedEventArgs e) => await OpenAiTutorAsync(null);

    private void SubtitleContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not TextBox textBox) return;
        var hasSelection = !string.IsNullOrWhiteSpace(textBox.SelectedText);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "AskAiSelectedMenuItem") is { } askItem)
            askItem.IsEnabled = hasSelection;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "CopySelectedMenuItem") is { } copyItem)
            copyItem.IsEnabled = textBox.SelectionLength > 0;
    }

    private async void AskSelectedTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu menu } || menu.PlacementTarget is not TextBox textBox) return;
        var selection = textBox.SelectedText.Trim();
        if (string.IsNullOrWhiteSpace(selection)) return;
        await OpenAiTutorAsync(selection);
    }

    private void CopySelectedTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu menu } || menu.PlacementTarget is not TextBox textBox || textBox.SelectionLength == 0) return;
        Clipboard.SetText(textBox.SelectedText);
    }

    private async Task OpenAiTutorAsync(string? selectedTextOverride)
    {
        if (_aiTutorWindow is { IsVisible: true })
        {
            if (!string.IsNullOrWhiteSpace(selectedTextOverride)) _aiTutorWindow.SetQuestion(selectedTextOverride);
            _aiTutorWindow.Activate();
            return;
        }

        string lessonKey;
        Guid? courseId = _selectedCourse?.Id;
        if (_activeSession is not null)
        {
            lessonKey = _activeSession.LessonKey ?? _activeSession.Id.ToString("N");
        }
        else if (_selectedHistoryLesson is not null)
        {
            lessonKey = await EnsureLessonKeyAsync(_selectedHistoryLesson);
        }
        else
        {
            StatusText.Text = "请先开始课堂或打开一节历史课堂，再询问AI";
            return;
        }

        var explicitlySelectedText = !string.IsNullOrWhiteSpace(selectedTextOverride)
            ? selectedTextOverride.Trim()
            : HistoryPanel.Visibility == Visibility.Visible
                ? HistoryTranscriptBox.SelectedText.Trim()
                : FirstSelectedText(ConfirmedTranslationText.SelectedText, ChineseSubtitleText.SelectedText);
        var selectedText = explicitlySelectedText;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            selectedText = _confirmedTranslations.LastOrDefault()
                           ?? (ChineseSubtitleText.Text.Contains("等待", StringComparison.Ordinal) ? null : ChineseSubtitleText.Text)
                           ?? SourceSubtitleText.Text;
        }
        if (selectedText?.Length > 1200) selectedText = selectedText[..1200];
        var capturedSelection = selectedText;
        var initialQuestion = string.IsNullOrWhiteSpace(explicitlySelectedText) ? null : explicitlySelectedText;
        _aiTutorWindow = new AiTutorWindow(capturedSelection ?? "当前课堂、课件和最近字幕", initialQuestion, async (question, retry) =>
            await SubmitAiQuestionAsync(question, retry, lessonKey, courseId, capturedSelection));
        _aiTutorWindow.Owner = this;
        _aiTutorWindow.Closed += (_, _) => _aiTutorWindow = null;
        _aiTutorWindow.Show();
    }

    private static string FirstSelectedText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private async void QuickTranslatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_quickTranslatorWindow is { IsVisible: true })
        {
            _quickTranslatorWindow.Activate();
            return;
        }

        var apiKey = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText.Text = "请先在设置中保存千问 API Key";
            return;
        }
        if (MicrophoneBox.SelectedIndex < 0)
        {
            StatusText.Text = "请先选择麦克风";
            return;
        }
        try { _ = QwenEndpoint.Singapore(WorkspaceIdBox.Text.Trim()); }
        catch (Exception exception) { StatusText.Text = exception.Message; return; }
        _quickTranslatorWindow = new QuickTranslatorWindow(
            WorkspaceIdBox.Text.Trim(), apiKey, MicrophoneBox.SelectedIndex,
            Path.Combine(_paths.ExportDirectory, "双向同传记录"),
            async usage => await _repository.RecordAiUsageAsync(usage));
        _quickTranslatorWindow.Owner = this;
        _quickTranslatorWindow.Closed += (_, _) => _quickTranslatorWindow = null;
        _quickTranslatorWindow.Show();
    }

    private async Task<AiQuestionRecord> SubmitAiQuestionAsync(
        string question,
        AiQuestionRecord? retry,
        string lessonKey,
        Guid? courseId,
        string? selectedText)
    {
        var transcripts = _activeSession is not null && string.Equals(_activeSession.LessonKey, lessonKey, StringComparison.Ordinal)
            ? await GetCompleteLessonTranscriptsAsync(_repository, _activeSession)
            : _historyTranscripts;
        var timestamp = transcripts.LastOrDefault()?.Start;
        var pending = (retry ?? new AiQuestionRecord(
            Guid.NewGuid(), lessonKey, courseId, DateTimeOffset.Now, question, selectedText, null,
            _slideDocument is null ? null : _currentSlidePage,
            timestamp is null ? null : FormatTimestamp(timestamp.Value),
            QwenAiTutorProtocol.Model, AiQuestionStatus.Pending, null)) with
        {
            Answer = null,
            Status = AiQuestionStatus.Pending,
            Error = null
        };
        await _repository.UpsertAiQuestionAsync(pending);
        if (_classroomSync is { IsConnected: true, CurrentClassroom: not null })
        {
            var room = _classroomSync.CurrentClassroom;
            _ = _classroomSync.PublishQuestionAsync(new ClassroomQuestionEvent(
                pending.Id, room.LessonId, question, pending.AskedAt, pending.TranscriptTimestamp,
                pending.SlidePage, selectedText is { Length: > 500 } ? selectedText[..500] : selectedText));
        }

        try
        {
            var request = new AiTutorRequest(
                question,
                selectedText,
                _selectedCourse?.Name ?? _activeSession?.CourseName ?? "课堂",
                _slideDocument is null ? null : _currentSlidePage,
                BuildSlideContext(question, selectedText),
                BuildTranscriptContext(transcripts, question, selectedText));
            var tutorInput = $"{request.Question}\n{request.SelectedText}\n{request.SlideContext}\n{request.TranscriptContext}";
            var apiKey = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
            string answer;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                using var tutor = new QwenAiTutorService(QwenEndpoint.SingaporeTranslation(WorkspaceIdBox.Text.Trim()), apiKey);
                answer = await tutor.AskAsync(request);
            }
            else if (_classroomSync.IsConnected)
            {
                var prompt = $"课程：{request.CourseName}\n当前PPT页：{request.CurrentSlidePage?.ToString() ?? "无"}\n选中内容：{request.SelectedText ?? "无"}\n\n课件上下文：\n{request.SlideContext}\n\n课堂字幕上下文：\n{request.TranscriptContext}\n\n问题：{request.Question}";
                answer = await _classroomSync.AskWithSchoolKeyAsync(prompt);
            }
            else throw new InvalidOperationException("请保存个人千问API Key，或加入已配置学校统一Key的云端课堂");
            var completed = pending with { Answer = answer, Status = AiQuestionStatus.Completed };
            await _repository.RecordAiUsageAsync(new AiUsageRecord(
                DateOnly.FromDateTime(DateTime.Now), AiUsageKind.AiTutor, QwenAiTutorProtocol.Model,
                1, 0, tutorInput.Length, answer.Length,
                AiUsageRecord.EstimateTokens(tutorInput), AiUsageRecord.EstimateTokens(answer), 0));
            await _repository.UpsertAiQuestionAsync(completed);
            await RefreshAiQuestionsIfVisibleAsync(lessonKey);
            return completed;
        }
        catch (Exception exception)
        {
            await _repository.RecordAiUsageAsync(new AiUsageRecord(
                DateOnly.FromDateTime(DateTime.Now), AiUsageKind.AiTutor, QwenAiTutorProtocol.Model,
                1, 1, question.Length + (selectedText?.Length ?? 0), 0,
                AiUsageRecord.EstimateTokens(question) + AiUsageRecord.EstimateTokens(selectedText), 0, 0));
            var failed = pending with { Status = AiQuestionStatus.Failed, Error = exception.Message };
            await _repository.UpsertAiQuestionAsync(failed);
            await RefreshAiQuestionsIfVisibleAsync(lessonKey);
            return failed;
        }
    }

    private async Task RefreshAiQuestionsIfVisibleAsync(string lessonKey)
    {
        if (_selectedHistoryLesson is null) return;
        var selectedKey = await EnsureLessonKeyAsync(_selectedHistoryLesson);
        if (!string.Equals(selectedKey, lessonKey, StringComparison.Ordinal)) return;
        _historyQuestions = await _repository.GetAiQuestionsAsync(lessonKey);
        RenderHistoryTranscript();
    }

    private string BuildSlideContext(string question, string? selectedText)
    {
        if (_slideDocument is null) return "（没有课件）";
        var query = $"{question} {selectedText}";
        var pages = _slideDocument.Pages
            .Select(page => new { Page = page, Score = ScoreRelevance($"{page.Title} {page.Text} {page.Notes}", query) })
            .Where(item => Math.Abs(item.Page.PageNumber - _currentSlidePage) <= 1 || item.Score > 0)
            .OrderByDescending(item => item.Page.PageNumber == _currentSlidePage)
            .ThenByDescending(item => item.Score)
            .Take(6)
            .Select(item => $"[PPT第{item.Page.PageNumber}页] {item.Page.Title}\n{item.Page.Text}\n备注：{item.Page.Notes}");
        return string.Join("\n\n", pages);
    }

    private static string BuildTranscriptContext(
        IReadOnlyList<TranscriptSegment> transcripts,
        string question,
        string? selectedText)
    {
        var query = $"{question} {selectedText}";
        var relevant = transcripts.Where(item => item.IsFinal)
            .Select(item => new { Item = item, Score = ScoreRelevance($"{item.SourceText} {item.TargetText ?? item.ChineseText}", query) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Start)
            .Take(10)
            .Select(item => item.Item)
            .Concat(transcripts.Where(item => item.IsFinal).OrderByDescending(item => item.Start).Take(8))
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Start)
            .Select(item => $"[{FormatTimestamp(item.Start)}] {item.SourceText} → {item.TargetText ?? item.ChineseText}");
        return string.Join("\n", relevant);
    }

    private static int ScoreRelevance(string text, string query)
    {
        var terms = System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), "[a-z0-9]{2,}|[\\p{IsCJKUnifiedIdeographs}\\p{IsHiragana}\\p{IsKatakana}]{2,}")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return terms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatTimestamp(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private void ToggleCourseSidebar_Click(object sender, RoutedEventArgs e)
    {
        var collapsing = CourseSidebar.Visibility == Visibility.Visible;
        CourseSidebar.Visibility = collapsing ? Visibility.Collapsed : Visibility.Visible;
        CourseSidebarColumn.Width = collapsing ? new GridLength(38) : new GridLength(275);
        OpenSidebarButton.Visibility = collapsing ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool CanChangeCourse()
    {
        if (_classCancellation is null) return true;
        StatusText.Text = "请先停止当前课堂，再切换或管理课程";
        return false;
    }

    private async void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = QwenEndpoint.Singapore(WorkspaceIdBox.Text.Trim());
            var typedKey = ApiKeyBox.Password.Trim();
            var savedKey = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
            if (!string.IsNullOrWhiteSpace(typedKey))
            {
                await _secretStore.SaveAsync(CredentialTargets.QwenApiKey, typedKey);
            }
            else if (string.IsNullOrWhiteSpace(savedKey))
            {
                throw new InvalidOperationException("请填写 API Key；Workspace ID 已恢复，但密钥需要重新保存一次。");
            }
            await _settingsStore.SaveAsync(new AppSettings
            {
                WorkspaceId = WorkspaceIdBox.Text.Trim(),
                TranslationDirectionId = SelectedDirection.Id,
                ClassroomServerUrl = _classroomServerUrl
            });
            ApiKeyBox.Clear();
            KeyStatusText.Text = "Key 已安全保存到 Windows 凭据库。";
            StatusText.Text = "千问凭据已配置";
        }
        catch (Exception exception)
        {
            KeyStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private async void StartClassButton_Click(object sender, RoutedEventArgs e)
    {
        if (_classCancellation is not null)
        {
            _classCancellation.Cancel();
            return;
        }

        if (_selectedCourse is null || _selectedCourse.IsArchived)
        {
            StatusText.Text = "请先在左侧选择或新建一门课程";
            return;
        }
        ExitHistoryView();

        var apiKey = await _secretStore.ReadAsync(CredentialTargets.QwenApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText.Text = "请先保存千问 API Key";
            return;
        }

        if (MicrophoneBox.SelectedIndex < 0)
        {
            StatusText.Text = "请选择麦克风";
            return;
        }

        Uri endpoint;
        try
        {
            endpoint = QwenEndpoint.Singapore(WorkspaceIdBox.Text.Trim());
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        _classCancellation = new CancellationTokenSource();
        SettingsDrawer.Visibility = Visibility.Collapsed;
        SettingsButton.Content = "设置";
        var cancellation = _classCancellation;
        var sessionId = Guid.NewGuid();
        var direction = SelectedDirection;
        var startedAt = DateTimeOffset.Now;
        var courseName = _selectedCourse.Name;
        var repository = _repository;
        var priorSessions = await repository.GetSessionsForCourseAsync(_selectedCourse.Id, cancellation.Token);
        var lessonNumber = _resumeLessonNumber ?? LessonRecord.Build(priorSessions, true).Count + 1;
        var courseDirectory = GetCourseDirectory(_selectedCourse);
        _activeLessonDirectory = _resumeLessonDirectory ?? Path.Combine(courseDirectory, $"第{lessonNumber:D2}节课-{startedAt:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(_activeLessonDirectory);
        if (!string.IsNullOrWhiteSpace(_materialPath) && File.Exists(_materialPath))
        {
            _materialPath = CopyMaterialIntoLesson(_materialPath, _activeLessonDirectory);
        }
        var liveSession = new Session(sessionId, courseName, startedAt, null, SessionStatus.Live)
        {
            CourseId = _selectedCourse.Id,
            MaterialPath = _materialPath,
            MaterialType = _materialPath is null ? null : Path.GetExtension(_materialPath).TrimStart('.').ToLowerInvariant(),
            LessonKey = _resumeLessonKey ?? sessionId.ToString("N"),
            LastSlidePage = _slideDocument is null ? null : (_resumeSlidePage ?? _currentSlidePage)
        };
        _activeSession = liveSession;
        await repository.UpsertSessionAsync(liveSession, cancellation.Token);
        _resumeLessonKey = null;
        _resumeLessonNumber = null;
        _resumeLessonDirectory = null;
        _resumeSlidePage = null;
        var audioPath = Path.Combine(_activeLessonDirectory, $"课堂录音-{startedAt:yyyyMMdd-HHmmss}.wav");

        StartClassButton.Content = "停止课堂";
        MicrophoneBox.IsEnabled = false;
        TranslationDirectionBox.IsEnabled = false;
        SaveKeyButton.IsEnabled = false;
        StatusText.Text = "正在连接千问实时识别……";
        _confirmedTranslations.Clear();
        _stableSlideTranscriptWindow.Clear();
        _pendingSlideCandidate = null;
        _pendingSlideSegmentId = null;
        ApplySlideSuggestionButton.IsEnabled = false;
        IgnoreSlideSuggestionButton.IsEnabled = false;
        if (_resumeConfirmedTranslations.Count > 0)
            _confirmedTranslations.AddRange(_resumeConfirmedTranslations);
        _resumeConfirmedTranslations = [];
        ConfirmedTranslationText.Text = string.Join("\n\n", _confirmedTranslations);
        TranslationStatusText.Text = string.Empty;
        ChineseSubtitleText.Text = "正在聆听……";
        SourceSubtitleText.Text = string.Empty;
        Task? audioPump = null;

        try
        {
            var translationEndpoint = QwenEndpoint.SingaporeTranslation(WorkspaceIdBox.Text.Trim());
            var primaryDirection = direction == TranslationDirection.JapaneseChineseBidirectional
                ? TranslationDirection.JapaneseToChinese
                : direction == TranslationDirection.EnglishChineseBidirectional
                    ? TranslationDirection.EnglishToChinese
                    : direction;
            var reverseDirection = direction == TranslationDirection.JapaneseChineseBidirectional
                ? TranslationDirection.ChineseToJapanese
                : direction == TranslationDirection.EnglishChineseBidirectional
                    ? TranslationDirection.ChineseToEnglish
                    : direction;
            using var translator = new QwenMtTranslator(translationEndpoint, apiKey, primaryDirection);
            using var reverseTranslator = new QwenMtTranslator(translationEndpoint, apiKey, reverseDirection);
            var hub = new LiveAudioSession(
                new MicrophoneAudioSource(),
                MicrophoneBox.SelectedIndex,
                audioPath,
                ClassInterpreter.Core.Audio.AudioFormat.ClassroomDefault);
            hub.LevelChanged += level => Dispatcher.BeginInvoke(() => AudioLevelBar.Value = level);
            audioPump = hub.PumpAsync(cancellation.Token);
            var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
            CancellationTokenSource? interimTranslation = null;
            long nextSequence = 0;
            var reconnectAttempt = 0;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var recognizer = new QwenRealtimeAsrClient(endpoint, apiKey, direction.AsrLanguage);
                    await foreach (var recognition in recognizer.RecognizeAsync(hub.ReadAllAsync(cancellation.Token), cancellation.Token))
                    {
                        var recognizedText = RecognitionTextNormalizer.Sanitize(recognition.Text);
                        if (string.IsNullOrWhiteSpace(recognizedText)) continue;
                        var renderVersion = Interlocked.Increment(ref _subtitleRenderVersion);
                        reconnectAttempt = 0;
                        StatusText.Text = recognition.IsFinal ? "实时识别正常" : "正在识别……";
                        SourceSubtitleText.Text = CompactLiveSource(recognizedText);
                        LanguageStatusText.Text = $"语言：{recognition.Language.ToUpperInvariant()} · {(recognition.IsFinal ? "稳定" : "临时")}";
                        string? targetText = null;
                        var actualDirection = BidirectionalTranslationRouter.Resolve(direction, recognition.Language, recognizedText);
                        var activeTranslator = actualDirection?.TargetLanguage == "Chinese" ? translator : reverseTranslator;
                        ConfigureTranslationContext(activeTranslator);
                        var outputPrefix = direction.IsBidirectional
                            ? actualDirection?.TargetLanguage == "Chinese" ? "给我：" : "给对方："
                            : string.Empty;
                        interimTranslation?.Cancel();
                        interimTranslation?.Dispose();
                        interimTranslation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        if (recognition.IsFinal)
                        {
                            if (actualDirection is null)
                            {
                                TranslationStatusText.Text = "当前模式未翻译这一段语音；原文已保留。";
                            }
                            else try
                            {
                                targetText = await TranslateTrackedAsync(activeTranslator, recognizedText, true, cancellation.Token);
                                AppendConfirmedTranslation($"{outputPrefix}{targetText}");
                                ChineseSubtitleText.Text = "正在聆听……";
                                TranslationStatusText.Text = string.Empty;
                                if (direction.IsBidirectional)
                                    LanguageStatusText.Text = $"{actualDirection.DisplayName} · 稳定";
                            }
                            catch (Exception translationException) when (translationException is QwenProviderException or HttpRequestException)
                            {
                                TranslationStatusText.Text = FriendlyTranslationError(translationException);
                                ChineseSubtitleText.Text = "正在聆听……";
                            }

                        }
                        else if (actualDirection is not null)
                        {
                            _ = TranslateInterimAsync(activeTranslator, recognizedText, interimTranslation.Token, renderVersion, outputPrefix);
                        }

                        if (!sequences.TryGetValue(recognition.SegmentId, out var sequence))
                        {
                            sequence = ++nextSequence;
                            sequences[recognition.SegmentId] = sequence;
                        }

                        var segmentId = TranscriptIdentity.CreateSegmentId(sessionId, recognition.SegmentId);
                        var slideLink = recognition.IsFinal
                            ? await LinkFinalSubtitleToSlideAsync(recognizedText, segmentId, cancellation.Token)
                            : new SlideLink(
                                _slideDocument is null ? null : _currentSlidePage,
                                null, null, null, SlideFollowAction.None);

                        await repository.UpsertTranscriptAsync(new TranscriptSegment(
                            segmentId, sessionId, sequence,
                            recognition.AudioPosition, recognition.AudioPosition,
                            recognizedText,
                            actualDirection?.TargetLanguage == "Chinese" ? targetText : null,
                            recognition.IsFinal, recognition.Language, null)
                        {
                            TargetText = targetText,
                            TranslationDirectionId = actualDirection?.Id ?? direction.Id,
                            ViewedSlidePage = slideLink.ViewedPage,
                            CandidateSlidePage = slideLink.CandidatePage,
                            SlideMatchConfidence = slideLink.Confidence,
                            SlideMatchEvidence = slideLink.Evidence,
                            SlideFollowAction = slideLink.Action
                        }, cancellation.Token);
                    }

                    if (audioPump.IsCompleted)
                    {
                        break;
                    }
                }
                catch (QwenProviderException exception) when (!IsRetryable(exception))
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is WebSocketException or HttpRequestException or IOException or QwenProviderException)
                {
                    reconnectAttempt++;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(reconnectAttempt, 5))));
                    StatusText.Text = $"连接失败：{exception.Message}；本地录音继续，{delay.TotalSeconds:0} 秒后重连……";
                    await Task.Delay(delay, cancellation.Token);
                }
            }

            interimTranslation?.Cancel();
            interimTranslation?.Dispose();
            await audioPump;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "课堂已停止";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"课堂中断：{exception.Message}";
        }
        finally
        {
            cancellation.Cancel();
            try
            {
            if (audioPump is not null)
            {
                try
                {
                    await audioPump.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception audioException)
                {
                    StatusText.Text = $"录音结束时出现错误：{audioException.Message}";
                }
            }
            var completedSession = (_activeSession ?? liveSession) with { EndedAt = DateTimeOffset.Now, Status = SessionStatus.Completed };
            await repository.RecordAiUsageAsync(new AiUsageRecord(
                DateOnly.FromDateTime(DateTime.Now), AiUsageKind.SpeechRecognition, QwenAsrProtocol.Model,
                1, 0, 0, 0, 0, 0,
                Math.Max(0, (long)(completedSession.EndedAt.Value - completedSession.StartedAt).TotalMilliseconds)));
            await repository.UpsertSessionAsync(completedSession);
            var completedTranscripts = await GetCompleteLessonTranscriptsAsync(repository, completedSession);
            var lessonKey = completedSession.LessonKey ?? completedSession.Id.ToString("N");
            var questions = await repository.GetAiQuestionsAsync(lessonKey);
            await WriteLessonTranscriptFilesAsync(_activeLessonDirectory, completedTranscripts);
            if (!string.IsNullOrWhiteSpace(_activeLessonDirectory))
                await LessonAiBundleWriter.WriteAsync(_activeLessonDirectory, completedSession, lessonKey, _slideDocument, completedTranscripts, questions);
            // The raw transcript and lesson bundle are now safe.  Do not make stopping class
            // wait for a remote model: generation continues in the background.
            if (Interlocked.CompareExchange(ref _studyPackGenerationRunning, 1, 0) == 0)
            {
                StatusText.Text = "课堂已保存；学习总结正在后台生成。";
                _ = CompleteStudyPackGenerationAsync(
                    completedSession, apiKey, WorkspaceIdBox.Text.Trim(), _activeLessonDirectory ?? string.Empty,
                    completedTranscripts, _slideDocument, questions, [completedSession], lessonKey);
            }
            }
            catch (Exception exception)
            {
                StatusText.Text = $"课堂已保存；学习包将在稍后重新生成：{exception.Message}";
            }
            finally
            {
            cancellation.Dispose();
            _classCancellation = null;
            _activeSession = null;
            _activeLessonDirectory = null;
            AudioLevelBar.Value = 0;
            MicrophoneBox.IsEnabled = true;
            TranslationDirectionBox.IsEnabled = true;
            SaveKeyButton.IsEnabled = true;
            StartClassButton.IsEnabled = _selectedCourse is not null && !_selectedCourse.IsArchived;
            StartClassButton.Content = "开始课堂";
            await RefreshCoursesAsync(_selectedCourse?.Id);
            }
        }
    }

    private TranslationDirection SelectedDirection =>
        TranslationDirectionBox.SelectedItem as TranslationDirection ?? TranslationDirection.MixedToChinese;

    private async void TranslationDirectionBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var direction = SelectedDirection;
        ApplyDirectionLabels(direction);
        SlideImportStatusText.Text = _slideDocument is null
            ? "尚未导入课件。"
            : $"课件已保留，共 {_slideDocument.Pages.Count} 页；请手动翻页或输入页码跳转。";
        try
        {
            await _settingsStore.SaveAsync(new AppSettings
            {
                WorkspaceId = WorkspaceIdBox.Text.Trim(),
                TranslationDirectionId = direction.Id,
                ClassroomServerUrl = _classroomServerUrl
            });
        }
        catch
        {
            // A settings write failure must not prevent direction selection in the current run.
        }
    }

    private void ApplyDirectionLabels(TranslationDirection direction)
    {
        OutputSubtitleLabel.Text = direction.OutputLabel;
        SourceSubtitleLabel.Text = direction.SourceLabel;
        ChineseSubtitleText.Text = "等待开始课堂……";
        ConfirmedTranslationText.Text = string.Empty;
        TranslationStatusText.Text = string.Empty;
    }

    private async void DemoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_classCancellation is not null)
        {
            StatusText.Text = "请先停止当前课堂。";
            return;
        }
        if (_selectedCourse is null) { StatusText.Text = "请先新建或选择一门课程"; return; }

        DemoButton.IsEnabled = false;
        StartClassButton.IsEnabled = false;
        ImportSlidesButton.IsEnabled = false;
        var scenario = DemoScenario.Create();
        var direction = SelectedDirection;
        _slideDocument = scenario.Slides;
        _currentSlidePage = 1;
        ShowSlide(_slideDocument.Pages[0]);
        SlideImportStatusText.Text = $"正在运行内置{direction.DisplayName}演示……";
        StatusText.Text = "演示运行中";
        CourseNameBox.Text = _selectedCourse.Name;

        var session = new Session(
            Guid.NewGuid(),
            CourseNameBox.Text,
            DateTimeOffset.Now,
            null,
            SessionStatus.Live);
        session = session with { CourseId = _selectedCourse.Id };
        var repository = _repository;
        await repository.UpsertSessionAsync(session);

        try
        {
            long sequence = 0;
            foreach (var utterance in scenario.Utterances)
            {
                await Task.Delay(850);
                var sourceText = utterance.SourceFor(direction);
                var targetText = utterance.TargetFor(direction);
                SourceSubtitleText.Text = sourceText;
                AppendConfirmedTranslation(targetText);
                ChineseSubtitleText.Text = "正在聆听……";
                LanguageStatusText.Text = $"语言：{(direction == TranslationDirection.MixedToChinese ? utterance.Language.ToUpperInvariant() : "ZH")} · 稳定 · 演示";
                SlideImportStatusText.Text = "演示字幕播放中 · 课件请手动翻页";
                await repository.UpsertTranscriptAsync(new TranscriptSegment(
                    Guid.NewGuid(), session.Id, ++sequence,
                    utterance.At, utterance.At + TimeSpan.FromSeconds(2),
                    sourceText, direction.TargetLanguage == "Chinese" ? targetText : null,
                    true, direction.TargetLanguage == "Chinese" ? utterance.Language : "zh", 1)
                {
                    TargetText = targetText,
                    TranslationDirectionId = direction.Id
                });
            }

            var completed = session with { EndedAt = DateTimeOffset.Now, Status = SessionStatus.Completed };
            await repository.UpsertSessionAsync(completed);
            var transcripts = await repository.GetTranscriptsAsync(session.Id);
            var output = Path.Combine(
                _paths.ExportDirectory,
                "演示课堂",
                session.StartedAt.ToString("yyyyMMdd-HHmmss"),
                "学习包.md");
            await MarkdownStudyPackWriter.WriteAsync(output, completed, scenario.AnalysisMarkdown, transcripts);
            await repository.UpsertSessionAsync(completed with { StudyPackPath = output });
            StatusText.Text = $"演示完成，学习包已保存：{output}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"演示失败：{exception.Message}";
        }
        finally
        {
            DemoButton.IsEnabled = true;
            StartClassButton.IsEnabled = true;
            ImportSlidesButton.IsEnabled = true;
            await RefreshCoursesAsync(_selectedCourse?.Id);
        }
    }

    private async Task<string?> GenerateStudyPackAsync(
        SqliteTimelineRepository repository,
        Session session,
        string apiKey,
        string workspaceId,
        string? lessonDirectory = null,
        IReadOnlyList<TranscriptSegment>? lessonTranscripts = null,
        SlideDocument? slides = null,
        IReadOnlyList<AiQuestionRecord>? questions = null)
    {
        var transcripts = lessonTranscripts ?? await repository.GetTranscriptsAsync(session.Id);
        if (transcripts.Count == 0)
        {
            return null;
        }

        StatusText.Text = "千问正在阅读整节课课件、字幕和问答并生成学习总结……";
        questions ??= string.IsNullOrWhiteSpace(session.LessonKey)
            ? []
            : await repository.GetAiQuestionsAsync(session.LessonKey);
        var modelInput = LessonAiBundleWriter.RenderForModel(session, slides, transcripts, questions);
        string analysis;
        using var analyzer = new QwenStudyPackAnalyzer(QwenEndpoint.SingaporeTranslation(workspaceId), apiKey);
        try
        {
            analysis = await analyzer.AnalyzeBundleAsync(modelInput);
        }
        catch (Exception exception)
        {
            analysis = $"## 自动分析暂未生成\n\n原因：{exception.Message}\n\n逐字稿仍已完整保留，可稍后重新分析。";
        }
        await repository.RecordAiUsageAsync(new AiUsageRecord(
            DateOnly.FromDateTime(DateTime.Now), AiUsageKind.StudyPack, QwenStudyPackProtocol.Model,
            analyzer.RequestCount, analyzer.FailureCount, analyzer.InputCharacters, analyzer.OutputCharacters,
            analyzer.EstimatedInputTokens, analyzer.EstimatedOutputTokens, 0));

        var safeCourse = string.Concat(session.CourseName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var directory = lessonDirectory ?? Path.Combine(_paths.ExportDirectory, safeCourse, session.StartedAt.ToString("yyyyMMdd-HHmmss"));
        var output = Path.Combine(directory, "学习包.md");
        await MarkdownStudyPackWriter.WriteAsync(output, session, analysis, transcripts);
        StatusText.Text = $"学习包已保存：{output}";
        return output;
    }

    private static async Task<IReadOnlyList<TranscriptSegment>> GetCompleteLessonTranscriptsAsync(
        SqliteTimelineRepository repository,
        Session completedSession)
    {
        if (completedSession.CourseId is null || string.IsNullOrWhiteSpace(completedSession.LessonKey))
        {
            return await repository.GetTranscriptsAsync(completedSession.Id);
        }

        var sessions = (await repository.GetSessionsForCourseAsync(completedSession.CourseId.Value))
            .Where(session => string.Equals(session.LessonKey, completedSession.LessonKey, StringComparison.Ordinal))
            .OrderBy(session => session.StartedAt)
            .ToArray();
        var batches = new List<(Session Session, IReadOnlyList<TranscriptSegment> Segments)>();
        foreach (var session in sessions)
        {
            var items = await repository.GetTranscriptsAsync(session.Id);
            batches.Add((session, items));
        }
        return LessonTranscriptTimeline.Combine(batches);
    }

    private static string CopyMaterialIntoLesson(string sourcePath, string lessonDirectory)
    {
        var materialDirectory = Path.Combine(lessonDirectory, "课件");
        Directory.CreateDirectory(materialDirectory);
        var destination = Path.Combine(materialDirectory, Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, destination, true);
        }
        return destination;
    }

    private string GetCourseDirectory(Course course)
    {
        var safeName = string.Concat(course.Name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (safeName.Length == 0) safeName = "未命名课程";
        return Path.Combine(_paths.CourseMaterialDirectory, $"{safeName}-{course.Id.ToString("N")[..8]}");
    }

    private async Task EnsureLessonArchiveAsync(
        LessonRecord lesson,
        string lessonKey,
        IReadOnlyList<TranscriptSegment> transcripts,
        IReadOnlyList<AiQuestionRecord> questions,
        SlideDocument? slides)
    {
        if (_selectedCourse is null) return;
        var directory = GetLessonDirectory(lesson);
        Directory.CreateDirectory(directory);
        await WriteLessonTranscriptFilesAsync(directory, transcripts);
        var representative = lesson.Sessions.OrderBy(item => item.StartedAt).First();
        await LessonAiBundleWriter.WriteAsync(directory, representative, lessonKey, slides, transcripts, questions);
        if (!string.IsNullOrWhiteSpace(lesson.MaterialPath) && File.Exists(lesson.MaterialPath))
        {
            CopyMaterialIntoLesson(lesson.MaterialPath, directory);
        }
        if (!string.IsNullOrWhiteSpace(lesson.StudyPackPath) && File.Exists(lesson.StudyPackPath))
        {
            var studyDestination = Path.Combine(directory, "学习包.md");
            if (!string.Equals(Path.GetFullPath(lesson.StudyPackPath), Path.GetFullPath(studyDestination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(lesson.StudyPackPath, studyDestination, true);
            }
        }
    }

    private async Task<string> EnsureLessonKeyAsync(LessonRecord lesson)
    {
        var key = lesson.Sessions.Select(item => item.LessonKey).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                  ?? lesson.Sessions.OrderBy(item => item.StartedAt).First().Id.ToString("N");
        foreach (var session in lesson.Sessions.Where(item => !string.Equals(item.LessonKey, key, StringComparison.Ordinal)))
        {
            await _repository.UpsertSessionAsync(session with { LessonKey = key });
        }
        return key;
    }

    private string GetLessonDirectory(LessonRecord lesson)
    {
        if (_selectedCourse is null) throw new InvalidOperationException("请先选择课程");
        return Path.Combine(
            GetCourseDirectory(_selectedCourse),
            $"第{lesson.LessonNumber:D2}节课-{lesson.StartedAt:yyyyMMdd-HHmmss}");
    }

    private static async Task WriteLessonTranscriptFilesAsync(
        string? lessonDirectory,
        IReadOnlyList<TranscriptSegment> transcripts)
    {
        if (string.IsNullOrWhiteSpace(lessonDirectory)) return;
        Directory.CreateDirectory(lessonDirectory);
        var source = TranscriptHistoryFormatter.Format(transcripts, true, true);
        var translation = TranscriptHistoryFormatter.Format(transcripts, false, true);
        var raw = $"【翻译字幕】\r\n\r\n{TranscriptHistoryFormatter.Format(transcripts, false, false)}" +
                  $"\r\n\r\n【识别原文】\r\n\r\n{TranscriptHistoryFormatter.Format(transcripts, true, false)}";
        await File.WriteAllTextAsync(Path.Combine(lessonDirectory, "听到的原文.txt"), source, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(lessonDirectory, "翻译字幕.txt"), translation, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(lessonDirectory, "逐条字幕.txt"), raw, Encoding.UTF8);
    }

    private async void ImportSlidesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择课堂 PPTX 或 PDF",
            Filter = "课堂课件 (*.pptx;*.pdf)|*.pptx;*.pdf|PowerPoint (*.pptx)|*.pptx|PDF (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (_selectedCourse is null) { SlideImportStatusText.Text = "请先选择课程"; return; }

        try
        {
            var courseDirectory = GetCourseDirectory(_selectedCourse);
            var materialDirectory = _activeLessonDirectory is null
                ? Path.Combine(courseDirectory, "待绑定课件")
                : Path.Combine(_activeLessonDirectory, "课件");
            Directory.CreateDirectory(materialDirectory);
            var storedPath = Path.Combine(materialDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(dialog.FileName)}");
            File.Copy(dialog.FileName, storedPath, true);
            _materialPath = storedPath;
            if (_activeSession is not null)
            {
                _activeSession = _activeSession with
                {
                    MaterialPath = storedPath,
                    MaterialType = Path.GetExtension(storedPath).TrimStart('.').ToLowerInvariant()
                };
                await _repository.UpsertSessionAsync(_activeSession);
            }
            await LoadSlidesAsync(storedPath, true);
        }
        catch (Exception exception)
        {
            SlideImportStatusText.Text = $"课件导入失败：{exception.Message}";
        }
    }

    private async Task LoadSlidesAsync(string fileName, bool announceCopy, int? historyLoadVersion = null, int? initialPage = null)
    {
        try
        {
            var isPptx = string.Equals(Path.GetExtension(fileName), ".pptx", StringComparison.OrdinalIgnoreCase);
            var previewWarning = string.Empty;
            SlideImportStatusText.Text = "正在读取课件文本……";
            // 大 PDF/PPTX 的文本提取可能要好几秒，放到线程池，避免冻结整个课堂界面。
            var slideDocument = await Task.Run(() => isPptx
                ? new PptxSlideExtractor().Extract(fileName)
                : new PdfSlideExtractor().Extract(fileName));
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
            : LoadSlideBitmap(page.ThumbnailPath);
        CurrentSlideBodyText.Visibility = CurrentSlideImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        PreviousSlideButton.IsEnabled = _slideDocument is not null && page.PageNumber > 1;
        NextSlideButton.IsEnabled = _slideDocument is not null && page.PageNumber < _slideDocument.Pages.Count;
        JumpPageButton.IsEnabled = _slideDocument is not null;
        JumpPageBox.IsEnabled = _slideDocument is not null;
        JumpPageBox.Text = page.PageNumber.ToString();
    }

    private static BitmapImage? LoadSlideBitmap(string thumbnailPath)
    {
        try
        {
            // OnLoad 立即读完并关闭文件，翻页时不再持有旧图片的文件句柄；Freeze 允许跨线程安全使用。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(thumbnailPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
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
