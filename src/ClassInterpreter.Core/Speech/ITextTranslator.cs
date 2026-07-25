namespace ClassInterpreter.Core.Speech;

public interface ITextTranslator
{
    ValueTask<string> TranslateAsync(string sourceText, bool isFinal, CancellationToken cancellationToken = default);
}
