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

    public static string Signed(int days) => days > 0 ? "+" + days : days.ToString();
}

public class PdfExporter
{
    public const string ContentType = "application/pdf";

    public PdfExporter(IConfiguration cfg) { }

    public byte[] ProjectSchedule(ProjectView p, DateOnly today) => Encoding.UTF8.GetBytes("%PDF-fake " + p.Title);
    public byte[] Weekly(WeeklyReport report) => Encoding.UTF8.GetBytes("%PDF-fake weekly " + report.From);
}
