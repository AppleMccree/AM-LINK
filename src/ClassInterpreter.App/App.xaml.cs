using System.Windows;
using System.IO;
using ClassInterpreter.Core.Configuration;
using ClassInterpreter.Infrastructure.Retention;
using ClassInterpreter.Infrastructure.Timeline;
using ClassInterpreter.Core.Demo;
using ClassInterpreter.Infrastructure.Demo;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClassInterpreter.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Create(AppRootResolver.ResolveDefault());
        paths.EnsureDirectories();
        if (e.Args.Any(argument => string.Equals(argument, "--headless-demo", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = Path.Combine(paths.Root, "data", "demo-last-run.txt");
            try
            {
                var result = await new DemoRunService(paths).RunAsync(DemoScenario.Create());
                await File.WriteAllTextAsync(marker, result.MarkdownPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Shutdown(0);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(marker, $"ERROR: {exception}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Shutdown(1);
            }

            return;
        }

        try
        {
            var repository = new SqliteTimelineRepository(Path.Combine(paths.DatabaseDirectory, "timeline.db"));
            await repository.InitializeAsync();
            await repository.MarkOpenSessionsInterruptedAsync(DateTimeOffset.Now);
            await new AudioRetentionService(paths, TimeSpan.FromDays(14), TimeSpan.FromHours(24))
                .SweepAsync(DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show($"启动恢复任务失败，但应用仍可打开：{exception.Message}", "课堂同传助手", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        var window = new MainWindow();
        window.Show();
        if (e.Args.Any(argument => string.Equals(argument, "--render-bidirectional-ui", StringComparison.OrdinalIgnoreCase)))
        {
            await window.InitializationCompleted;
            var dialog = new QuickTranslatorWindow("ws-preview", "preview-key", 0, paths.ExportDirectory) { Owner = window };
            dialog.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(350);
            dialog.UpdateLayout();
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(dialog.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(dialog.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var previewPath = Path.Combine(paths.Root, "data", "bidirectional-ui-preview.png");
            await using (var stream = File.Create(previewPath)) encoder.Save(stream);
            dialog.Close();
            window.Close();
            Shutdown(0);
            return;
        }
        if (e.Args.Any(argument => string.Equals(argument, "--render-ui", StringComparison.OrdinalIgnoreCase)))
        {
            await window.InitializationCompleted;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var previewPath = Path.Combine(paths.Root, "data", "ui-preview.png");
            await using (var stream = File.Create(previewPath))
            {
                encoder.Save(stream);
            }

            window.Close();
            Shutdown(0);
        }
    }
}
