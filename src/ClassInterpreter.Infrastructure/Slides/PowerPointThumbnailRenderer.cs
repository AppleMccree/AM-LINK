using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClassInterpreter.Infrastructure.Slides;

public sealed class PowerPointThumbnailRenderer
{
    public IReadOnlyDictionary<int, string> Render(string presentationPath, string cacheRoot)
    {
        var powerpointType = Type.GetTypeFromProgID("PowerPoint.Application")
            ?? throw new NotSupportedException("本机未安装 Microsoft PowerPoint，无法生成 PPTX 页面预览图。");
        var fullPath = Path.GetFullPath(presentationPath);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant()[..16];
        var outputDirectory = Path.Combine(Path.GetFullPath(cacheRoot), "slides", hash);
        Directory.CreateDirectory(outputDirectory);
        var cached = Directory.EnumerateFiles(outputDirectory, "slide-*.png").ToArray();
        if (cached.Length > 0)
        {
            return cached.ToDictionary(ParsePageNumber, path => path);
        }

        object? application = null;
        object? presentation = null;
        try
        {
            application = Activator.CreateInstance(powerpointType)
                ?? throw new InvalidOperationException("无法启动 PowerPoint 页面渲染器。");
            dynamic app = application;
            presentation = app.Presentations.Open(fullPath, -1, 0, 0);
            dynamic deck = presentation;
            var results = new Dictionary<int, string>();
            for (var page = 1; page <= deck.Slides.Count; page++)
            {
                var output = Path.Combine(outputDirectory, $"slide-{page:D4}.png");
                object? slide = null;
                try
                {
                    slide = deck.Slides[page];
                    ((dynamic)slide).Export(output, "PNG", 1280, 720);
                    results[page] = output;
                }
                finally
                {
                    Release(slide);
                }
            }

            return results;
        }
        finally
        {
            if (presentation is not null)
            {
                try { ((dynamic)presentation).Close(); } catch (COMException) { }
            }

            if (application is not null)
            {
                try { ((dynamic)application).Quit(); } catch (COMException) { }
            }

            Release(presentation);
            Release(application);
        }
    }

    private static int ParsePageNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.Parse(name.AsSpan("slide-".Length));
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
