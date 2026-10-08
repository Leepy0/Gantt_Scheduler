using ClosedXML.Excel;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// Excel 내보내기 (ClosedXML)
public class ExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] ProjectList(List<ProjectView> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("과제 목록");
        Header(ws, ("과제명", 40), ("유형", 14), ("법인", 12), ("위치", 24), ("담당", 12), ("상태", 10),
            ("현재 단계", 14), ("단계 목표일", 13), ("진행률(%)", 11), ("지연(일)", 10), ("착수일", 13), ("목표 종료", 13));

        var r = 2;
        foreach (var p in rows)
        {
            ws.Cell(r, 1).Value = p.Title;
            ws.Cell(r, 2).Value = p.Category;
            ws.Cell(r, 3).Value = p.Corporation;
            ws.Cell(r, 4).Value = p.LocationPath;
            ws.Cell(r, 5).Value = p.Owner;
            ws.Cell(r, 6).Value = StateLabel(p);
            ws.Cell(r, 7).Value = p.CurrentStage ?? "";
            Date(ws.Cell(r, 8), p.CurrentTarget);
            ws.Cell(r, 9).Value = p.Progress;
            ws.Cell(r, 10).Value = p.DelayDays;
            Date(ws.Cell(r, 11), p.StartDate);
            Date(ws.Cell(r, 12), p.EndTarget);
            r++;
        }
        return Save(wb);
    }

    public byte[] ProjectSchedule(ProjectView p)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add("일정");
        Header(ws, ("구분", 8), ("단계", 14), ("이름", 36), ("담당", 12), ("부담당", 12), ("상태", 10), ("진행률(%)", 11),
            ("목표 시작", 13), ("목표 종료", 13), ("실적 시작", 13), ("실적 종료", 13), ("메모", 40));

        var r = 2;
        foreach (var s in p.Stages)
        {
            ws.Cell(r, 1).Value = "단계";
            ws.Cell(r, 2).Value = s.Name;
            ws.Cell(r, 3).Value = s.Name;
            ws.Cell(r, 6).Value = StageLabel(s.Status);
            ws.Cell(r, 7).Value = s.Progress;
            Date(ws.Cell(r, 8), s.PlanFrom);
            Date(ws.Cell(r, 9), s.TargetDate);
            Date(ws.Cell(r, 11), s.CompletedDate);
            ws.Range(r, 1, r, 12).Style.Font.Bold = true;
            ws.Range(r, 1, r, 12).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F3F5");
            r++;

            foreach (var w in s.Works)
            {
                ws.Cell(r, 1).Value = "작업";
                ws.Cell(r, 2).Value = s.Name;
                ws.Cell(r, 3).Value = w.Title;
                ws.Cell(r, 4).Value = w.Owner ?? "";
                ws.Cell(r, 5).Value = w.CoOwner ?? "";
                ws.Cell(r, 6).Value = WorkLabel(w);
                ws.Cell(r, 7).Value = w.Progress;
                Date(ws.Cell(r, 8), w.PlanStart);
                Date(ws.Cell(r, 9), w.PlanEnd);
                Date(ws.Cell(r, 10), w.ActualStart);
                Date(ws.Cell(r, 11), w.ActualEnd);
                ws.Cell(r, 12).Value = w.Note ?? "";
                r++;
            }
        }

        var log = wb.Worksheets.Add("활동");
        Header(log, ("일시", 18), ("사용자", 12), ("내용", 70), ("사유", 50));
        r = 2;
        foreach (var a in p.Activities ?? new())
        {
            log.Cell(r, 1).Value = KoreaClock.ToKst(a.At).ToString("yyyy-MM-dd HH:mm");
            log.Cell(r, 2).Value = a.User;
            log.Cell(r, 3).Value = a.Text;
            log.Cell(r, 4).Value = a.Reason ?? "";
            r++;
        }
        return Save(wb);
    }

    public static string StateLabel(ProjectView p) => p.DelayDays > 0 ? "지연" : p.State switch
    {
        "active" => "진행",
        "hold" => "보류",
        "dropped" => "중단",
        "done" => "완료",
        _ => p.State,
    };

    private static string StageLabel(string status) => status switch
    {
        "done" => "완료",
        "late" => "지연",
        "current" => "진행중",
        _ => "예정",
    };

    private static string WorkLabel(WorkView w) =>
        w.Late ? "지연" : w.DoneLate ? "완료(초과)" : w.Status switch
        {
            "done" => "완료",
            "inprogress" => "진행중",
            _ => "예정",
        };

    private static void Header(IXLWorksheet ws, params (string Title, double Width)[] columns)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            ws.Cell(1, i + 1).Value = columns[i].Title;
            ws.Column(i + 1).Width = columns[i].Width;
        }
        var head = ws.Range(1, 1, 1, columns.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#1428A0");
        ws.SheetView.FreezeRows(1);
    }

    private static void Date(IXLCell cell, DateOnly? date)
    {
        if (date == null) return;
        cell.Value = date.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "yyyy-mm-dd";
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
