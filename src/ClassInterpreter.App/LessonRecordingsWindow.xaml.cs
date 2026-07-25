using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace ClassInterpreter.App;

public sealed record LessonRecordingItem(string Path, DateTimeOffset StartedAt, string DisplayName)
{
    public static LessonRecordingItem From(string path, DateTimeOffset startedAt)
    {
        var size = new FileInfo(path).Length / 1024d / 1024d;
        return new(path, startedAt, $"{startedAt:yyyy-MM-dd HH:mm:ss}　{System.IO.Path.GetFileName(path)}　{size:0.0} MB");
    }
}

public partial class LessonRecordingsWindow : Window
{
    public LessonRecordingsWindow(string title, IReadOnlyList<LessonRecordingItem> recordings)
    {
        InitializeComponent();
        TitleText.Text = title;
        RecordingList.ItemsSource = recordings;
        if (recordings.Count > 0) RecordingList.SelectedIndex = 0;
    }

    private LessonRecordingItem? Selected => RecordingList.SelectedItem as LessonRecordingItem;

    private void PlayButton_Click(object sender, RoutedEventArgs e) => OpenSelected(false);
    private void OpenLocationButton_Click(object sender, RoutedEventArgs e) => OpenSelected(true);
    private void RecordingList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(false);

    private void OpenSelected(bool reveal)
    {
        var selected = Selected;
        if (selected is null || !File.Exists(selected.Path)) return;
        if (reveal)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected.Path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(selected.Path) { UseShellExecute = true });
    }
}
