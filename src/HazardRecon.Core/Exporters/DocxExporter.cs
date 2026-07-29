using System.IO.Compression;
using System.Net;
using System.Text;

namespace HazardRecon.Core.Exporters;

public class DocxExporter
{
    public static string WriteMemo(string mdText, string outdir, string dateStr, List<string> sets)
    {
        string filename = "analysis_memo.docx";
        string path = Path.Combine(outdir, filename);

        StringBuilder docXml = new();
        docXml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        docXml.AppendLine("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
        docXml.AppendLine("<w:body>");

        // Title
        docXml.AppendLine("<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val=\"36\"/></w:rPr><w:t>Hazard-Rate Reconciliation - Analysis Memo</w:t></w:r></w:p>");
        docXml.AppendLine($"<w:p><w:r><w:rPr><w:i/><w:color w:val=\"5B6B7F\"/></w:rPr><w:t>Anchor Point Risk  |  {WebUtility.HtmlEncode(dateStr)}</w:t></w:r></w:p>");

        if (sets.Count > 0)
        {
            docXml.AppendLine($"<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Sets: {WebUtility.HtmlEncode(string.Join(", ", sets))}</w:t></w:r></w:p>");
        }

        docXml.AppendLine("<w:p/>"); // empty spacer line

        // Process markdown lines
        foreach (string rawLine in mdText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("## "))
            {
                string heading = WebUtility.HtmlEncode(line[3..]);
                docXml.AppendLine($"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val=\"28\"/><w:color w:val=\"1A2332\"/></w:rPr><w:t>{heading}</w:t></w:r></w:p>");
            }
            else if (line.StartsWith("# "))
            {
                string heading = WebUtility.HtmlEncode(line[2..]);
                docXml.AppendLine($"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val=\"32\"/><w:color w:val=\"1A2332\"/></w:rPr><w:t>{heading}</w:t></w:r></w:p>");
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                string bullet = WebUtility.HtmlEncode(line[2..]);
                docXml.AppendLine($"<w:p><w:pPr><w:pStyle w:val=\"ListBullet\"/></w:pPr><w:r><w:t>•  {bullet}</w:t></w:r></w:p>");
            }
            else
            {
                string para = WebUtility.HtmlEncode(line);
                docXml.AppendLine($"<w:p><w:r><w:t>{para}</w:t></w:r></w:p>");
            }
        }

        docXml.AppendLine("</w:body>");
        docXml.AppendLine("</w:document>");

        // Write OpenXml zip package
        if (File.Exists(path)) File.Delete(path);

        using (FileStream fs = new(path, FileMode.Create))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            // [Content_Types].xml
            ZipArchiveEntry contentTypesEntry = archive.CreateEntry("[Content_Types].xml");
            using (StreamWriter sw = new(contentTypesEntry.Open(), Encoding.UTF8))
            {
                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                         "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                         "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                         "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                         "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                         "</Types>");
            }

            // _rels/.rels
            ZipArchiveEntry relsEntry = archive.CreateEntry("_rels/.rels");
            using (StreamWriter sw = new(relsEntry.Open(), Encoding.UTF8))
            {
                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                         "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                         "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                         "</Relationships>");
            }

            // word/document.xml
            ZipArchiveEntry docEntry = archive.CreateEntry("word/document.xml");
            using (StreamWriter sw = new(docEntry.Open(), Encoding.UTF8))
            {
                sw.Write(docXml.ToString());
            }
        }

        return filename;
    }
}
