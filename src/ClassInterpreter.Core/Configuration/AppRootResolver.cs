namespace ClassInterpreter.Core.Configuration;

public static class AppRootResolver
{
    private const string PreferredDataRoot = @"D:\AM-LINK";

    public static string Resolve(string executableDirectory, string localAppData, Func<string, bool> isWritable, bool dDriveExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentNullException.ThrowIfNull(isWritable);

        var candidates = new List<string>();
        if (dDriveExists) candidates.Add(PreferredDataRoot);
        candidates.Add(executableDirectory);
        candidates.Add(Path.Combine(localAppData, "AM-LINK"));

        foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (isWritable(candidate)) return Path.TrimEndingDirectorySeparator(candidate);
        }
        throw new IOException("无法找到可写的数据保存位置。");
    }

    public static string ResolveDefault() => Resolve(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProbeWritable,
        Directory.Exists(@"D:\"));

    private static bool ProbeWritable(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".am-link-write-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
