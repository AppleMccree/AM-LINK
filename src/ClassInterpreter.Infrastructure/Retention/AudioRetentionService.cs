using System.Globalization;
using ClassInterpreter.Core.Configuration;

namespace ClassInterpreter.Infrastructure.Retention;

public sealed class AudioRetentionService(
    AppPaths paths,
    TimeSpan activeRetention,
    TimeSpan trashGracePeriod)
{
    public async ValueTask SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        MoveExpiredAudioToTrash(now, cancellationToken);
        await DeleteExpiredTrashAsync(now, cancellationToken);
    }

    private void MoveExpiredAudioToTrash(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var audioPath in Directory.EnumerateFiles(paths.AudioDirectory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(audioPath + ".keep"))
            {
                continue;
            }

            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(audioPath), TimeSpan.Zero);
            if (now - lastWrite < activeRetention)
            {
                continue;
            }

            var fileName = $"{Path.GetFileNameWithoutExtension(audioPath)}-{Guid.NewGuid():N}{Path.GetExtension(audioPath)}";
            var trashPath = Path.Combine(paths.TrashDirectory, fileName);
            File.Move(audioPath, trashPath);
            File.WriteAllText(MetadataPath(trashPath), now.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private async ValueTask DeleteExpiredTrashAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var trashPath in Directory.EnumerateFiles(paths.TrashDirectory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataPath = MetadataPath(trashPath);
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var value = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var trashedAt))
            {
                continue;
            }

            if (now - trashedAt < trashGracePeriod)
            {
                continue;
            }

            File.Delete(trashPath);
            File.Delete(metadataPath);
        }
    }

    private static string MetadataPath(string trashPath) => trashPath + ".trashed-at";
}
