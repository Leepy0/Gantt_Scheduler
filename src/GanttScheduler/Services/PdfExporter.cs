using GanttScheduler.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace GanttScheduler.Services;

// PDF 내보내기 (PDFsharp/MigraDoc, MIT 라이선스).
// 한글이 들어가므로 TTF 글꼴 파일이 필요하다: Windows 는 맑은 고딕, Linux 는 나눔고딕을 찾고,
// 다른 글꼴을 쓰려면 설정 Pdf:Font / Pdf:FontBold 에 파일 경로를 지정한다
public class PdfExporter
{
    public const string ContentType = "application/pdf";
    private const string FontName = "ReportFont";

    private static readonly object Gate = new();
    private static bool _fontReady;

    private static readonly Color Accent = new(20, 40, 160);
    private static readonly Color Line = new(213, 218, 225);
    private static readonly Color Soft = new(241, 243, 245);
    private static readonly Color Muted = new(99, 106, 119);
    private static readonly Color Alarm = new(198, 40, 40);
    private static readonly Color Ok = new(19, 122, 59);
    private static readonly Color Warn = new(138, 82, 0);
    private static readonly Color White = new(255, 255, 255);
    private static readonly Color Text = new(22, 24, 29);

    private readonly IConfiguration _cfg;

    public PdfExporter(IConfiguration cfg) => _cfg = cfg;

    // ── 과제 일정 (A4 가로) ──────────────────────────────────────────────
    public byte[] ProjectSchedule(ProjectView p, DateOnly today)
    {
        var doc = NewDocument(p.Title, landscape: true);
        var sec = doc.LastSection;

        Heading(sec, p.Title, $"과제 일정 · 출력일 {today:yyyy-MM-dd} · 날짜는 한국 시간 기준");

        var baseline = p.BaselineAt == null ? "미확정"
            : $"{KoreaClock.ToKst(p.BaselineAt.Value):yyyy-MM-dd} 확정" + (p.SlipDays == null ? "" : $" · 목표 종료 {ExcelExporter.Signed(p.SlipDays.Value)}일");
        var info = sec.AddTable();
        info.Format.Font.Size = 9;
        foreach (var w in new[] { 2.0, 6.9, 2.0, 6.9, 2.0, 6.9 }) info.AddColumn(Unit.FromCentimeter(w));
        InfoRow(info, "유형", p.Category, "위치", p.Corporation + (p.LocationPath.Length > 0 ? " · " + p.LocationPath : ""),
            "담당", p.Owner + (p.OwnerOrg == null ? "" : $" ({p.OwnerOrg})"));
        InfoRow(info, "상태", p.Chip.Text,
            "기간", $"{p.StartDate:yyyy-MM-dd} ~ {(p.EndTarget == null ? "미정" : p.EndTarget.Value.ToString("yyyy-MM-dd"))}",
            "진행률", p.Progress + "%");
        InfoRow(info, "기준선", baseline, "목표일 변경", p.TargetChanges + "회", "임계 작업", p.CriticalCount + "건");
        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);

        var t = NewTable(sec, 8,
            ("구분", 1.2), ("단계 · 작업", 6.6), ("담당", 2.8), ("상태", 2.0), ("진행률", 1.4),
            ("기준선 종료", 2.2), ("목표 시작", 2.2), ("목표 종료", 2.2), ("실적 시작", 2.2), ("실적 종료", 2.2), ("대비(일)", 1.5));

        foreach (var s in p.Stages)
        {
            var row = t.AddRow();
            row.Shading.Color = Soft;
            row.Format.Font.Bold = true;
            Cell(row, 0, "단계", center: true);
            Cell(row, 1, s.Name + (s.CheckTotal > 0 ? $"  (체크 {s.CheckDone}/{s.CheckTotal})" : ""));
            Cell(row, 3, s.Chip.Text, center: true, color: StateColor(s.Chip.Text));
            Cell(row, 4, s.Progress + "%", right: true);
            Cell(row, 5, D(s.BaselineDate), center: true);
            Cell(row, 6, D(s.PlanFrom), center: true);
            Cell(row, 7, D(s.TargetDate), center: true);
            Cell(row, 9, D(s.CompletedDate), center: true);
            SlipCell(row, 10, s.SlipDays);

            foreach (var w in s.Works)
            {
                var wr = t.AddRow();
                Cell(wr, 0, "작업", center: true, color: Muted);
                Cell(wr, 1, "  " + w.Title + (w.Critical ? "  [임계]" : ""));
                Cell(wr, 2, string.Join(", ", new[] { w.Owner, w.CoOwner }.Where(x => !string.IsNullOrEmpty(x))));
                Cell(wr, 3, w.Chip.Text, center: true, color: StateColor(w.Chip.Text));
                Cell(wr, 4, w.Progress + "%", right: true);
                Cell(wr, 5, D(w.BaselineEnd), center: true);
                Cell(wr, 6, D(w.PlanStart), center: true);
                Cell(wr, 7, D(w.PlanEnd), center: true);
                Cell(wr, 8, D(w.ActualStart), center: true);
                Cell(wr, 9, D(w.ActualEnd), center: true);
                SlipCell(wr, 10, w.SlipDays);
            }
        }
        if (p.Stages.Count == 0) Empty(t, 11, "단계가 없습니다.");

