using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// ── Excel 가져오기의 자료 모양 (파일 형식과 무관 — ClosedXML 로 읽는 ProjectImporter 가 만든다) ──
// 열은 머리글 이름으로 찾는다(순서가 바뀌거나 열을 더 넣어도 된다). Row 는 시트의 행 번호(1부터, 머리글이 1행)
public record ImportRow(int Row, List<string> Cells);
public record ImportSheet(string Name, List<string> Headers, List<ImportRow> Rows);
public record ImportBook(ImportSheet? Projects, ImportSheet? Works);

// 양식의 '목록' 시트(참고용): 유형과 그 단계, 법인과 그 아래 위치 경로, 사용자
public record ImportCategoryRef(string Name, List<string> Stages);
public record ImportLocationRef(string Corporation, string Path);
public record ImportLists(List<ImportCategoryRef> Categories, List<ImportLocationRef> Locations, List<string> Users);

// 미리보기: 행·열 단위 문제와 '새로 만들 담당자' 목록. 문제가 하나라도 있으면 가져오지 않는다
public record ImportIssue(string Sheet, int Row, string? Column, string Message);
public record ImportProjectRow(int Row, string? Title, string? Category, string? Location, string? Owner, bool NewOwner, DateOnly? StartDate,
                               int Stages, int Targets, int Works, bool Ok);
public record ImportWorkRow(int Row, string? Project, string? Stage, string? Title, string? Owner, bool NewOwner,
                            DateOnly? PlanStart, DateOnly? PlanEnd, string? Predecessor, bool Ok);
public record ImportPreview(List<ImportProjectRow> Projects, List<ImportWorkRow> Works, List<ImportIssue> Issues, List<string> NewUsers)
{
    public bool Ok => Issues.Count == 0 && Projects.Count > 0;
}
public record ImportResult(int Projects, int Works, int NewUsers, List<int> Ids);

// Excel 가져오기: 과제(·작업)를 미리 검증한 뒤 한 번에 등록한다. 과제 한 건은 등록 화면과 같은 BuildProjectAsync,
// 작업 줄은 붙여넣기와 같은 CheckWorkRowsAsync·ApplyWorkRowsAsync 를 거친다
public partial class ProjectService
{
    public const int MaxImportProjects = 200;
    public const int MaxImportWorks = 2000;

    public static class ImportColumns
    {
        public const string ProjectSheet = "과제";
        public const string WorkSheet = "작업";
        public const string ListSheet = "목록";

        public const string Title = "과제명";
        public const string Category = "유형";
        public const string Corporation = "법인";
        public const string Location = "위치";
        public const string Owner = "담당";
        public const string StartDate = "착수일";
        public const string Vendor = "협력사";
        public const string Description = "설명";
        public const string StageTargets = "단계 목표일";
        public const string Stage = "단계명";

        public static readonly string[] Project = { Title, Category, Corporation, Location, Owner, StartDate, Vendor, Description, StageTargets };
        public static readonly string[] Work = { Title, Stage, WorkColumns.Title, WorkColumns.Owner, WorkColumns.Start, WorkColumns.End, WorkColumns.Predecessor };
    }

