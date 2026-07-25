namespace ClassInterpreter.Core.Configuration;

public sealed record AppPaths(
    string Root,
    string DatabaseDirectory,
    string AudioDirectory,
    string TrashDirectory,
    string CacheDirectory,
    string CourseMaterialDirectory,
    string ExportDirectory,
    string LogDirectory)
{
    public IReadOnlyList<string> AllDirectories =>
    [
        DatabaseDirectory,
        AudioDirectory,
        TrashDirectory,
        CacheDirectory,
        CourseMaterialDirectory,
        ExportDirectory,
        LogDirectory
    ];

    public static AppPaths Create(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("数据目录不能为空。", nameof(root));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return new AppPaths(
            fullRoot,
            Path.Combine(fullRoot, "data", "db"),
            Path.Combine(fullRoot, "data", "audio"),
            Path.Combine(fullRoot, "data", "trash"),
            Path.Combine(fullRoot, "data", "cache"),
            Path.Combine(fullRoot, "data", "courses"),
            Path.Combine(fullRoot, "data", "exports"),
            Path.Combine(fullRoot, "logs"));
    }

    public void EnsureDirectories()
    {
        try
        {
            foreach (var directory in AllDirectories) Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法创建数据目录：{Root}", exception);
        }
    }
}
