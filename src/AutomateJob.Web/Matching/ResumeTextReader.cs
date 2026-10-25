using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace AutomateJob.Web.Matching;

public sealed class ResumeTextReader
{
    private const long MaxBytes = 5 * 1024 * 1024;

    public async Task<string> ReadAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length == 0)
        {
            throw new InvalidOperationException("The resume file is empty.");
        }

        if (file.Length > MaxBytes)
        {
            throw new InvalidOperationException("Resume files must be 5 MB or smaller.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        return extension switch
        {
            ".txt" => ReadPlainText(buffer),
            ".pdf" => ReadPdf(buffer),
            ".docx" => ReadDocx(buffer),
            _ => throw new InvalidOperationException("Upload a .pdf, .docx, or .txt resume.")
        };
    }

    private static string ReadPlainText(MemoryStream buffer)
    {
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadPdf(MemoryStream buffer)
    {
        using var document = PdfDocument.Open(buffer);
        var pages = document.GetPages().Select(page => page.Text);
        return string.Join(Environment.NewLine, pages);
    }

    private static string ReadDocx(MemoryStream buffer)
    {
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("That Word file does not contain readable document text.");

        using var entryStream = entry.Open();
        var document = XDocument.Load(entryStream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document.Descendants(word + "p")
            .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(node => node.Value)))
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return string.Join(Environment.NewLine, paragraphs);
    }
}
