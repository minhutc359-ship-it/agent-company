using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AICompanyOS.Runtime;

public sealed record ParsedDocument(string Title, string Content, string Kind, string Mime, string ParserVersion, string OriginalName, string OriginalSha256);

public static partial class DocumentIngestion
{
    public const int MaxUploadBytes = 8 * 1024 * 1024;
    public const int MaxTextCharacters = 262_144;

    public static ParsedDocument Parse(string fileName, string? contentType, byte[] bytes, string? title = null)
    {
        if (bytes.Length is 0 or > MaxUploadBytes) throw new ArgumentException("Document must contain 1 byte to 8 MiB.");
        var name = Path.GetFileName(fileName);
        if (name != fileName || name.Length is 0 or > 180 || name.Any(char.IsControl) || name.StartsWith('.') || name.Contains('\\') || name.Contains(':')) throw new ArgumentException("Use a simple non-hidden document name.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        string content; string kind; string parser; string mime;
        try
        {
        switch (extension)
        {
            case ".txt": case ".md": case ".markdown": case ".csv": case ".tsv": case ".yaml": case ".yml":
                content = Utf8(bytes); kind = extension.TrimStart('.'); parser = "utf8-text-v1"; mime = contentType ?? "text/plain"; break;
            case ".json":
                content = Utf8(bytes); using (JsonDocument.Parse(content)) { } kind = "json"; parser = "json-v1"; mime = "application/json"; break;
            case ".html": case ".htm":
                content = Html(Utf8(bytes)); kind = "html"; parser = "html-text-v1"; mime = "text/html"; break;
            case ".docx":
                content = OpenXmlText(bytes, false); kind = "docx"; parser = "openxml-word-v1"; mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"; break;
            case ".xlsx":
                content = OpenXmlText(bytes, true); kind = "xlsx"; parser = "openxml-sheet-v1"; mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; break;
            case ".pdf":
                content = PdfText(bytes); kind = "pdf"; parser = "pdf-text-layer-v1"; mime = "application/pdf"; break;
            default: throw new ArgumentException("Supported imports: TXT, Markdown, JSON, CSV/TSV, YAML, HTML, DOCX, XLSX and text-layer PDF.");
        }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or RegexMatchTimeoutException or XmlException)
        {
            throw new ArgumentException("The document is malformed or exceeds the supported parser limits.");
        }
        content = kind is "html" or "pdf" ? Normalize(content) : content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxTextCharacters) throw new ArgumentException("Parsed document must contain at most 262,144 characters.");
        if (SourceSafety.ContainsKnownSecret(content) || SourceSafety.ContainsKnownSecret(name)) throw new ArgumentException("Possible credentials detected. Remove secrets before importing.");
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(name) : title.Trim();
        if (resolvedTitle.Length is 0 or > 160 || SourceSafety.ContainsKnownSecret(resolvedTitle)) throw new ArgumentException("Document title must contain at most 160 secret-free characters.");
        return new(resolvedTitle, content, kind, mime, parser, name, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string Utf8(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) throw new ArgumentException("Binary content is not valid text input.");
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
        catch (DecoderFallbackException) { throw new ArgumentException("Text documents must use valid UTF-8."); }
    }

    private static string Html(string html)
    {
        html = DangerousHtml().Replace(html, " ");
        html = Tags().Replace(html, " ");
        return WebUtility.HtmlDecode(html);
    }

    private static string OpenXmlText(byte[] bytes, bool workbook)
    {
        using var memory = new MemoryStream(bytes); using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        ValidateArchive(archive);
        if (archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Macro-enabled Office documents are not imported.");
        if (!workbook)
        {
            var entry = archive.GetEntry("word/document.xml") ?? throw new ArgumentException("DOCX document.xml is missing.");
            var root = LoadXml(entry);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join('\n', root.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).Where(s => s.Length > 0));
        }
        var shared = archive.GetEntry("xl/sharedStrings.xml") is { } strings
            ? LoadXml(strings).Descendants().Where(e => e.Name.LocalName == "si").Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray() : [];
        var rows = new List<string>();
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            rows.Add("# " + Path.GetFileNameWithoutExtension(entry.Name));
            foreach (var row in LoadXml(entry).Descendants().Where(e => e.Name.LocalName == "row"))
            {
                var cells = row.Elements().Where(e => e.Name.LocalName == "c").Select(cell =>
                {
                    var value = cell.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    return cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length ? shared[index] : value;
                });
                rows.Add(string.Join('\t', cells));
            }
        }
        return string.Join('\n', rows);
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 256 || archive.Entries.Any(e => e.Length > 4 * 1024 * 1024) || archive.Entries.Sum(e => e.Length) > 16 * 1024 * 1024)
            throw new ArgumentException("Office archive exceeds safe entry or expansion limits.");
        if (archive.Entries.Any(e => e.FullName.StartsWith('/') || e.FullName.Contains("../", StringComparison.Ordinal) || e.FullName.Contains("\\", StringComparison.Ordinal)))
            throw new ArgumentException("Office archive contains an unsafe path.");
    }

    private static XElement LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        try { return XDocument.Load(reader, LoadOptions.None).Root ?? throw new ArgumentException("Office XML is empty."); }
        catch (XmlException) { throw new ArgumentException("Office XML is malformed or contains a prohibited DTD."); }
    }

    private static string PdfText(byte[] bytes)
    {
        var ascii = Encoding.Latin1.GetString(bytes);
        if (!ascii.StartsWith("%PDF-", StringComparison.Ordinal) || ascii.Contains("/Encrypt", StringComparison.Ordinal)) throw new ArgumentException("PDF must be an unencrypted PDF with a text layer.");
        var parts = new List<string>(); var characters = 0; var expandedBytes = 0;
        void Add(string value)
        {
            characters += value.Length;
            if (characters > MaxTextCharacters) throw new ArgumentException("PDF text exceeds the parsed character limit.");
            parts.Add(value);
        }
        foreach (Match match in PdfStream().Matches(ascii))
        {
            var dictionary = match.Groups[1].Value; var raw = Encoding.Latin1.GetBytes(match.Groups[2].Value); byte[] decoded = raw;
            if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                try
                {
                    using var input = new MemoryStream(raw); using var zlib = new ZLibStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
                    var buffer = new byte[4096]; int read;
                    while ((read = zlib.Read(buffer)) > 0) { if (output.Length + read > 1024 * 1024) throw new ArgumentException("PDF text stream expands beyond the safe limit."); output.Write(buffer, 0, read); }
                    decoded = output.ToArray();
                }
                catch (InvalidDataException) { continue; }
            }
            expandedBytes += decoded.Length;
            if (expandedBytes > 16 * 1024 * 1024) throw new ArgumentException("PDF streams exceed the total expansion limit.");
            var stream = Encoding.Latin1.GetString(decoded);
            foreach (Match text in PdfLiteral().Matches(stream)) Add(DecodePdfLiteral(text.Groups[1].Value));
            foreach (Match array in PdfArray().Matches(stream)) foreach (Match text in PdfStringInArray().Matches(array.Groups[1].Value)) Add(DecodePdfLiteral(text.Groups[1].Value));
            foreach (Match text in PdfHex().Matches(stream))
            {
                var value = string.Concat(text.Groups[1].Value.Where(c => !char.IsWhiteSpace(c))); if (value.Length % 2 != 0) value += "0";
                try
                {
                    var decodedText = Convert.FromHexString(value);
                    Add(decodedText.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode.GetString(decodedText[2..]) : Encoding.Latin1.GetString(decodedText));
                }
                catch (FormatException) { }
            }
        }
        var result = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (result.Length < 2) throw new ArgumentException("No supported PDF text layer was found. Offline OCR is not installed and no cloud OCR is called.");
        return result;
    }

    private static string DecodePdfLiteral(string value)
    {
        var output = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { output.Append(value[i]); continue; }
            if (++i >= value.Length) throw new ArgumentException("PDF text contains a truncated escape.");
            var c = value[i];
            if (c is >= '0' and <= '7')
            {
                var octal = c - '0'; var count = 1;
                while (count < 3 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7') { octal = octal * 8 + value[++i] - '0'; count++; }
                output.Append((char)octal);
            }
            else output.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', '\r' or '\n' => '\0', _ => c });
        }
        return output.Replace("\0", "").ToString();
    }

    private static string Normalize(string value) => string.Join('\n', value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => Whitespace().Replace(line, " ").Trim()).Where(line => line.Length > 0));

    [GeneratedRegex(@"(?is)<(?:script|style|iframe|object|embed|svg|math)\b.*?</(?:script|style|iframe|object|embed|svg|math)\s*>", RegexOptions.CultureInvariant, 200)] private static partial Regex DangerousHtml();
    [GeneratedRegex(@"(?s)<[^>]{0,2000}>", RegexOptions.CultureInvariant, 200)] private static partial Regex Tags();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 100)] private static partial Regex Whitespace();
    [GeneratedRegex(@"(?s)(<<.{0,4096}?>>)\s*stream\r?\n(.*?)\r?\nendstream", RegexOptions.CultureInvariant, 1000)] private static partial Regex PdfStream();
    [GeneratedRegex("""\(((?:\\.|[^\\)])*)\)\s*(?:Tj|'|")""", RegexOptions.CultureInvariant, 300)] private static partial Regex PdfLiteral();
    [GeneratedRegex(@"\[((?:\\.|[^\]])*)\]\s*TJ", RegexOptions.CultureInvariant, 300)] private static partial Regex PdfArray();
    [GeneratedRegex(@"\(((?:\\.|[^\\)])*)\)", RegexOptions.CultureInvariant, 300)] private static partial Regex PdfStringInArray();
    [GeneratedRegex(@"<([0-9A-Fa-f\s]{2,8192})>\s*Tj", RegexOptions.CultureInvariant, 300)] private static partial Regex PdfHex();
}
