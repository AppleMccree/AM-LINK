using ClassInterpreter.Core.Audio;

namespace ClassInterpreter.Core.Speech;

public interface IStreamingRecognizer
{
    IAsyncEnumerable<RecognitionEvent> RecognizeAsync(
        IAsyncEnumerable<AudioFrame> audioFrames,
        CancellationToken cancellationToken = default);
}
