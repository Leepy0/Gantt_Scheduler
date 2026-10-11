// 로컬(오프라인) 검증 전용 — ClosedXML·MigraDoc 없이 빌드하기 위한 대체 구현. 실제 파일이 아니라 텍스트를 돌려준다.
#nullable enable
using System.Text;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

public class ExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] ProjectList(List<ProjectView> rows, DateOnly today, string filterText) =>
        Encoding.UTF8.GetBytes(filterText + "\n" + string.Join("\n", rows.Select(p => $"{p.Title},{p.Category},{p.StateLabel},{p.Progress}")));

    public byte[] ProjectSchedule(ProjectView p, DateOnly today) =>
        Encoding.UTF8.GetBytes(string.Join("\n", p.Stages.Select(s => $"{s.Name},{s.TargetDate},{s.CompletedDate},{s.Chip.Text}")));

    public byte[] Weekly(WeeklyReport report) =>
        Encoding.UTF8.GetBytes(string.Join("\n", report.Projects.Select(p => $"{p.Title},{p.StateLabel},{p.Done.Count},{p.Changes.Count}")));

    public byte[] History(List<HistoryRow> rows, string filterText, int total) =>
        Encoding.UTF8.GetBytes(filterText + "\n" + string.Join("\n", rows.Select(r => $"{r.At:yyyy-MM-dd HH:mm},{r.User},{r.Project},{r.Text}")));

    public static string Signed(int days) => days > 0 ? "+" + days : days.ToString();

    // 가져오기 양식 — 섀도 빌드는 xlsx 대신 '# 시트명' 줄과 TAB 으로 나눈 칸의 글자를 준다(FakeProjectImporter 가 같은 형식을 읽는다)
    public byte[] ImportTemplate(ImportLists lists) =>
        Encoding.UTF8.GetBytes("# " + ProjectService.ImportColumns.ProjectSheet + "\n" + string.Join("\t", ProjectService.ImportColumns.Project) + "\n" +
                               "# " + ProjectService.ImportColumns.WorkSheet + "\n" + string.Join("\t", ProjectService.ImportColumns.Work) + "\n");
}

// 섀도 빌드용 가져오기 읽기: '# 시트명' 다음 줄이 머리글, 그 뒤가 줄(TAB 구분)
public class ProjectImporter
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public ImportBook Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sheets = new Dictionary<string, ImportSheet>();
        ImportSheet? cur = null;
        var row = 0;
        foreach (var line in reader.ReadToEnd().Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("# ")) { cur = new ImportSheet(line[2..].Trim(), new(), new()); sheets[cur.Name] = cur; row = 0; continue; }
            if (cur == null || line.Length == 0) continue;
            row++;
            var cells = line.Split('\t').ToList();
            if (row == 1) cur.Headers.AddRange(cells); else cur.Rows.Add(new ImportRow(row, cells));
        }
        if (sheets.Count == 0) throw new RuleException("Excel(.xlsx) 파일을 읽지 못했습니다.", "file");
        return new ImportBook(sheets.GetValueOrDefault(ProjectService.ImportColumns.ProjectSheet), sheets.GetValueOrDefault(ProjectService.ImportColumns.WorkSheet));
    }
}

public class PdfExporter
{
    public const string ContentType = "application/pdf";

    public PdfExporter(IConfiguration cfg) { }

    public byte[] ProjectSchedule(ProjectView p, DateOnly today) => Encoding.UTF8.GetBytes("%PDF-fake " + p.Title);
    public byte[] Weekly(WeeklyReport report) => Encoding.UTF8.GetBytes("%PDF-fake weekly " + report.From);
}
