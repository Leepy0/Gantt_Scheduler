// 로컬(오프라인) 검증 전용 — ClosedXML 없이 빌드하기 위한 대체 구현. 실제 xlsx 가 아니라 CSV 텍스트를 돌려준다.
#nullable enable
using System.Text;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

public class ExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] ProjectList(List<ProjectView> rows) =>
        Encoding.UTF8.GetBytes(string.Join("\n", rows.Select(p => $"{p.Title},{p.Category},{StateLabel(p)},{p.Progress}")));

    public byte[] ProjectSchedule(ProjectView p) =>
        Encoding.UTF8.GetBytes(string.Join("\n", p.Stages.Select(s => $"{s.Name},{s.TargetDate},{s.CompletedDate}")));

    public static string StateLabel(ProjectView p) => p.DelayDays > 0 ? "지연" : p.State;
}
