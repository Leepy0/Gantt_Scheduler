using ClosedXML.Excel;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// Excel 내보내기 (ClosedXML). 양식: 제목·요약 → 머리글(4행 부근) → 표. A4 가로 한 장 폭에 맞춰 인쇄된다
public class ExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly XLColor Accent = XLColor.FromHtml("#1428A0");
    private static readonly XLColor AccentWeak = XLColor.FromHtml("#E8EBF8");
    private static readonly XLColor AccentMid = XLColor.FromHtml("#8D99D8");
    private static readonly XLColor Line = XLColor.FromHtml("#D5DAE1");
    private static readonly XLColor Soft = XLColor.FromHtml("#F1F3F5");
    private static readonly XLColor Plan = XLColor.FromHtml("#CBD0D8");
    private static readonly XLColor Muted = XLColor.FromHtml("#636A77");
    private static readonly XLColor Ok = XLColor.FromHtml("#137A3B");
    private static readonly XLColor OkWeak = XLColor.FromHtml("#E4F3E9");
    private static readonly XLColor Warn = XLColor.FromHtml("#8A5200");
    private static readonly XLColor WarnWeak = XLColor.FromHtml("#FFF1D6");
    private static readonly XLColor WarnMid = XLColor.FromHtml("#E0A030");
    private static readonly XLColor Alarm = XLColor.FromHtml("#C62828");
    private static readonly XLColor AlarmWeak = XLColor.FromHtml("#FDEBE9");

    // ── 과제 목록 ────────────────────────────────────────────────────────
    public byte[] ProjectList(List<ProjectView> rows, DateOnly today, string filterText)
    {
        using var wb = NewWorkbook();
        var ws = wb.Worksheets.Add("과제 목록");
        Title(ws, "과제 목록", $"기준일 {today:yyyy-MM-dd}(한국 시간) · {filterText} · {rows.Count}건");

        const int head = 4;
        Header(ws, head, ("No", 5), ("과제명", 38), ("유형", 13), ("법인", 11), ("위치", 22), ("담당", 10), ("상태", 9),
            ("현재 단계", 12), ("단계 목표일", 12), ("진행률", 9), ("지연(일)", 9), ("착수일", 12), ("목표 종료", 12), ("기준선 대비(일)", 13));

        var r = head + 1;
        foreach (var p in rows)
        {
            ws.Cell(r, 1).Value = r - head;
            ws.Cell(r, 2).Value = p.Title;
            ws.Cell(r, 3).Value = p.Category;
            ws.Cell(r, 4).Value = p.Corporation;
            ws.Cell(r, 5).Value = p.LocationPath;
            ws.Cell(r, 6).Value = p.Owner;
            State(ws.Cell(r, 7), p.StateLabel);
            ws.Cell(r, 8).Value = p.CurrentStage ?? "";
            Date(ws.Cell(r, 9), p.CurrentTarget);
            Percent(ws.Cell(r, 10), p.Progress);
            if (p.DelayDays > 0)
            {
                ws.Cell(r, 11).Value = p.DelayDays;
                ws.Cell(r, 11).Style.Font.FontColor = Alarm;
                ws.Cell(r, 11).Style.Font.Bold = true;
            }
            Date(ws.Cell(r, 12), p.StartDate);
            Date(ws.Cell(r, 13), p.EndTarget);
            Slip(ws.Cell(r, 14), p.SlipDays);
            r++;
        }
        Body(ws, head, r - 1, 14, centered: new[] { 1, 7, 9, 11, 12, 13, 14 });
        Print(ws, head);
        return Save(wb);
    }

    // ── 과제 일정 ────────────────────────────────────────────────────────
    public byte[] ProjectSchedule(ProjectView p, DateOnly today)
    {
        using var wb = NewWorkbook();
        ScheduleSheet(wb, p, today);
        GanttSheet(wb, p, today);
        ChecklistSheet(wb, p);
        ActivitySheet(wb, p);
        return Save(wb);
    }

    private static void ScheduleSheet(XLWorkbook wb, ProjectView p, DateOnly today)
    {
        var ws = wb.Worksheets.Add("일정");
        // 지연 판정과 날짜·시각이 모두 한국 시간 기준이라는 것을 밝힌다 (해외 법인에서 열어도 헷갈리지 않게)
        Title(ws, p.Title, $"출력일 {today:yyyy-MM-dd} · 날짜·시각은 한국 시간 기준");

        // 요약: 항목명-값 3쌍씩 두 줄
        var baseline = p.BaselineAt == null ? "미확정"
            : $"{KoreaClock.ToKst(p.BaselineAt.Value):yyyy-MM-dd} 확정" + (p.SlipDays == null ? "" : $" · 목표 종료 {Signed(p.SlipDays.Value)}일");
        Pair(ws, 3, 1, "유형", p.Category);
        Pair(ws, 3, 4, "위치", p.Corporation + (p.LocationPath.Length > 0 ? " · " + p.LocationPath : ""));
        Pair(ws, 3, 8, "담당", p.Owner + (p.OwnerOrg == null ? "" : $" ({p.OwnerOrg})"));
        Pair(ws, 4, 1, "상태", p.Chip.Text);
        Pair(ws, 4, 4, "기간", $"{p.StartDate:yyyy-MM-dd} ~ {(p.EndTarget == null ? "미정" : p.EndTarget.Value.ToString("yyyy-MM-dd"))}");
        Pair(ws, 4, 8, "진행률", p.Progress + "%");
        Pair(ws, 5, 1, "기준선", baseline);
        Pair(ws, 5, 4, "목표일 변경", p.TargetChanges + "회");
        Pair(ws, 5, 8, "임계 작업", p.CriticalCount + "건");

        const int head = 7;
        Header(ws, head, ("구분", 7), ("단계 · 작업", 34), ("담당", 16), ("상태", 10), ("진행률", 8),
            ("기준선 시작", 12), ("기준선 종료", 12), ("목표 시작", 12), ("목표 종료", 12), ("근무일", 8),
            ("실적 시작", 12), ("실적 종료", 12), ("기준선 대비(일)", 13), ("선행 작업", 22), ("메모", 30));

        var titles = p.Stages.SelectMany(s => s.Works).ToDictionary(w => w.Id, w => w.Title);
        // 선행 이름 뒤에 유형·간격 (끝나면 시작·간격 0 은 그대로 이름만 — 예전 양식과 같게)
        var links = p.Links.GroupBy(l => (l.From, l.To)).ToDictionary(g => g.Key, g => g.First());
        string Pre(int to, int from)
        {
            var name = titles.TryGetValue(from, out var t) ? t : "";
            if (!links.TryGetValue((from, to), out var l) || (l.Type == "fs" && l.Lag == 0)) return name;
            return $"{name}({Rules.LinkText(Rules.ParseLinkType(l.Type), l.Lag)})";
        }
        var r = head + 1;
        foreach (var s in p.Stages)
        {
            ws.Cell(r, 1).Value = "단계";
            ws.Cell(r, 2).Value = s.Name;
            State(ws.Cell(r, 4), s.Chip.Text);
            Percent(ws.Cell(r, 5), s.Progress);
            Date(ws.Cell(r, 7), s.BaselineDate);
            Date(ws.Cell(r, 8), s.PlanFrom);
            Date(ws.Cell(r, 9), s.TargetDate);
            Date(ws.Cell(r, 12), s.CompletedDate);
            Slip(ws.Cell(r, 13), s.SlipDays);
            if (s.CheckTotal > 0) ws.Cell(r, 15).Value = $"체크리스트 {s.CheckDone}/{s.CheckTotal}";
            ws.Range(r, 1, r, 15).Style.Fill.BackgroundColor = Soft;
            ws.Range(r, 1, r, 3).Style.Font.Bold = true;
            r++;

            foreach (var w in s.Works)
            {
                ws.Cell(r, 1).Value = "작업";
                ws.Cell(r, 1).Style.Font.FontColor = Muted;
                ws.Cell(r, 2).Value = w.Title;
                ws.Cell(r, 2).Style.Alignment.Indent = 1;
                ws.Cell(r, 3).Value = string.Join(", ", new[] { w.Owner, w.CoOwner }.Where(x => !string.IsNullOrEmpty(x)));
                State(ws.Cell(r, 4), w.Chip.Text);
                Percent(ws.Cell(r, 5), w.Progress);
                Date(ws.Cell(r, 6), w.BaselineStart);
                Date(ws.Cell(r, 7), w.BaselineEnd);
                Date(ws.Cell(r, 8), w.PlanStart);
                Date(ws.Cell(r, 9), w.PlanEnd);
                ws.Cell(r, 10).Value = w.PlanWorkDays;
                Date(ws.Cell(r, 11), w.ActualStart);
                Date(ws.Cell(r, 12), w.ActualEnd);
                Slip(ws.Cell(r, 13), w.SlipDays);
                ws.Cell(r, 14).Value = string.Join(", ", w.Predecessors.Select(id => Pre(w.Id, id))) + (w.Critical ? (w.Predecessors.Count > 0 ? " · " : "") + "임계" : "");
                ws.Cell(r, 15).Value = w.Note ?? "";
                r++;
            }
        }
        Body(ws, head, r - 1, 15, centered: new[] { 1, 4, 6, 7, 8, 9, 10, 11, 12, 13 });
        Print(ws, head);
    }

    // 주 단위 간트: 목표(회색)·실적(파랑)·지연(빨강)을 칸 색으로 표시
    private static void GanttSheet(XLWorkbook wb, ProjectView p, DateOnly today)
    {
        var dates = new List<DateOnly> { p.StartDate, today };
        foreach (var s in p.Stages)
        {
            if (s.TargetDate != null) dates.Add(s.TargetDate.Value);
            if (s.CompletedDate != null) dates.Add(s.CompletedDate.Value);
            foreach (var w in s.Works)
            {
                dates.Add(w.PlanStart);
                dates.Add(w.PlanEnd);
                if (w.ActualEnd != null) dates.Add(w.ActualEnd.Value);
            }
        }
        var first = Reporting.WeekOf(dates.Min()).From;
        var weeks = Math.Min(130, (Reporting.WeekOf(dates.Max()).From.DayNumber - first.DayNumber) / 7 + 2);

        var ws = wb.Worksheets.Add("간트");
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = p.Title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 12;
        ws.Cell(2, 1).Value = "단계 · 작업";
        ws.Cell(2, 2).Value = "담당";
        ws.Cell(2, 3).Value = "진행률";
        ws.Column(1).Width = 30;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 8;

        const int col0 = 4;
        var monthStart = col0;
        for (var i = 0; i < weeks; i++)
        {
            var monday = first.AddDays(i * 7);
            var c = col0 + i;
            ws.Column(c).Width = 3.4;
            ws.Cell(2, c).Value = monday.Day;
            // 월이 바뀌는 곳마다 윗줄에 "yyyy.MM" 을 병합해 적는다
            var last = i == weeks - 1;
            if (last || first.AddDays((i + 1) * 7).Month != monday.Month)
            {
                var from = first.AddDays((monthStart - col0) * 7);
                ws.Cell(1, monthStart).Value = from.ToString("yyyy.MM");
                if (c > monthStart) ws.Range(1, monthStart, 1, c).Merge();
                ws.Range(1, monthStart, 1, c).Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                ws.Range(1, monthStart, 1, c).Style.Border.LeftBorderColor = Line;
                monthStart = c + 1;
            }
        }
        var lastCol = col0 + weeks - 1;
        var headRange = ws.Range(2, 1, 2, lastCol);
        headRange.Style.Fill.BackgroundColor = Accent;
        headRange.Style.Font.FontColor = XLColor.White;
        headRange.Style.Font.Bold = true;
        headRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(1, col0, 2, lastCol).Style.Font.FontSize = 8;
        ws.Range(1, col0, 1, lastCol).Style.Font.FontColor = Muted;

        void Paint(int row, DateOnly from, DateOnly to, XLColor color)
        {
            if (to < from) return;
            var a = Math.Max(0, (from.DayNumber - first.DayNumber) / 7);
            var b = Math.Min(weeks - 1, (to.DayNumber - first.DayNumber) / 7);
            if (b < a) return;
            ws.Range(row, col0 + a, row, col0 + b).Style.Fill.BackgroundColor = color;
        }

        var r = 3;
        foreach (var s in p.Stages)
        {
            ws.Cell(r, 1).Value = s.Name;
            ws.Cell(r, 1).Style.Font.Bold = true;
            Percent(ws.Cell(r, 3), s.Progress);
            ws.Range(r, 1, r, 3).Style.Fill.BackgroundColor = Soft;
            if (s.PlanFrom != null && s.TargetDate != null) Paint(r, s.PlanFrom.Value, s.TargetDate.Value, Plan);
            if (s.ActualFrom != null && s.ActualTo != null)
            {
                if (s.CompletedDate != null) Paint(r, s.ActualFrom.Value, s.ActualTo.Value, s.DoneLate ? WarnMid : Accent);
                else
                {
                    Paint(r, s.ActualFrom.Value, s.ActualTo.Value, AccentMid);
                    if (s.Status == "late" && s.TargetDate != null) Paint(r, s.TargetDate.Value.AddDays(1), s.ActualTo.Value, Alarm);
                }
            }
            r++;

            foreach (var w in s.Works)
            {
                ws.Cell(r, 1).Value = w.Title;
                ws.Cell(r, 1).Style.Alignment.Indent = 1;
                ws.Cell(r, 2).Value = w.Owner ?? "";
                Percent(ws.Cell(r, 3), w.Progress);
                Paint(r, w.PlanStart, w.PlanEnd, Plan);
                if (w.ActualStart != null)
                {
                    if (w.ActualEnd != null) Paint(r, w.ActualStart.Value, w.ActualEnd.Value, w.DoneLate ? WarnMid : Accent);
                    else Paint(r, w.ActualStart.Value, today, AccentMid);
                }
                if (w.Late) Paint(r, w.PlanEnd.AddDays(1), today, Alarm);
                r++;
            }
        }

        var lastRow = Math.Max(3, r - 1);
        var grid = ws.Range(2, 1, lastRow, lastCol);
        grid.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        grid.Style.Border.OutsideBorderColor = Line;
        grid.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        grid.Style.Border.InsideBorderColor = Line;

        // 오늘이 속한 주에 굵은 테두리
        var todayCol = col0 + (today.DayNumber - first.DayNumber) / 7;
        if (todayCol >= col0 && todayCol <= lastCol)
        {
            var t = ws.Range(2, todayCol, lastRow, todayCol);
            t.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            t.Style.Border.LeftBorderColor = Accent;
            t.Style.Border.RightBorder = XLBorderStyleValues.Medium;
            t.Style.Border.RightBorderColor = Accent;
        }

        // 범례
        var lg = lastRow + 2;
        (string Text, XLColor Color)[] legend =
        {
            ("목표", Plan), ("실적(완료)", Accent), ("실적(진행 중)", AccentMid), ("목표 초과 완료", WarnMid), ("지연", Alarm),
        };
        for (var i = 0; i < legend.Length; i++)
        {
            ws.Cell(lg + i, 2).Style.Fill.BackgroundColor = legend[i].Color;
            ws.Cell(lg + i, 1).Value = legend[i].Text;
            ws.Cell(lg + i, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(lg + i, 1).Style.Font.FontColor = Muted;
        }
        ws.Cell(lg + legend.Length, 1).Value = "굵은 세로선 = 오늘이 속한 주, 한 칸 = 1주(월요일 날짜)";
        ws.Cell(lg + legend.Length, 1).Style.Font.FontColor = Muted;

        ws.SheetView.Freeze(2, 3);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
    }

    private static void ChecklistSheet(XLWorkbook wb, ProjectView p)
    {
        if (p.Stages.All(s => s.CheckTotal == 0)) return;
        var ws = wb.Worksheets.Add("체크리스트");
        Title(ws, "체크리스트", p.Title);
        const int head = 4;
        // 서버가 만드는 시각은 한국 시간이다 — 해외 법인에서 열어도 헷갈리지 않게 머리글에 적는다
        Header(ws, head, ("단계", 14), ("항목", 50), ("완료", 8), ("완료자", 12), ("완료 시각(한국 시간)", 20));
        var r = head + 1;
        foreach (var s in p.Stages)
        {
            foreach (var c in s.Checks)
            {
                ws.Cell(r, 1).Value = s.Name;
                ws.Cell(r, 2).Value = c.Text;
                State(ws.Cell(r, 3), c.Done ? "완료" : "미완료");
                ws.Cell(r, 4).Value = c.DoneBy ?? "";
                ws.Cell(r, 5).Value = c.DoneAt == null ? "" : KoreaClock.ToKst(c.DoneAt.Value).ToString("yyyy-MM-dd HH:mm");
                r++;
            }
        }
        Body(ws, head, r - 1, 5, centered: new[] { 3, 5 });
        Print(ws, head);
    }

    private static void ActivitySheet(XLWorkbook wb, ProjectView p)
    {
        var ws = wb.Worksheets.Add("활동");
        Title(ws, "활동 이력", p.Title);
        const int head = 4;
        Header(ws, head, ("일시(한국 시간)", 18), ("사용자", 12), ("내용", 70), ("사유", 46));
        var r = head + 1;
        foreach (var a in p.Activities ?? new())
        {
            ws.Cell(r, 1).Value = KoreaClock.ToKst(a.At).ToString("yyyy-MM-dd HH:mm");
            ws.Cell(r, 2).Value = a.User;
            ws.Cell(r, 3).Value = a.Text;
            ws.Cell(r, 4).Value = a.Reason ?? "";
            r++;
        }
        Body(ws, head, r - 1, 4, centered: new[] { 1 }, wrap: new[] { 3, 4 });
        Print(ws, head);
    }

    // ── 주간 보고 ────────────────────────────────────────────────────────
    // 화면과 같이: 변동·지연·밀린 항목이 있는 과제는 첫 시트에 한 줄씩, 나머지 무변동 과제는 '변동 없는 과제' 시트 한 장으로 접는다
    public byte[] Weekly(WeeklyReport report)
    {
        using var wb = NewWorkbook();
        var ws = wb.Worksheets.Add("주간 보고");
        var asOf = report.Past && report.AsOf != null ? $" · 지연은 {report.AsOf.Value:yyyy-MM-dd} 기준, 진행률·현재 단계는 작성일 기준" : "";
        Title(ws, $"주간 보고 ({report.From:yyyy-MM-dd} ~ {report.To:MM-dd})",
            $"진행 과제 {report.Active} · 지연 {report.Delayed} · 이번 주 완료 {report.DoneCount}건 · 일정 변경 {report.ChangeCount}건(대상 {report.ChangeTargets}건)" +
            $" · 상태·기준선 {report.StateCount}건 · 과제 완료 {report.Completed}건 · 작성일 {report.Today:yyyy-MM-dd}(한국 시간){asOf}");

        const int head = 4;
        Header(ws, head, ("과제", 30), ("유형", 12), ("법인", 9), ("담당", 9), ("상태", 12), ("진행률", 8), ("현재 단계", 16),
            ("이번 주 실적", 36), ("일정 변경 · 사유", 44), ("상태 · 기준선", 30), ("진행 내용", 38), ("밀린 항목", 32), ("다음 주 계획", 34));

        var r = head + 1;
        foreach (var p in report.Cards())
        {
            ws.Cell(r, 1).Value = p.Title;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = p.Category;
            ws.Cell(r, 3).Value = p.Corporation;
            ws.Cell(r, 4).Value = p.Owner;
            State(ws.Cell(r, 5), p.StateLabel + (p.DelayDays > 0 ? $" D+{p.DelayDays}" : ""));
            // 예상 지연(조기 경보)으로 카드에 오른 과제는 그 이유를 상태 칸 아래에 적는다(화면의 칩과 같은 글자) — 무변동인데 왜 접히지 않았는지 보이게
            if (p.RiskChip is { } risk) ws.Cell(r, 5).Value = ws.Cell(r, 5).GetString() + "\n" + risk.Text;
            Percent(ws.Cell(r, 6), p.Progress);
            ws.Cell(r, 7).Value = p.CurrentStage == null ? "" : p.CurrentStage + (p.CurrentTarget == null ? "" : $"\n목표 {p.CurrentTarget.Value:MM-dd}");
            ws.Cell(r, 8).Value = Lines(p.Done.Select(x => x.Text));
            // 결정 하나에 함께 옮겨진 대상은 그 아래 들여 적는다 (결정 수와 대상 수를 함께 읽을 수 있게)
            ws.Cell(r, 9).Value = Lines(p.Changes.Select(x => x.Text
                + (x.More == null ? "" : $"\n  함께 옮겨진 {x.More.Count}건: " + string.Join(", ", x.More))
                + (x.Reason == null ? "" : $"\n  사유: {x.Reason}")));
            ws.Cell(r, 10).Value = Lines((p.States ?? new()).Select(x => x.Text + (x.Reason == null ? "" : $"\n  사유: {x.Reason}")));
            ws.Cell(r, 11).Value = Lines(p.Comments.Select(x => $"{x.Text} ({x.User})"));
            ws.Cell(r, 12).Value = Lines((p.Overdue ?? new()).Select(x => $"{x.Kind} {x.Title} D+{x.LateDays}" + (x.Owner == null ? "" : $" ({x.Owner})")));
            ws.Cell(r, 13).Value = Lines(p.Next);
            r++;
        }
        Body(ws, head, r - 1, 13, centered: new[] { 5, 6 }, wrap: new[] { 1, 5, 7, 8, 9, 10, 11, 12, 13 }, top: true);
        Print(ws, head);

        var folded = report.Folded();
        if (folded.Count > 0)
        {
            var qs = wb.Worksheets.Add("변동 없는 과제");
            Title(qs, "변동 없는 과제", $"{report.From:yyyy-MM-dd} ~ {report.To:MM-dd} · 이번 주 기록된 변동·지연·밀린 항목이 없는 과제 {folded.Count}건");
            Header(qs, head, ("과제", 30), ("유형", 12), ("법인", 9), ("담당", 9), ("상태", 9), ("진행률", 8), ("현재 단계", 16), ("다음 주 계획", 40));
            var q = head + 1;
            foreach (var p in folded)
            {
                qs.Cell(q, 1).Value = p.Title;
                qs.Cell(q, 2).Value = p.Category;
                qs.Cell(q, 3).Value = p.Corporation;
                qs.Cell(q, 4).Value = p.Owner;
                State(qs.Cell(q, 5), p.StateLabel);
                Percent(qs.Cell(q, 6), p.Progress);
                qs.Cell(q, 7).Value = p.CurrentStage == null ? "" : p.CurrentStage + (p.CurrentTarget == null ? "" : $" ({p.CurrentTarget.Value:MM-dd})");
                qs.Cell(q, 8).Value = Lines(p.Next);
                q++;
            }
            Body(qs, head, q - 1, 8, centered: new[] { 5, 6 }, wrap: new[] { 1, 8 }, top: true);
            Print(qs, head);
        }
        return Save(wb);
    }

    // ── 가져오기 양식 ─────────────────────────────────────────────────────
    // 과제·작업 시트는 1행이 머리글이고 2행부터 적는다(ProjectImporter 가 머리글 이름으로 열을 찾는다).
    // 단계 목표일은 유형마다 단계가 달라 고정 열 대신 '단계명=날짜; 단계명=날짜' 한 칸에 적는다(유형별 시트보다 틀릴 곳이 적다)
    public byte[] ImportTemplate(ImportLists lists)
    {
        const int rows = 300;     // 날짜 서식·목록 고르기를 미리 걸어 두는 줄 수
        using var wb = NewWorkbook();
        var c = ProjectService.ImportColumns.Project;
        var ps = wb.Worksheets.Add(ProjectService.ImportColumns.ProjectSheet);
        Header(ps, 1, (c[0], 34), (c[1], 14), (c[2], 12), (c[3], 22), (c[4], 10), (c[5], 12), (c[6], 14), (c[7], 30), (c[8], 46));
        ps.Range(2, 6, rows + 1, 6).Style.DateFormat.Format = "yyyy-mm-dd";
        ps.Range(2, 1, rows + 1, c.Length).Style.NumberFormat.Format = "@";
        ps.Range(2, 6, rows + 1, 6).Style.NumberFormat.Format = "yyyy-mm-dd";

        var w = ProjectService.ImportColumns.Work;
        var ws = wb.Worksheets.Add(ProjectService.ImportColumns.WorkSheet);
        Header(ws, 1, (w[0], 34), (w[1], 12), (w[2], 30), (w[3], 10), (w[4], 12), (w[5], 12), (w[6], 24));
        ws.Range(2, 1, rows * 5 + 1, w.Length).Style.NumberFormat.Format = "@";
        ws.Range(2, 5, rows * 5 + 1, 6).Style.NumberFormat.Format = "yyyy-mm-dd";

        // 목록(참고용): 유형과 단계, 법인, 법인 아래 위치 경로, 사용자
        var ls = wb.Worksheets.Add(ProjectService.ImportColumns.ListSheet);
        Header(ls, 1, ("유형", 14), ("단계 (순서대로)", 50), ("", 2), ("법인", 12), ("", 2), ("법인", 12), ("위치 (법인 아래 경로)", 30), ("", 2), ("사용자", 14));
        for (var i = 0; i < lists.Categories.Count; i++)
        {
            ls.Cell(i + 2, 1).Value = lists.Categories[i].Name;
            ls.Cell(i + 2, 2).Value = string.Join(", ", lists.Categories[i].Stages);
        }
        var corps = lists.Locations.Select(l => l.Corporation).Distinct().ToList();
        for (var i = 0; i < corps.Count; i++) ls.Cell(i + 2, 4).Value = corps[i];
        for (var i = 0; i < lists.Locations.Count; i++)
        {
            ls.Cell(i + 2, 6).Value = lists.Locations[i].Corporation;
            ls.Cell(i + 2, 7).Value = lists.Locations[i].Path;
        }
        for (var i = 0; i < lists.Users.Count; i++) ls.Cell(i + 2, 9).Value = lists.Users[i];
        // 유형·법인 칸은 목록에서 고르게 한다(오타 방지). 목록이 비어 있으면 걸지 않는다
        if (lists.Categories.Count > 0)
            ps.Range(2, 2, rows + 1, 2).CreateDataValidation().List(ls.Range(2, 1, lists.Categories.Count + 1, 1), true);
        if (corps.Count > 0)
            ps.Range(2, 3, rows + 1, 3).CreateDataValidation().List(ls.Range(2, 4, corps.Count + 1, 4), true);

        var guide = wb.Worksheets.Add("안내");
        guide.ShowGridLines = false;
        guide.Column(1).Width = 16;
        guide.Column(2).Width = 100;
        var lines = new (string Key, string Text)[]
        {
            ("과제 시트", "한 줄에 과제 하나. 1행 머리글은 지우거나 바꾸지 마세요. 과제명·유형·법인·착수일은 꼭 적습니다."),
            ("유형", "'목록' 시트의 유형 이름 (칸을 누르면 고를 수 있습니다)."),
            ("법인 · 위치", "법인 이름과, 그 아래 위치를 '제품군>라인>층' 처럼 > 로 이어 적습니다. 위치를 비우면 법인 전체입니다."),
            ("담당", "사용자 이름. 비우면 올린 사람입니다. 목록에 없는 이름은 가져올 때 '새로 만들 담당자'로 확인을 받습니다."),
            ("착수일 · 날짜", "2026-10-12, 2026.10.12, 2026/10/12, 10/12(올해) 모두 됩니다. 날짜 서식 칸이면 그대로 읽습니다."),
            ("단계 목표일", "'단계명=날짜; 단계명=날짜' 처럼 한 칸에 적습니다. 예) 검토=2026-11-02; 품의=2026-11-16. 적지 않은 단계는 목표일 미정입니다."),
            ("작업 시트", "선택. 과제명(과제 시트와 같게)·단계명·작업명·담당·목표 시작·목표 종료·선행 작업명. 종료를 비우면 하루짜리 작업입니다."),
            ("선행 작업명", "같은 과제의 작업 이름(끝나면 시작). 여러 개는 ; 로 나눕니다. 선행이 끝나기 전에 시작하면 오류로 알려 줍니다."),
            ("템플릿 작업", "작업 시트에 그 과제의 줄이 없으면 유형의 템플릿 작업을 만듭니다. 줄이 있으면 그 줄만으로 만듭니다."),
            ("가져오기", "올리면 먼저 미리보기로 행·열마다 문제를 보여 줍니다. 문제가 하나라도 있으면 아무것도 등록하지 않습니다. " +
                         $"한 번에 과제 {ProjectService.MaxImportProjects}줄, 작업 {ProjectService.MaxImportWorks}줄까지."),
        };
        guide.Cell(1, 1).Value = "과제 가져오기 양식";
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Cell(1, 1).Style.Font.FontSize = 16;
        for (var i = 0; i < lines.Length; i++)
        {
            guide.Cell(i + 3, 1).Value = lines[i].Key;
            guide.Cell(i + 3, 1).Style.Font.Bold = true;
            guide.Cell(i + 3, 2).Value = lines[i].Text;
            guide.Cell(i + 3, 2).Style.Alignment.WrapText = true;
        }
        ps.SetTabActive();
        return Save(wb);
    }

    private static string Lines(IEnumerable<string> items) => string.Join("\n", items.Select(x => "· " + x));

    // ── 표시 문구 ────────────────────────────────────────────────────────
    // 상태 글자는 화면 칩과 같은 것을 쓴다(Views.ProjectChip·StageChip·WorkChip, ProjectView.StateLabel)
    public static string Signed(int days) => days > 0 ? "+" + days : days.ToString();

    // ── 양식 공통 ────────────────────────────────────────────────────────
    private static XLWorkbook NewWorkbook()
    {
        var wb = new XLWorkbook();
        wb.Style.Font.FontName = "맑은 고딕";
        wb.Style.Font.FontSize = 10;
        wb.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        return wb;
    }

    private static void Title(IXLWorksheet ws, string title, string sub)
    {
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Row(1).Height = 26;
        ws.Cell(2, 1).Value = sub;
        ws.Cell(2, 1).Style.Font.FontColor = Muted;
    }

    // 요약 줄의 "항목명 | 값" (값은 옆 칸까지 이어 쓴다)
    private static void Pair(IXLWorksheet ws, int row, int col, string key, string value)
    {
        ws.Cell(row, col).Value = key;
        ws.Cell(row, col).Style.Font.FontColor = Muted;
        ws.Cell(row, col + 1).Value = value;
        ws.Cell(row, col + 1).Style.Font.Bold = true;
    }

    private static void Header(IXLWorksheet ws, int row, params (string Title, double Width)[] columns)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            ws.Cell(row, i + 1).Value = columns[i].Title;
            ws.Column(i + 1).Width = columns[i].Width;
        }
        var head = ws.Range(row, 1, row, columns.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = Accent;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Row(row).Height = 22;
        ws.SheetView.FreezeRows(row);
    }

    // 표 본문 테두리·정렬·자동 필터.
    // top: 여러 줄 칸이 있는 표는 모든 칸을 위쪽에 맞춘다 (한 줄 칸만 가운데에 떠 보이지 않게)
    private static void Body(IXLWorksheet ws, int headRow, int lastRow, int columns, int[]? centered = null, int[]? wrap = null, bool top = false)
    {
        if (lastRow <= headRow)
        {
            ws.Cell(headRow + 1, 1).Value = "내용이 없습니다.";
            ws.Cell(headRow + 1, 1).Style.Font.FontColor = Muted;
            return;
        }
        var all = ws.Range(headRow, 1, lastRow, columns);
        all.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        all.Style.Border.OutsideBorderColor = Line;
        all.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        all.Style.Border.InsideBorderColor = Line;
        foreach (var c in centered ?? Array.Empty<int>())
            ws.Range(headRow + 1, c, lastRow, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        foreach (var c in wrap ?? Array.Empty<int>())
        {
            var range = ws.Range(headRow + 1, c, lastRow, c);
            range.Style.Alignment.WrapText = true;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }
        if (top) ws.Range(headRow + 1, 1, lastRow, columns).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        all.SetAutoFilter();
    }

    private static void Print(IXLWorksheet ws, int headRow)
    {
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(headRow, headRow);
        ws.PageSetup.Margins.Left = 0.4;
        ws.PageSetup.Margins.Right = 0.4;
        ws.PageSetup.Margins.Top = 0.5;
        ws.PageSetup.Margins.Bottom = 0.5;
    }

    // 상태 칸: 글자와 함께 연한 배경색
    private static void State(IXLCell cell, string label)
    {
        cell.Value = label;
        cell.Style.Font.Bold = true;
        var (fore, back) = label switch
        {
            _ when label.StartsWith("지연") || label == "미완료" => (Alarm, AlarmWeak),
            _ when label.StartsWith("완료") => (Ok, OkWeak),
            "초과 완료" => (Warn, WarnWeak),
            "진행" or "진행 중" => (Accent, AccentWeak),
            _ => (Muted, Soft),
        };
        cell.Style.Font.FontColor = fore;
        cell.Style.Fill.BackgroundColor = back;
    }

    private static void Percent(IXLCell cell, int progress)
    {
        cell.Value = progress / 100.0;
        cell.Style.NumberFormat.Format = "0%";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    // 기준선 대비 일수: 밀리면 빨강(+n), 당겨지면 초록(-n). 글자가 아니라 숫자로 넣고 표시 형식으로 부호를 붙인다 —
    // 글자('+3')면 정렬·필터가 사전순이 되고 합계를 낼 수 없다
    private static void Slip(IXLCell cell, int? days)
    {
        if (days == null) return;
        cell.Value = days.Value;
        cell.Style.NumberFormat.Format = "+0;-0;0";
        if (days.Value > 0) cell.Style.Font.FontColor = Alarm;
        else if (days.Value < 0) cell.Style.Font.FontColor = Ok;
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
