using System.Security.Cryptography;
using System.Text;
using PDFtoImage;

namespace ClassInterpreter.Infrastructure.Slides;

public sealed class PdfPageImageRenderer
{
    public IReadOnlyDictionary<int, string> Render(string pdfPath, string cacheRoot, int pageCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("找不到 PDF 文件。", pdfPath);
        }
        if (pageCount < 1)
        {
            return new Dictionary<int, string>();
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(pdfPath)}|{File.GetLastWriteTimeUtc(pdfPath).Ticks}|{new FileInfo(pdfPath).Length}")))[..16];
        var outputDirectory = Path.Combine(cacheRoot, "pdf-pages", fingerprint);
        Directory.CreateDirectory(outputDirectory);

        var result = new Dictionary<int, string>();
        for (var page = 1; page <= pageCount; page++)
        {
            var outputPath = Path.Combine(outputDirectory, $"page-{page:D4}.png");
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                using var pdfStream = File.OpenRead(pdfPath);
                Conversion.SavePng(outputPath, pdfStream, page - 1, leaveOpen: false, options: new RenderOptions
                {
                    Width = 1600,
                    WithAspectRatio = true,
                    WithAnnotations = true
                });
            }
            result[page] = outputPath;
        }
        return result;
    }
}
