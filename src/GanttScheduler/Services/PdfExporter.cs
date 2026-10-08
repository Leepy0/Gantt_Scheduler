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

        Heading(sec, p.Title, $"과제 일정 · 출력일 {today:yyyy-MM-dd}");

        var baseline = p.BaselineAt == null ? "미확정"
            : $"{KoreaClock.ToKst(p.BaselineAt.Value):yyyy-MM-dd} 확정" + (p.SlipDays == null ? "" : $" · 목표 종료 {ExcelExporter.Signed(p.SlipDays.Value)}일");
        var info = sec.AddTable();
        info.Format.Font.Size = 9;
        foreach (var w in new[] { 2.0, 6.9, 2.0, 6.9, 2.0, 6.9 }) info.AddColumn(Unit.FromCentimeter(w));
        InfoRow(info, "유형", p.Category, "위치", p.Corporation + (p.LocationPath.Length > 0 ? " · " + p.LocationPath : ""),
            "담당", p.Owner + (p.OwnerOrg == null ? "" : $" ({p.OwnerOrg})"));
        InfoRow(info, "상태", ExcelExporter.StateLabel(p) + (p.DelayDays > 0 ? $" D+{p.DelayDays}" : ""),
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
            Cell(row, 3, ExcelExporter.StageLabel(s), center: true, color: StateColor(ExcelExporter.StageLabel(s)));
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
                Cell(wr, 3, ExcelExporter.WorkLabel(w), center: true, color: StateColor(ExcelExporter.WorkLabel(w)));
                Cell(wr, 4, w.Progress + "%", right: true);
                Cell(wr, 5, D(w.BaselineEnd), center: true);
                Cell(wr, 6, D(w.PlanStart), center: true);
                Cell(wr, 7, D(w.PlanEnd), center: true);
                Cell(wr, 8, D(w.ActualStart), center: true);
                Cell(wr, 9, D(w.ActualEnd), center: true);
                SlipCell(wr, 10, w.BaselineEnd == null ? null : w.PlanEnd.DayNumber - w.BaselineEnd.Value.DayNumber);
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

        Heading(sec, "주간 보고", $"{report.From:yyyy-MM-dd} ~ {report.To:yyyy-MM-dd} · 작성일 {report.Today:yyyy-MM-dd}");

        var sum = NewTable(sec, 9, ("진행 과제", 4.5), ("지연", 4.5), ("이번 주 완료", 4.5), ("일정 변경", 4.5));
        var sr = sum.AddRow();
        sr.Format.Font.Size = 14;
        sr.Format.Font.Bold = true;
        Cell(sr, 0, report.Active.ToString(), center: true);
        Cell(sr, 1, report.Delayed.ToString(), center: true, color: report.Delayed > 0 ? Alarm : Text);
        Cell(sr, 2, report.DoneCount + "건", center: true);
        Cell(sr, 3, report.ChangeCount + "건", center: true);
        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);

        foreach (var p in report.Projects)
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
            meta.AddText($" · 진행률 {p.Progress}%" + (p.CurrentStage == null ? "" : $" · 현재 {p.CurrentStage}" + (p.CurrentTarget == null ? "" : $" (목표 {p.CurrentTarget.Value:MM-dd})")));

            var t = sec.AddTable();
            t.Borders.Width = 0.5;
            t.Borders.Color = Line;
            t.Format.Font.Size = 8.5;
            t.TopPadding = 3;
            t.BottomPadding = 3;
            t.AddColumn(Unit.FromCentimeter(2.8));
            t.AddColumn(Unit.FromCentimeter(15.2));

            var any = false;
            any |= Block(t, "이번 주 실적", p.Done.Select(x => $"{x.Text}  ({x.Date:MM-dd})"));
            any |= Block(t, "일정 변경", p.Changes.Select(x => x.Text + (x.Reason == null ? "" : $"\n사유: {x.Reason}") + $"  ({x.User})"));
            any |= Block(t, "진행 내용", p.Comments.Select(x => $"{x.Text}  ({x.User} {x.Date:MM-dd})"));
            any |= Block(t, "다음 주 계획", p.Next);
            if (!any) Block(t, "변동", new[] { "이번 주 기록된 변동이 없습니다." });
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
        sec.PageSetup = doc.DefaultPageSetup.Clone();
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
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var regular = FirstExisting(_cfg["Pdf:Font"],
                Path.Combine(windows, "malgun.ttf"),
                "/usr/share/fonts/truetype/nanum/NanumGothic.ttf",
                "/usr/share/fonts/truetype/nanum/NanumBarunGothic.ttf")
                ?? throw new RuleException("PDF 에 쓸 한글 글꼴(TTF)을 찾지 못했습니다. 설정 Pdf:Font 에 글꼴 파일 경로를 지정해 주세요.");
            var bold = FirstExisting(_cfg["Pdf:FontBold"],
                Path.Combine(windows, "malgunbd.ttf"),
                "/usr/share/fonts/truetype/nanum/NanumGothicBold.ttf",
                "/usr/share/fonts/truetype/nanum/NanumBarunGothicBold.ttf");

            GlobalFontSettings.FontResolver = new FileFontResolver(File.ReadAllBytes(regular), bold == null ? null : File.ReadAllBytes(bold));
            _fontReady = true;
        }
    }

    private static string? FirstExisting(params string?[] paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

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
