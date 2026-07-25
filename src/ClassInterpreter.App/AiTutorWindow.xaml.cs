using System.Windows;
using System.Windows.Input;
using ClassInterpreter.Core.Learning;

namespace ClassInterpreter.App;

public partial class AiTutorWindow : Window
{
    private readonly Func<string, AiQuestionRecord?, Task<AiQuestionRecord>> _ask;
    private AiQuestionRecord? _lastRecord;
    private bool _busy;

    public AiTutorWindow(
        string contextPreview,
        string? initialQuestion,
        Func<string, AiQuestionRecord?, Task<AiQuestionRecord>> ask)
    {
        InitializeComponent();
        ContextPreviewText.Text = string.IsNullOrWhiteSpace(contextPreview) ? "将参考当前课堂和课件" : $"参考：{contextPreview}";
        QuestionBox.Text = initialQuestion?.Trim() ?? string.Empty;
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
        _ask = ask;
        Loaded += (_, _) => QuestionBox.Focus();
    }

    public void SetQuestion(string question)
    {
        QuestionBox.Text = question.Trim();
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
        QuestionBox.Focus();
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e) => await AskAsync(false);

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await AskAsync(true);

    private async Task AskAsync(bool retry)
    {
        if (_busy) return;
        var question = retry ? _lastRecord?.Question : QuestionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            StatusText.Text = "请输入问题";
            return;
        }

        _busy = true;
        AskButton.IsEnabled = false;
        QuestionBox.IsEnabled = false;
        RetryButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        AnswerBox.Text = "AI正在结合本节课字幕和课件思考……";
        StatusText.Text = "询问不会暂停课堂同传";
        try
        {
            // A provider/network stall must not leave this non-modal window looking frozen.
            _lastRecord = await _ask(question, retry ? _lastRecord : null).WaitAsync(TimeSpan.FromSeconds(45));
            if (_lastRecord.Status == AiQuestionStatus.Completed)
            {
                AnswerBox.Text = _lastRecord.Answer ?? "AI没有返回内容。";
                StatusText.Text = "已保存到本节课问AI记录";
            }
            else
            {
                AnswerBox.Text = $"暂时无法回答：{_lastRecord.Error}";
                StatusText.Text = "问题已保存，可以重新询问";
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            AnswerBox.Text = $"暂时无法回答：{exception.Message}";
            StatusText.Text = "可以重新询问";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            AskButton.IsEnabled = true;
            QuestionBox.IsEnabled = true;
            RetryButton.IsEnabled = true;
        }
    }

    private void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AskButton_Click(sender, e);
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
