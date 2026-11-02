using System.IO.Compression;
using System.Text;
using AICompanyOS.Runtime;

public static class KnowledgeTests
{
    private static int _checks;

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aicos-knowledge-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileEventStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var source = await store.ImportSourceAsync("software-studio", new("architecture", "Rendering decisions",
                "Quyết định chính thức: giữ tương thích dữ liệu lưu. Projectile renderer dùng shared visual effect models.", Classification: "confidential"), CancellationToken.None);
            var accent = await store.SearchAsync("software-studio", "giu tuong thich du lieu", CancellationToken.None);
            Check(accent.Mode == "hybrid-local-vector" && accent.IndexVersion == "local-hybrid-v1" && accent.Hits[0].Citation.ContentSha256 == source.ContentSha256,
                "Hybrid search must resolve accent-insensitive text to the exact source revision.");
            Check((await store.SearchAsync("software-studio", "quyet dinh", CancellationToken.None)).Hits[0].MatchedTerms == 2,
                "Vietnamese đ must fold consistently with unaccented d in lexical matching.");
            var fuzzy = await store.SearchAsync("software-studio", "projectle rendrer", CancellationToken.None);
            Check(fuzzy.Hits.Any(h => h.Citation.SourceId == source.SourceId && h.VectorScore > 0), "Character-vector retrieval must recover a bounded typo without inventing a citation.");
            var status = await store.KnowledgeIndexStatusAsync("software-studio", CancellationToken.None);
            Check(status.Sources == 1 && status.Chunks == 1 && status.Capabilities.Contains("exact source revision/range citations"), "Index status must report the derived local corpus and its honest capability boundary.");

            var html = DocumentIngestion.Parse("notes.html", "text/html", Encoding.UTF8.GetBytes("<h1>Plan</h1><script>steal()</script><p>Owner review required.</p>"));
            Check(html.Content.Contains("Owner review required") && !html.Content.Contains("steal"), "HTML import must remove active-content bodies and retain visible text.");
            const string formatted = "{\n  \"label\": \"Keep   spacing\"\n}\n";
            Check(DocumentIngestion.Parse("formatted.json", null, Encoding.UTF8.GetBytes(formatted)).Content == formatted,
                "Plain-text and JSON imports must preserve indentation, blank lines and string whitespace semantics.");
            var docx = DocumentIngestion.Parse("requirements.docx", null, OfficeArchive("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Preserve saves</w:t></w:r></w:p><w:p><w:r><w:t>Run tests</w:t></w:r></w:p></w:body></w:document>"));
            Check(docx.Kind == "docx" && docx.Content == "Preserve saves\nRun tests", "DOCX parser must extract ordered text without executing Office content.");
            Check(docx.OriginalSha256.StartsWith("sha256:") && docx.OriginalSha256.Length == 71 && docx.OriginalSha256 != RuntimeJson.TextHash(docx.Content),
                "Imported Office provenance must distinguish original file bytes from the parsed text hash.");
            await Throws<ArgumentException>(() => Task.Run(() => DocumentIngestion.Parse("unsafe.docx", null, OfficeArchive("word/document.xml", "<!DOCTYPE document [<!ENTITY data 'expanded'>]><document>&data;</document>"))),
                "Office XML must reject DTD/entity declarations before parsing content.");
            await Throws<ArgumentException>(() => Task.Run(() => DocumentIngestion.Parse("broken.docx", null, [1, 2, 3])), "Malformed Office uploads must produce an input error instead of a storage-integrity failure.");
            await Throws<ArgumentException>(() => Task.Run(() => DocumentIngestion.Parse("broken.json", null, Encoding.UTF8.GetBytes("{broken}"))), "Malformed JSON uploads must return a bounded input error.");
            var xlsx = DocumentIngestion.Parse("criteria.xlsx", null, SpreadsheetArchive());
            Check(xlsx.Kind == "xlsx" && xlsx.Content.Contains("AC-1") && xlsx.Content.Contains("Pass tests"), "XLSX parser must resolve shared strings and cells offline.");
            var pdf = DocumentIngestion.Parse("report.pdf", "application/pdf", Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Length 39 >> stream\nBT (Verified receipt) Tj ET\nendstream\nendobj\n%%EOF"));
            Check(pdf.Kind == "pdf" && pdf.Content == "Verified receipt", "Text-layer PDF parser must extract a bounded literal text operation.");
            await Throws<ArgumentException>(() => Task.Run(() => DocumentIngestion.Parse("scan.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 /Encrypt no text"))), "Encrypted or unsupported PDF input must fail closed without OCR fallback.");
            await Throws<ArgumentException>(() => Task.Run(() => DocumentIngestion.Parse("secret.txt", "text/plain", Encoding.UTF8.GetBytes("api_key=sk-proj-123456789012345678901234567890"))), "Parsed document content must pass the credential scanner before persistence.");
            Console.WriteLine($"PASS: {_checks} knowledge assertions: hybrid local retrieval, exact citations, bounded DOCX/XLSX/PDF/HTML parsing and secret rejection.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] OfficeArchive(string path, string xml)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) Write(archive, path, xml);
        return output.ToArray();
    }

    private static byte[] SpreadsheetArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(archive, "xl/sharedStrings.xml", "<sst><si><t>AC-1</t></si><si><t>Pass tests</t></si></sst>");
            Write(archive, "xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row></sheetData></worksheet>");
        }
        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(value);
    }

    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        _checks++; if (!condition) throw new InvalidOperationException(message);
    }
}