    // 양식의 '목록' 시트 — 사용 중인 유형·위치·사용자
    public async Task<ImportLists> ImportListsAsync()
    {
        var cats = await _db.Categories.AsNoTracking().Include(c => c.StageTemplates).Where(c => c.IsActive).ToListAsync();
        var index = await LocationsAsync();
        var locations = new List<ImportLocationRef>();
        foreach (var l in index.All.OrderBy(l => l.Level).ThenBy(l => l.SortOrder).ThenBy(l => l.Id))
        {
            var chain = index.Chain(l.Id);
            if (chain.Count == 0 || chain.Any(x => !x.IsActive)) continue;
            locations.Add(new ImportLocationRef(chain[0].Name, string.Join(">", chain.Skip(1).Select(x => x.Name))));
        }
        return new ImportLists(
            cats.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new ImportCategoryRef(c.Name, c.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => t.Name).ToList())).ToList(),
            locations.OrderBy(x => x.Corporation).ThenBy(x => x.Path).ToList(),
            await UserNamesAsync());
    }

    public async Task<ImportPreview> PreviewImportAsync(ImportBook book) => (await AnalyzeImportAsync(book, "")).Preview;

    // 미리보기와 같은 검사를 다시 하고, 문제가 없을 때만 모두 저장한다(SaveChanges 한 번 — 하나라도 틀리면 아무것도 남지 않는다).
    // 새 담당자가 있으면 confirmNewUsers 로 확인을 받아야 한다(오타로 사용자가 생기지 않게). fileName: 활동에 남길 출처
    public async Task<ImportResult> ImportAsync(ImportBook book, bool confirmNewUsers, string? fileName = null)
    {
        var (preview, built) = await AnalyzeImportAsync(book, fileName ?? "");
        if (preview.Issues.Count > 0)
            throw new RuleException($"문제가 {preview.Issues.Count}건 있어 아무것도 가져오지 않았습니다. 미리보기에서 고칠 곳을 확인해 주세요.");
        if (built.Count == 0) throw new RuleException("가져올 과제가 없습니다. '과제' 시트에 한 줄 이상 적어 주세요.");
        if (preview.NewUsers.Count > 0 && !confirmNewUsers)
            throw new RuleException($"새로 만들 담당자 {preview.NewUsers.Count}명({string.Join(", ", preview.NewUsers)})을 확인해 주세요.", "confirmNewUsers");

        foreach (var (p, plans) in built)
        {
            if (plans.Count > 0) await ApplyWorkRowsAsync(p, plans, Guid.NewGuid(), "Excel 가져오기");
            _db.Projects.Add(p);
        }
        await _db.SaveChangesAsync();
        // 작업 수는 템플릿 작업과 작업 시트로 만든 작업을 모두 센다
        return new ImportResult(built.Count, built.Sum(b => b.P.Stages.Sum(s => s.Works.Count)), preview.NewUsers.Count,
            built.Select(b => b.P.Id).ToList());
    }

    private async Task<(ImportPreview Preview, List<(Project P, List<WorkRowPlan> Plans)> Built)> AnalyzeImportAsync(ImportBook book, string fileName)
    {
        var issues = new List<ImportIssue>();
        var projectRows = new List<ImportProjectRow>();
        var workRows = new List<ImportWorkRow>();
        var built = new List<(Project P, List<WorkRowPlan> Plans)>();
        var newUsers = new SortedSet<string>(StringComparer.Ordinal);
        var empty = (new ImportPreview(projectRows, workRows, issues, new()), built);

        var ps = book.Projects;
        if (ps == null)
        {
            issues.Add(new(ImportColumns.ProjectSheet, 0, null, "'과제' 시트가 없습니다. 이 화면에서 내려받은 양식에 적어 주세요."));
            return empty;
        }
        var pcol = Columns(ps, ImportColumns.Project, new[] { ImportColumns.Title, ImportColumns.Category, ImportColumns.Corporation, ImportColumns.StartDate }, issues);
        var ws = book.Works;
        var wcol = ws == null ? null : Columns(ws, ImportColumns.Work, new[] { ImportColumns.Title, ImportColumns.Stage, WorkColumns.Title, WorkColumns.Start }, issues);
        if (issues.Count > 0) return empty;

        var pRows = ps.Rows.Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        var wRows = ws == null ? new List<ImportRow>() : ws.Rows.Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (pRows.Count > MaxImportProjects)
            issues.Add(new(ImportColumns.ProjectSheet, 0, null, $"과제는 한 번에 {MaxImportProjects}줄까지 가져올 수 있습니다(지금 {pRows.Count}줄). 나눠서 올려 주세요."));
        if (wRows.Count > MaxImportWorks)
            issues.Add(new(ImportColumns.WorkSheet, 0, null, $"작업은 한 번에 {MaxImportWorks}줄까지 가져올 수 있습니다(지금 {wRows.Count}줄). 나눠서 올려 주세요."));
        if (issues.Count > 0) return empty;

        var today = _clock.Today;
        var categories = (await _db.Categories.AsNoTracking().Include(c => c.StageTemplates).ToListAsync()).Where(c => c.IsActive).ToList();
        var index = await LocationsAsync();
        // 과제 시트의 과제명 → 만든 과제 (작업 시트가 과제명으로 찾는다)
        var byTitle = new Dictionary<string, (int Row, Project? P)>(StringComparer.Ordinal);
        var workTitles = wcol == null ? new HashSet<string>() : wRows.Select(r => Cell(r, wcol, ImportColumns.Title)).Where(t => t != null).Select(t => t!).ToHashSet();

        foreach (var r in pRows)
        {
            var before = issues.Count;
            void Issue(string? column, string message) => issues.Add(new(ImportColumns.ProjectSheet, r.Row, column, message));

            var title = Cell(r, pcol, ImportColumns.Title);
            if (title == null) Issue(ImportColumns.Title, "과제명을 입력해 주세요.");
            else if (byTitle.TryGetValue(title, out var dup)) Issue(ImportColumns.Title, $"같은 과제명이 {dup.Row}행에도 있습니다. 작업 시트가 과제명으로 찾으므로 다르게 적어 주세요.");

            var catName = Cell(r, pcol, ImportColumns.Category);
            var category = catName == null ? null : categories.FirstOrDefault(c => Same(c.Name, catName));
            if (catName == null) Issue(ImportColumns.Category, "유형을 입력해 주세요.");
            else if (category == null) Issue(ImportColumns.Category, $"유형 '{catName}' 을(를) 찾지 못했습니다. '목록' 시트의 유형 이름으로 적어 주세요.");

            var location = FindLocation(index, Cell(r, pcol, ImportColumns.Corporation), Cell(r, pcol, ImportColumns.Location), out var locError, out var locColumn);
            if (locError != null) Issue(locColumn, locError);

            var startText = Cell(r, pcol, ImportColumns.StartDate);
            DateOnly? start = null;
            if (startText == null) Issue(ImportColumns.StartDate, "착수일을 입력해 주세요.");
            else if (InputText.TryDate(startText, today, out var sd)) start = sd;
            else Issue(ImportColumns.StartDate, $"'{startText}' 을(를) 날짜로 읽지 못했습니다. 2026-10-12 처럼 적어 주세요.");

            var targets = new List<StageTargetInput>();
            if (category != null && Cell(r, pcol, ImportColumns.StageTargets) is { } targetText)
                foreach (var message in ParseStageTargets(targetText, category, today, targets))
                    Issue(ImportColumns.StageTargets, message);

            var owner = Cell(r, pcol, ImportColumns.Owner);
            Project? p = null;
            if (issues.Count == before)
            {
                var hasWorks = title != null && workTitles.Contains(title);
                var input = new ProjectInput(title, category!.Id, location!.Id, owner, start, Cell(r, pcol, ImportColumns.Vendor), Cell(r, pcol, ImportColumns.Description),
                    null, null, null, targets, WithTemplateWorks: !hasWorks);
                try
                {
                    // 작업 시트에 이 과제의 줄이 있으면 템플릿 작업 대신 그 줄로 만든다
                    p = await BuildProjectAsync(input, string.IsNullOrEmpty(fileName) ? "과제 등록 (Excel 가져오기)" : $"과제 등록 (Excel 가져오기: {Short(fileName, 100)})");
                }
                catch (RuleException ex)
                {
                    Issue(ColumnOf(ex.Field), ex.Message);
                }
            }
            if (title != null && !byTitle.ContainsKey(title)) byTitle[title] = (r.Row, p);
            var newOwner = p?.Owner is { Id: 0 };
            if (newOwner) newUsers.Add(p!.Owner!.Name);
            projectRows.Add(new ImportProjectRow(r.Row, title, category?.Name ?? catName,
                location == null ? Cell(r, pcol, ImportColumns.Corporation) : string.Join(" > ", index.Chain(location.Id).Select(l => l.Name)),
                p == null ? owner : (p.Owner?.Name ?? _me.Name), newOwner, start,
                p?.Stages.Count ?? 0, p?.Stages.Count(s => s.TargetDate != null) ?? 0, p?.Stages.Sum(s => s.Works.Count) ?? 0, p != null && issues.Count == before));
            if (p != null) built.Add((p, new List<WorkRowPlan>()));
        }

        // 작업 시트: 과제명·단계명으로 단계를 찾고, 과제마다 붙여넣기와 같은 검사를 한다
        if (wcol != null)
        {
            var groups = new Dictionary<Project, List<(WorkRowInput Row, Stage Stage)>>();
            var rowInfo = new Dictionary<int, (string? Project, string? Stage)>();
            foreach (var r in wRows)
            {
                var projectTitle = Cell(r, wcol, ImportColumns.Title);
                var stageName = Cell(r, wcol, ImportColumns.Stage);
                rowInfo[r.Row] = (projectTitle, stageName);
                var input = new WorkRowInput(Cell(r, wcol, WorkColumns.Title), Cell(r, wcol, WorkColumns.Owner), Cell(r, wcol, WorkColumns.Start),
                    Cell(r, wcol, WorkColumns.End), Cell(r, wcol, WorkColumns.Predecessor), r.Row);
                string? problem = null, column = null;
                Stage? stage = null;
                if (projectTitle == null) (problem, column) = ("과제명을 입력해 주세요.", ImportColumns.Title);
                else if (!byTitle.TryGetValue(projectTitle, out var target)) (problem, column) = ($"'과제' 시트에 없는 과제 '{projectTitle}' 입니다.", ImportColumns.Title);
                else if (target.P == null) (problem, column) = ($"과제 '{projectTitle}'({target.Row}행)에 문제가 있어 확인하지 못했습니다. 과제 행을 먼저 고쳐 주세요.", ImportColumns.Title);
                else if (stageName == null) (problem, column) = ("단계명을 입력해 주세요.", ImportColumns.Stage);
                else if ((stage = target.P.Stages.FirstOrDefault(s => Same(s.Name, stageName))) == null)
                    (problem, column) = ($"단계 '{stageName}' 이(가) 이 과제의 유형에 없습니다 (단계: {string.Join(", ", target.P.Stages.OrderBy(s => s.SortOrder).Select(s => s.Name))}).", ImportColumns.Stage);
                if (problem != null)
                {
                    issues.Add(new(ImportColumns.WorkSheet, r.Row, column, problem));
                    workRows.Add(new ImportWorkRow(r.Row, projectTitle, stageName, input.Title, input.Owner, false, null, null, input.Predecessor, false));
                    continue;
                }
                var p = byTitle[projectTitle!].P!;
                if (!groups.TryGetValue(p, out var list)) groups[p] = list = new();
                list.Add((input, stage!));
            }
            foreach (var (p, rows) in groups)
            {
                var plans = await CheckWorkRowsAsync(p, rows);
                foreach (var x in plans)
                {
                    foreach (var i in x.Issues) issues.Add(new(ImportColumns.WorkSheet, x.Input.Line, i.Column, i.Message));
                    if (x.NewOwner) newUsers.Add(x.Owner!);
                    var info = rowInfo[x.Input.Line];
                    workRows.Add(new ImportWorkRow(x.Input.Line, info.Project, x.Stage.Name, x.Title, x.Owner, x.NewOwner, x.Start, x.End, x.Input.Predecessor, x.Issues.Count == 0));
                }
                var at = built.FindIndex(b => b.P == p);
                built[at] = (p, plans);
            }
            workRows.Sort((a, b) => a.Row.CompareTo(b.Row));
        }

        issues.Sort((a, b) => a.Sheet == b.Sheet ? a.Row.CompareTo(b.Row) : a.Sheet == ImportColumns.ProjectSheet ? -1 : 1);
        if (projectRows.Count == 0 && issues.Count == 0)
            issues.Add(new(ImportColumns.ProjectSheet, 0, null, "가져올 과제가 없습니다. '과제' 시트에 한 줄 이상 적어 주세요."));
        return (new ImportPreview(projectRows, workRows, issues, newUsers.ToList()), built);
    }

    // 머리글 이름 → 열 위치 (공백·대소문자 무시). 꼭 있어야 하는 열이 없으면 문제로 남긴다
    private static Dictionary<string, int> Columns(ImportSheet sheet, string[] known, string[] required, List<ImportIssue> issues)
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < sheet.Headers.Count; i++)
        {
            var h = Key(sheet.Headers[i]);
            var name = known.FirstOrDefault(k => Key(k) == h);
            if (name != null && !map.ContainsKey(name)) map[name] = i;
        }
        foreach (var name in required.Where(n => !map.ContainsKey(n)))
            issues.Add(new(sheet.Name, 1, name, $"'{sheet.Name}' 시트에 '{name}' 열이 없습니다. 양식의 머리글(1행)을 지우거나 바꾸지 말아 주세요."));
        return map;
    }

    private static string Key(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static bool Same(string a, string b) => Key(a) == Key(b);

    private static string? Cell(ImportRow r, Dictionary<string, int> cols, string name) =>
        cols.TryGetValue(name, out var i) && i < r.Cells.Count && !string.IsNullOrWhiteSpace(r.Cells[i]) ? r.Cells[i].Trim() : null;

    private static string Short(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    // 법인 이름 + 그 아래 경로('세탁기>2라인' — '>'·'/'·'·' 로 나눔). 사용 중지 항목은 고를 수 없다
    private static Location? FindLocation(LocationIndex index, string? corpName, string? path, out string? error, out string column)
    {
        error = null;
        column = ImportColumns.Corporation;
        if (corpName == null) { error = "법인을 입력해 주세요."; return null; }
        var corp = index.All.Where(l => l.Level == 1 && Same(l.Name, corpName)).OrderBy(l => l.IsActive ? 0 : 1).FirstOrDefault();
        if (corp == null) { error = $"법인 '{corpName}' 을(를) 찾지 못했습니다. '목록' 시트의 법인 이름으로 적어 주세요."; return null; }
        if (!corp.IsActive) { error = $"법인 '{corp.Name}' 은(는) 사용이 중지되었습니다."; return null; }
        var cur = corp;
        if (path == null) return cur;
        column = ImportColumns.Location;
        foreach (var part in path.Split(new[] { '>', '/', '·' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var next = index.All.Where(l => l.ParentId == cur.Id && Same(l.Name, part)).OrderBy(l => l.IsActive ? 0 : 1).FirstOrDefault();
            if (next == null) { error = $"'{cur.Name}' 아래에 '{part}' 이(가) 없습니다. '목록' 시트의 위치 경로로 적어 주세요."; return null; }
            if (!next.IsActive) { error = $"위치 '{next.Name}' 은(는) 사용이 중지되었습니다."; return null; }
            cur = next;
        }
        return cur;
    }

    // '단계명=날짜; 단계명=날짜' (줄바꿈·세미콜론으로 나눔, '=' 대신 ':' 도 됨). 문제는 문구로 돌려준다
    private static IEnumerable<string> ParseStageTargets(string text, Category category, DateOnly today, List<StageTargetInput> into)
    {
        var problems = new List<string>();
        foreach (var part in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = part.IndexOfAny(new[] { '=', ':' });
            if (cut <= 0) { problems.Add($"'{part}' — '단계명=날짜' 형식으로 적어 주세요 (여러 단계는 ; 로 나눕니다)."); continue; }
            var name = part[..cut].Trim();
            var dateText = part[(cut + 1)..].Trim();
            var t = category.StageTemplates.FirstOrDefault(x => Same(x.Name, name));
            if (t == null) { problems.Add($"단계 '{name}' 이(가) 유형 '{category.Name}' 에 없습니다."); continue; }
            if (!InputText.TryDate(dateText, today, out var d)) { problems.Add($"'{name}' 목표일 '{dateText}' 을(를) 날짜로 읽지 못했습니다."); continue; }
            into.Add(new StageTargetInput(t.Id, d));
        }
        return problems;
    }

    // 서비스 오류의 field → 과제 시트의 열 이름
    private static string? ColumnOf(string? field) => field switch
    {
        "title" => ImportColumns.Title,
        "categoryId" => ImportColumns.Category,
        "locationId" => ImportColumns.Corporation,
        "owner" => ImportColumns.Owner,
        "startDate" => ImportColumns.StartDate,
        "vendor" => ImportColumns.Vendor,
        "description" => ImportColumns.Description,
        { } f when f.StartsWith("stageTargets", StringComparison.Ordinal) => ImportColumns.StageTargets,
        _ => null,
    };
}
