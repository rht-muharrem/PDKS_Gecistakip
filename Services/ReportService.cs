using System.IO.Compression;
using System.Net;
using System.Text;
using ServerMonitor.Models;

namespace ServerMonitor.Services;


public static class ReportService
{

    private const string Green = "2EA043";
    private const string Amber = "D29922";
    private const string Red = "E5484D";
    private const string Grey = "8B96A5";
    private const string HeaderFill = "1B222B";
    private const string ZebraFill = "F3F5F8";

    public static byte[] BuildStatusReport(IReadOnlyList<ServerStatus> servers, string? scopeLabel)
    {
        var body = new StringBuilder();


        body.Append(Para("Sunucu Durum Raporu", bold: true, sizeHalfPt: 40, color: "1B222B"));
        var subtitle = $"{DateTime.Now:dd.MM.yyyy HH:mm}" +
                       (string.IsNullOrEmpty(scopeLabel) ? "" : $"  ·  {scopeLabel}") +
                       $"  ·  {Environment.MachineName}";
        body.Append(Para(subtitle, sizeHalfPt: 18, color: "5F6B7A"));

        var summary = new
        {
            up = servers.Count(s => s.State == HealthState.Up),
            warn = servers.Count(s => s.State == HealthState.Warning),
            down = servers.Count(s => s.State == HealthState.Down),
            other = servers.Count(s => s.State is HealthState.Unknown)
        };
        body.Append(Para(
            $"Toplam {servers.Count}  ·  Çalışıyor {summary.up}  ·  Uyarı {summary.warn}  ·  Sorunlu {summary.down}  ·  Bilinmiyor {summary.other}",
            sizeHalfPt: 20, color: "1B222B"));
        body.Append(Spacer());

        var groups = servers
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Group) ? "Genel" : s.Group)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in groups)
        {
            body.Append(Para(group.Key, bold: true, sizeHalfPt: 26, color: "1B222B"));
            body.Append(BuildTable(group
                .OrderBy(s => StateOrder(s.State))
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)));
            body.Append(Spacer());
        }

        if (servers.Count == 0)
            body.Append(Para("Bu kapsamda gösterilecek cihaz yok.", color: "5F6B7A"));

        var document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
            "<w:pgMar w:top=\"1100\" w:right=\"1100\" w:bottom=\"1100\" w:left=\"1100\"/></w:sectPr>" +
            "</w:body></w:document>";

        return Package(document);
    }


    private static string BuildTable(IEnumerable<ServerStatus> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/>");
        sb.Append("<w:tblBorders>");
        foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            sb.Append($"<w:{edge} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"DDE3EA\"/>");
        sb.Append("</w:tblBorders><w:tblLook w:firstRow=\"1\"/></w:tblPr>");


        sb.Append("<w:tr>");
        sb.Append(HeaderCell("Cihaz", 2800));
        sb.Append(HeaderCell("Adres", 2600));
        sb.Append(HeaderCell("Durum", 1400));
        sb.Append(HeaderCell("Uptime", 1000));
        sb.Append(HeaderCell("Kontroller", 3200));
        sb.Append("</w:tr>");

        var zebra = false;
        foreach (var s in rows)
        {
            var fill = zebra ? ZebraFill : "FFFFFF";
            zebra = !zebra;

            var checks = string.Join(" · ", (s.Checks ?? new List<CheckResult>())
                .Select(c => $"{c.Label}: {c.Message}"));

            var kind = s.Category == DeviceCategory.Computer ? "🖥 " : "";

            sb.Append("<w:tr>");
            sb.Append(Cell(kind + s.Name, fill, bold: true));
            sb.Append(Cell(s.Host, fill, mono: true));
            sb.Append(Cell(StateText(s.State), StateColor(s.State), bold: true, textColor: "FFFFFF"));
            sb.Append(Cell($"%{s.UptimePercent}", fill));
            sb.Append(Cell(checks, fill, sizeHalfPt: 16));
            sb.Append("</w:tr>");
        }

        sb.Append("</w:tbl>");
        return sb.ToString();
    }

    private static string HeaderCell(string text, int widthDxa) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{widthDxa}\" w:type=\"dxa\"/>" +
        $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{HeaderFill}\"/></w:tcPr>" +
        Para(text, bold: true, sizeHalfPt: 18, color: "FFFFFF") + "</w:tc>";

    private static string Cell(string text, string fill, bool bold = false, bool mono = false,
        int sizeHalfPt = 18, string color = "1B222B", string? textColor = null)
    {
        return "<w:tc><w:tcPr>" +
               $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/></w:tcPr>" +
               Para(text, bold: bold, sizeHalfPt: sizeHalfPt, color: textColor ?? color, mono: mono) +
               "</w:tc>";
    }

 

    private static string Para(string text, bool bold = false, int sizeHalfPt = 22, string color = "1B222B", bool mono = false)
    {
        var rpr = new StringBuilder("<w:rPr>");
        if (bold) rpr.Append("<w:b/>");
        rpr.Append($"<w:color w:val=\"{color}\"/>");
        rpr.Append($"<w:sz w:val=\"{sizeHalfPt}\"/>");
        if (mono) rpr.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/>");
        rpr.Append("</w:rPr>");

        return "<w:p><w:pPr><w:spacing w:after=\"60\"/></w:pPr>" +
               "<w:r>" + rpr + $"<w:t xml:space=\"preserve\">{XmlEscape(text)}</w:t></w:r></w:p>";
    }

    private static string Spacer() => "<w:p><w:pPr><w:spacing w:after=\"120\"/></w:pPr></w:p>";

    private static byte[] Package(string documentXml)
    {
        const string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";

        const string rels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", contentTypes);
            WriteEntry(zip, "_rels/.rels", rels);
            WriteEntry(zip, "word/document.xml", documentXml);
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }



    private static string XmlEscape(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static int StateOrder(HealthState s) => s switch
    {
        HealthState.Down => 0,
        HealthState.Warning => 1,
        HealthState.Unknown => 2,
        _ => 3
    };

    private static string StateText(HealthState s) => s switch
    {
        HealthState.Up => "ÇALIŞIYOR",
        HealthState.Warning => "UYARI",
        HealthState.Down => "SORUNLU",
        _ => "BİLİNMİYOR"
    };

    private static string StateColor(HealthState s) => s switch
    {
        HealthState.Up => Green,
        HealthState.Warning => Amber,
        HealthState.Down => Red,
        _ => Grey
    };
}