        return Render(doc);
    }

    // ── 주간 보고 (A4 세로) ──────────────────────────────────────────────
    public byte[] Weekly(WeeklyReport report)
    {
        var doc = NewDocument("주간 보고", landscape: false);
        var sec = doc.LastSection;

        Heading(sec, "주간 보고", $"{report.From:yyyy-MM-dd} ~ {report.To:yyyy-MM-dd} · 작성일 {report.Today:yyyy-MM-dd}(한국 시간)");

        // 일정 변경은 결정 단위(묶음은 1건)로 세고 옮겨진 대상 수를 함께 적는다. 상태·기준선과 자동 완료는 따로 센다
        var sum = NewTable(sec, 9, ("진행 과제", 3.0), ("지연", 3.0), ("이번 주 완료", 3.0), ("일정 변경", 3.0), ("상태·기준선", 3.0), ("과제 완료", 3.0));
        var sr = sum.AddRow();
        sr.Format.Font.Size = 14;
        sr.Format.Font.Bold = true;
        Cell(sr, 0, report.Active.ToString(), center: true);
        Cell(sr, 1, report.Delayed.ToString(), center: true, color: report.Delayed > 0 ? Alarm : Text);
        Cell(sr, 2, report.DoneCount + "건", center: true);
        Cell(sr, 3, report.ChangeCount + "건", center: true);
        Cell(sr, 4, report.StateCount + "건", center: true);
        Cell(sr, 5, report.Completed + "건", center: true);
        var subRow = sum.AddRow();
        subRow.Format.Font.Size = 7.5;
        subRow.Format.Font.Color = Muted;
        Cell(subRow, 3, $"대상 {report.ChangeTargets}건", center: true);
        if (report.Past && report.AsOf != null)
        {
            var note = sec.AddParagraph($"지연·밀린 항목은 {report.AsOf.Value:yyyy-MM-dd}(그 주 마지막 날) 기준입니다. 진행률·현재 단계는 작성일 기준입니다.");
            note.Format.Font.Size = 8;
            note.Format.Font.Color = Muted;
            note.Format.SpaceBefore = Unit.FromPoint(4);
        }
        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);

        foreach (var p in report.Cards())
        {
            var head = sec.AddParagraph();
            head.Format.SpaceBefore = Unit.FromPoint(10);
            head.Format.SpaceAfter = Unit.FromPoint(2);
            head.Format.KeepWithNext = true;
            var title = head.AddFormattedText(p.Title);
            title.Font.Size = 11;
            title.Font.Bold = true;

            var meta = sec.AddParagraph();
            meta.Format.Font.Size = 8.5;
            meta.Format.Font.Color = Muted;
            meta.Format.SpaceAfter = Unit.FromPoint(3);
            meta.Format.KeepWithNext = true;
            meta.AddText($"{p.Category} · {p.Corporation} · 담당 {p.Owner} · ");
            var state = meta.AddFormattedText(p.StateLabel + (p.DelayDays > 0 ? $" D+{p.DelayDays}" : ""));
            state.Font.Bold = true;
            state.Font.Color = StateColor(p.StateLabel);
            // 예상 지연(조기 경보)으로 카드에 오른 과제는 그 이유를 화면 칩과 같은 글자로 붙인다 — 무변동인데 왜 접히지 않았는지 보이게
            if (p.RiskChip is { } risk)
            {
                meta.AddText(" · ");
                var warn = meta.AddFormattedText(risk.Text);
                warn.Font.Bold = true;
                warn.Font.Color = Warn;
            }
            meta.AddText($" · 진행률 {p.Progress}%" + (p.CurrentStage == null ? "" : $" · 현재 {p.CurrentStage}" + (p.CurrentTarget == null ? "" : $" (목표 {p.CurrentTarget.Value:MM-dd})")));

            var t = sec.AddTable();
            t.Borders.Width = 0.5;
            t.Borders.Color = Line;
            t.Format.Font.Size = 8.5;
            t.TopPadding = 3;
            t.BottomPadding = 3;
            t.AddColumn(Unit.FromCentimeter(2.8));
            t.AddColumn(Unit.FromCentimeter(15.2));

            if (p.Quiet) Block(t, "변동", new[] { "이번 주 기록된 변동이 없습니다." });
            Block(t, "이번 주 실적", p.Done.Select(x => $"{x.Text}  ({x.Date:MM-dd})"));
            Block(t, p.ChangeTargets > p.Changes.Count ? $"일정 변경 {p.Changes.Count}건\n(대상 {p.ChangeTargets}건)" : "일정 변경",
                p.Changes.Select(x => x.Text
                    + (x.More == null ? "" : $"\n  함께 옮겨진 {x.More.Count}건: " + string.Join(", ", x.More))
                    + (x.Reason == null ? "" : $"\n사유: {x.Reason}") + $"  ({x.User})"));
            Block(t, "상태·기준선", (p.States ?? new()).Select(x => x.Text + (x.Reason == null ? "" : $"\n사유: {x.Reason}") + $"  ({x.User})"));
            Block(t, "진행 내용", p.Comments.Select(x => $"{x.Text}  ({x.User} {x.Date:MM-dd})"));
            Block(t, "밀린 항목", (p.Overdue ?? new()).Select(x => $"[D+{x.LateDays}] {x.Kind} {x.Title} · 목표 {x.Due:MM-dd}" + (x.Owner == null ? "" : $" ({x.Owner})")));
            Block(t, "다음 주 계획", p.Next);
        }

        // 무변동 과제는 끝에 표 한 장으로 접는다(화면·Excel 과 같다)
        var folded = report.Folded();
        if (folded.Count > 0)
        {
            var qh = sec.AddParagraph($"변동 없는 과제 {folded.Count}건");
            qh.Format.Font.Size = 11;
            qh.Format.Font.Bold = true;
            qh.Format.SpaceBefore = Unit.FromPoint(12);
            qh.Format.SpaceAfter = Unit.FromPoint(4);
            qh.Format.KeepWithNext = true;
            var qt = NewTable(sec, 8, ("과제", 6.2), ("담당", 2.2), ("상태", 1.8), ("진행률", 1.6), ("현재 단계", 2.6), ("다음 주 계획", 3.6));
            foreach (var p in folded)
            {
                var row = qt.AddRow();
                Cell(row, 0, p.Title);
                Cell(row, 1, p.Owner);
                Cell(row, 2, p.StateLabel, center: true, color: StateColor(p.StateLabel));
                Cell(row, 3, p.Progress + "%", right: true);
                Cell(row, 4, (p.CurrentStage ?? "") + (p.CurrentTarget == null ? "" : $" ({p.CurrentTarget.Value:MM-dd})"));
                Cell(row, 5, string.Join("\n", p.Next));
            }
        }

        if (report.Projects.Count == 0)
        {
            var none = sec.AddParagraph("이 기간에 해당하는 과제가 없습니다.");
            none.Format.Font.Color = Muted;
        }
        return Render(doc);
    }

    // 주간 보고의 한 구획: 왼쪽 제목, 오른쪽 항목 목록. 항목이 없으면 만들지 않는다
    private static bool Block(Table t, string label, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return false;
        var row = t.AddRow();
        row.Cells[0].Shading.Color = Soft;
        var lp = row.Cells[0].AddParagraph(label);
        lp.Format.Font.Bold = true;
        foreach (var item in list)
        {
            var para = row.Cells[1].AddParagraph("· " + item);
            para.Format.SpaceAfter = Unit.FromPoint(1.5);
        }
        return true;
    }

    // ── 양식 공통 ────────────────────────────────────────────────────────
    private Document NewDocument(string title, bool landscape)
    {
        EnsureFont();
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = "과제 스케줄러";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontName;
        normal.Font.Size = 9;
        normal.Font.Color = Text;

        var sec = doc.AddSection();
        // DefaultPageSetup 을 복제하면 세로 기준 쪽 크기가 그대로 박혀 가로 설정이 먹지 않는다.
        // 용지와 방향만 정하고 크기 계산은 MigraDoc 에 맡긴다
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.Orientation = landscape ? Orientation.Landscape : Orientation.Portrait;
        sec.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        sec.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);

        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Right;
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = Muted;
        footer.AddText("과제 스케줄러 · ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();
        return doc;
    }

    private static void Heading(Section sec, string title, string sub)
    {
        var h = sec.AddParagraph(title);
        h.Format.Font.Size = 15;
        h.Format.Font.Bold = true;
        h.Format.SpaceAfter = Unit.FromPoint(2);
        var s = sec.AddParagraph(sub);
        s.Format.Font.Size = 9;
        s.Format.Font.Color = Muted;
        s.Format.SpaceAfter = Unit.FromPoint(8);
        s.Format.Borders.Bottom.Width = 1.5;
        s.Format.Borders.Bottom.Color = Accent;
        s.Format.Borders.DistanceFromBottom = Unit.FromPoint(6);
    }

    private static void InfoRow(Table t, params string[] pairs)
    {
        var row = t.AddRow();
        row.TopPadding = 2;
        row.BottomPadding = 2;
        for (var i = 0; i < pairs.Length && i < 6; i++)
        {
            var para = row.Cells[i].AddParagraph(pairs[i]);
            if (i % 2 == 0) para.Format.Font.Color = Muted;
            else para.Format.Font.Bold = true;
        }
    }

    // 머리글이 있는 표 (머리글은 쪽마다 반복)
    private static Table NewTable(Section sec, double fontSize, params (string Title, double Cm)[] columns)
    {
        var t = sec.AddTable();
        t.Borders.Width = 0.5;
        t.Borders.Color = Line;
        t.Format.Font.Size = fontSize;
        t.TopPadding = 2.5;
        t.BottomPadding = 2.5;
        foreach (var c in columns) t.AddColumn(Unit.FromCentimeter(c.Cm));

        var head = t.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Accent;
        head.Format.Font.Color = White;
        head.Format.Font.Bold = true;
        head.Format.Alignment = ParagraphAlignment.Center;
        for (var i = 0; i < columns.Length; i++) head.Cells[i].AddParagraph(columns[i].Title);
        return t;
    }

    private static void Cell(Row row, int index, string text, bool center = false, bool right = false, Color? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var para = row.Cells[index].AddParagraph(text);
        if (center) para.Format.Alignment = ParagraphAlignment.Center;
        if (right) para.Format.Alignment = ParagraphAlignment.Right;
        if (color != null) para.Format.Font.Color = color.Value;
    }

    private static void SlipCell(Row row, int index, int? days)
    {
        if (days == null) return;
        Cell(row, index, ExcelExporter.Signed(days.Value), center: true, color: days.Value > 0 ? Alarm : days.Value < 0 ? Ok : Text);
    }

    private static void Empty(Table t, int columns, string text)
    {
        var row = t.AddRow();
        row.Cells[0].MergeRight = columns - 1;
        var para = row.Cells[0].AddParagraph(text);
        para.Format.Alignment = ParagraphAlignment.Center;
        para.Format.Font.Color = Muted;
    }

    private static Color StateColor(string label) =>
        label.StartsWith("지연") ? Alarm
        : label.StartsWith("완료") ? Ok
        : label == "초과 완료" ? Warn
        : label.StartsWith("진행") ? Accent
        : Muted;

    private static string D(DateOnly? d) => d == null ? "" : d.Value.ToString("yyyy-MM-dd");

    private static byte[] Render(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    // ── 글꼴 ─────────────────────────────────────────────────────────────
    private void EnsureFont()
    {
        lock (Gate)
        {
            if (_fontReady) return;
            // 찾는 규칙은 서버 상태 점검과 같다 (PdfFonts)
            var (found, bold) = PdfFonts.Find(_cfg);
            var regular = found
                ?? throw new RuleException("PDF 에 쓸 한글 글꼴(TTF)을 찾지 못했습니다. 설정 Pdf:Font 에 글꼴 파일 경로를 지정해 주세요.");

            GlobalFontSettings.FontResolver = new FileFontResolver(File.ReadAllBytes(regular), bold == null ? null : File.ReadAllBytes(bold));
            _fontReady = true;
        }
    }

    // 어떤 글꼴 이름을 요청받아도 준비한 한글 글꼴 하나로 답한다
    private sealed class FileFontResolver : IFontResolver
    {
        private readonly byte[] _regular;
        private readonly byte[]? _bold;

        public FileFontResolver(byte[] regular, byte[]? bold)
        {
            _regular = regular;
            _bold = bold;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (!bold) return new FontResolverInfo("regular");
            // 굵은 글꼴 파일이 없으면 보통 글꼴을 굵게 흉내 낸다
            return _bold != null ? new FontResolverInfo("bold") : new FontResolverInfo("regular", true, false);
        }

        public byte[]? GetFont(string faceName) => faceName == "bold" ? _bold : _regular;
    }
}
