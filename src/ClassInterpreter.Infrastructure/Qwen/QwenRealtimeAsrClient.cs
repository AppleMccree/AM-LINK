using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using ClassInterpreter.Core.Audio;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Infrastructure.Qwen;

public sealed class QwenRealtimeAsrClient(Uri endpoint, string apiKey, string? sourceLanguage = null, int silenceDurationMs = 1200) : IStreamingRecognizer
{
    public async IAsyncEnumerable<RecognitionEvent> RecognizeAsync(
        IAsyncEnumerable<AudioFrame> audioFrames,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        await socket.ConnectAsync(endpoint, cancellationToken);
        await SendTextAsync(socket, QwenAsrProtocol.CreateSessionUpdate(AudioFormat.ClassroomDefault, sourceLanguage, silenceDurationMs), cancellationToken);

        long latestAudioTicks = 0;
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sender = SendAudioAsync(socket, audioFrames, value => Interlocked.Exchange(ref latestAudioTicks, value), connectionCancellation.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, cancellationToken);
                if (message is null)
                {
                    break;
                }

                var providerEvent = QwenAsrProtocol.ParseServerEvent(
                    message,
                    TimeSpan.FromTicks(Interlocked.Read(ref latestAudioTicks)));
                if (providerEvent is RecognitionEvent recognition)
                {
                    yield return recognition;
                }
                else if (providerEvent is SpeechSessionEvent { Type: "session.finished" })
                {
                    break;
                }
            }

            await sender;
        }
        finally
        {
            connectionCancellation.Cancel();
            try
            {
                await sender;
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException) when (socket.State != WebSocketState.Open)
            {
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client finished", CancellationToken.None);
            }
        }
    }

    private static async Task SendAudioAsync(
        ClientWebSocket socket,
        IAsyncEnumerable<AudioFrame> audioFrames,
        Action<long> updatePosition,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in audioFrames.WithCancellation(cancellationToken))
        {
            updatePosition(frame.Timestamp.Ticks);
            var message = QwenAsrProtocol.CreateAudioAppend(frame.Pcm.ToArray());
            await SendTextAsync(socket, message, cancellationToken);
        }

        if (socket.State == WebSocketState.Open)
        {
            await SendTextAsync(socket, QwenAsrProtocol.CreateSessionFinish(), cancellationToken);
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async ValueTask<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }
}
