using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 붙여넣기·가져오기의 작업 한 줄 (사람이 적은 글자 그대로). Line: 원래 글·시트의 줄 번호(오류 안내에 쓴다)
// PredecessorStage: 선행 작업이 있는 단계(비우면 이 작업과 같은 단계). 등록 CSV 는 '선행 단계'와 '선행 작업'을 나눠 적는다
public record WorkRowInput(string? Title, string? Owner, string? PlanStart, string? PlanEnd, string? Predecessor, int Line = 0, string? PredecessorStage = null);

// 상세 단계의 '작업 여러 개 추가'. Text: 붙여넣은 글(미리보기), Rows: 미리보기가 돌려준 줄(저장)
public record PreviewWorksInput(int StageId, string? Text);
public record AddWorksInput(int StageId, List<WorkRowInput>? Rows, int? StageVersion = null);

// 한 줄의 문제 — Column 은 그 칸의 머리글(작업명·담당·목표 시작·목표 종료·선행 작업명)
public record RowIssue(string Column, string Message);

// 해석·검증한 작업 한 줄. NewOwner: 사용자 목록에 없는 담당(저장하면 새로 등록된다). WorkDays: 근무일 기간
public record WorkRowView(int Line, string? Title, string? Owner, bool NewOwner, DateOnly? PlanStart, DateOnly? PlanEnd,
                          string? Predecessor, int? WorkDays, List<RowIssue> Issues)
{
    public bool Ok => Issues.Count == 0;
}

public record WorksPreview(int StageId, string StageName, int StageVersion, List<WorkRowView> Rows, int ErrorCount, List<string> NewOwners);

// 작업 여러 개 한 번에 추가 (붙여넣기) — 해석·검증은 Excel 가져오기의 '작업' 시트와 같은 코드를 쓴다
public partial class ProjectService
{
    public const int MaxPasteWorks = 200;

    public static class WorkColumns
    {
        public const string Title = "작업명";
        public const string Owner = "담당";
        public const string Start = "목표 시작";
        public const string End = "목표 종료";
        public const string Predecessor = "선행 작업명";
        public const string PredecessorStage = "선행 단계";
    }

    // 붙여넣은 글을 줄로 나눈다: 작업명, 담당, 목표 시작, 목표 종료[, 선행 작업명] (Excel 에서 복사하면 TAB, 아니면 쉼표).
    // 첫 줄이 머리글이면 건너뛴다. 담당 칸을 빼고 붙여도(둘째 칸이 날짜) 알아본다
    public List<WorkRowInput> ParseWorkText(string? text)
    {
        var rows = new List<WorkRowInput>();
        var today = _clock.Today;
        foreach (var (line, raw) in InputText.Lines(text))
        {
            var c = InputText.Cells(raw);
            string? At(int i) => i < c.Count && c[i].Length > 0 ? c[i] : null;
            if (rows.Count == 0 && (At(0) ?? "").Contains("작업") && !InputText.TryDate(At(2), today, out _) && !InputText.TryDate(At(1), today, out _))
                continue;
            rows.Add(InputText.TryDate(At(1), today, out _)
                ? new WorkRowInput(At(0), null, At(1), At(2), At(3), line)
                : new WorkRowInput(At(0), At(1), At(2), At(3), At(4), line));
        }
        return rows;
    }

    // 붙여넣기 미리보기: 줄마다 해석 결과와 문제(날짜 형식·순서·범위, 사용 중지 담당, 선행 이름 불일치·순환·일정)를 돌려준다. 저장하지 않는다
    public async Task<WorksPreview> PreviewWorksAsync(PreviewWorksInput input)
    {
        var rows = ParseWorkText(input.Text);
        if (rows.Count == 0) throw new RuleException("붙여 넣을 내용을 입력해 주세요. 한 줄에 작업 하나씩입니다.", "text");
        if (rows.Count > MaxPasteWorks) throw new RuleException($"한 번에 {MaxPasteWorks}줄까지 넣을 수 있습니다.", "text");
        var p = await RequireByStageAsync(input.StageId);
        var stage = p.Stages.First(s => s.Id == input.StageId);
        var plans = await CheckWorkRowsAsync(p, rows.Select(r => (r, stage)).ToList());
        var views = plans.Select(x => x.View()).ToList();
        return new WorksPreview(stage.Id, stage.Name, stage.Version, views, views.Count(v => !v.Ok),
            views.Where(v => v.NewOwner).Select(v => v.Owner!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList());
    }

    // 미리보기에서 확인한 줄을 저장한다. 다시 검증해 하나라도 틀리면 아무것도 저장하지 않고(SaveChanges 한 번),
    // 모든 작업 추가·선행 연결 활동을 BatchId 하나로 묶는다. stageVersion: 미리보기 때 본 단계 버전(그 사이 단계가 바뀌었으면 409)
    public async Task<int> AddWorksAsync(AddWorksInput input)
    {
        var rows = input.Rows ?? new();
        if (rows.Count == 0) throw new RuleException("추가할 작업이 없습니다.", "rows");
        if (rows.Count > MaxPasteWorks) throw new RuleException($"한 번에 {MaxPasteWorks}줄까지 넣을 수 있습니다.", "rows");
        var p = await RequireByStageAsync(input.StageId);
        var stage = p.Stages.First(s => s.Id == input.StageId);
        CheckVersion(input.StageVersion, stage.Version, p.Id);

        var plans = await CheckWorkRowsAsync(p, rows.Select(r => (r, stage)).ToList());
        var bad = plans.Where(x => x.Issues.Count > 0).ToList();
        if (bad.Count > 0)
        {
            var first = bad[0];
            throw new RuleException($"{first.Input.Line}번째 줄: {first.Issues[0].Message}" + (bad.Count > 1 ? $" (문제 있는 줄 {bad.Count}개)" : ""), "rows");
        }
        await ApplyWorkRowsAsync(p, plans, Guid.NewGuid(), "여러 개 추가");
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // ── 공통: 작업 줄 검증·적용 (붙여넣기·Excel 가져오기) ──────────────────

    // 검증한 줄. 선행은 같은 묶음의 다른 줄(Row) 또는 과제에 이미 있는 작업(Existing)
    private sealed class WorkRowPlan
    {
        public required WorkRowInput Input { get; init; }
        public required Stage Stage { get; init; }
        public string? Title { get; set; }
        public string? Owner { get; set; }
        public bool NewOwner { get; set; }
        public DateOnly? Start { get; set; }
        public DateOnly? End { get; set; }
        public int? WorkDays { get; set; }
        public List<RowIssue> Issues { get; } = new();
        public List<(WorkRowPlan? Row, WorkItem? Existing)> Preds { get; } = new();
        public WorkItem? Work { get; set; }

        public WorkRowView View() => new(Input.Line, Title, Owner, NewOwner, Start, End, Input.Predecessor?.Trim(), WorkDays, Issues);
    }

    private async Task<List<WorkRowPlan>> CheckWorkRowsAsync(Project p, List<(WorkRowInput Row, Stage Stage)> rows)
    {
        var cal = await CalendarAsync();
        var today = _clock.Today;
        // 사용자 표는 작다 — 이름 목록 조건(IN)을 쓰지 않고 이름·사용 여부만 읽는다(DESIGN 6장).
        // 대소문자는 구분하지 않는다 — 저장할 때 담당을 찾는 UserService.ResolveAsync 와 같은 규칙이어야 미리보기와 저장 결과가 같다
        var users = (await _db.Users.AsNoTracking().Select(u => new { u.Name, u.IsActive }).ToListAsync())
            .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().IsActive, StringComparer.OrdinalIgnoreCase);
        var plans = rows.Select(r => new WorkRowPlan { Input = r.Row, Stage = r.Stage }).ToList();

        foreach (var x in plans)
        {
            var r = x.Input;
            x.Title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim();
            if (x.Title == null) x.Issues.Add(new(WorkColumns.Title, "작업명을 입력해 주세요."));
            else if (x.Title.Length > Limits.WorkTitle) x.Issues.Add(new(WorkColumns.Title, $"작업명은 {Limits.WorkTitle}자 이내로 입력해 주세요."));

            x.Owner = string.IsNullOrWhiteSpace(r.Owner) ? null : r.Owner.Trim();
            if (x.Owner != null)
            {
                if (x.Owner.Length > Limits.UserName) x.Issues.Add(new(WorkColumns.Owner, $"담당자 이름은 {Limits.UserName}자 이내로 입력해 주세요."));
                else if (users.TryGetValue(x.Owner, out var active)) { if (!active) x.Issues.Add(new(WorkColumns.Owner, $"'{x.Owner}' 은(는) 사용이 중지된 사용자입니다.")); }
                else x.NewOwner = true;
            }

            x.Start = ReadDate(x, r.PlanStart, WorkColumns.Start, today, required: true);
            // 종료를 비우면 하루짜리 작업(시작일과 같은 날)
            x.End = string.IsNullOrWhiteSpace(r.PlanEnd) ? x.Start : ReadDate(x, r.PlanEnd, WorkColumns.End, today, required: false);
            if (x.Start != null && x.End != null)
            {
                if (x.Start > x.End) x.Issues.Add(new(WorkColumns.End, "목표 시작일이 종료일보다 늦습니다."));
                else
                {
                    TryRule(x, WorkColumns.Start, () => Rules.ValidateDate(x.Start, p.StartDate, "목표 시작일", "planStart"));
                    TryRule(x, WorkColumns.End, () => Rules.ValidateDate(x.End, p.StartDate, "목표 종료일", "planEnd"));
                    x.WorkDays = Math.Max(1, cal.WorkingDays(x.Start.Value, x.End.Value));
                }
            }
        }

        // 선행: (단계, 작업명) 쌍. 단계를 적었으면 그 단계의 작업만, 비우면 같은 단계 작업을 먼저 본다(없으면 다른 단계에서 하나뿐일 때).
        // 같은 묶음의 다른 줄을 먼저 보고, 없으면 과제에 이미 있는 작업
        var existing = p.Stages.SelectMany(st => st.Works.Select(w => (Work: w, Stage: st))).ToList();
        foreach (var x in plans)
        {
            var text = x.Input.Predecessor?.Trim();
            var stageText = x.Input.PredecessorStage?.Trim();
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(stageText)) continue;
            if (string.IsNullOrEmpty(text))
            {
                x.Issues.Add(new(WorkColumns.Predecessor, $"선행 단계 '{stageText}' 의 선행 작업도 적어 주세요."));
                continue;
            }
            var names = PredecessorNames(text, plans, existing.Select(e => e.Work));
            var stages = string.IsNullOrEmpty(stageText) ? new List<string>() : SplitList(stageText);
            if (stages.Count > 1 && stages.Count != names.Count)
            {
                x.Issues.Add(new(WorkColumns.PredecessorStage, "선행 단계와 선행 작업의 개수가 다릅니다. 단계는 하나만 적거나 작업마다 적어 주세요."));
                continue;
            }
            for (var k = 0; k < names.Count; k++)
            {
                var name = names[k];
                var stageName = stages.Count == 0 ? null : stages.Count == 1 ? stages[0] : stages[k];
                var label = stageName == null ? $"'{name}'" : $"'{stageName}' 단계의 '{name}'";
                bool InStage(Stage st) => stageName == null || Same(st.Name, stageName);
                if (stageName != null && !p.Stages.Any(InStage)) { x.Issues.Add(new(WorkColumns.PredecessorStage, $"선행 단계 '{stageName}' 을(를) 이 과제에서 찾지 못했습니다.")); continue; }

                var inRows = plans.Where(o => o != x && o.Title == name && InStage(o.Stage)).ToList();
                if (inRows.Count > 1 && stageName == null) inRows = inRows.Where(o => o.Stage == x.Stage).ToList() is { Count: 1 } same ? same : inRows;
                if (inRows.Count > 1) { x.Issues.Add(new(WorkColumns.Predecessor, $"선행 작업 {label} 이(가) 붙여넣은 줄에 여러 개 있습니다. 이름을 다르게 하거나 선행 단계를 적어 주세요.")); continue; }
                if (inRows.Count == 1) { x.Preds.Add((inRows[0], null)); continue; }
                if (x.Title == name && InStage(x.Stage)) { x.Issues.Add(new(WorkColumns.Predecessor, "자기 자신을 선행으로 둘 수 없습니다.")); continue; }
                var found = existing.Where(e => e.Work.Title == name && InStage(e.Stage)).ToList();
                if (found.Count > 1 && stageName == null) found = found.Where(e => ReferenceEquals(e.Stage, x.Stage)).ToList() is { Count: 1 } same ? same : found;
                if (found.Count == 1) x.Preds.Add((null, found[0].Work));
                else x.Issues.Add(new(WorkColumns.Predecessor, found.Count == 0
                    ? $"선행 작업 {label} 을(를) 이 과제에서 찾지 못했습니다."
                    : $"선행 작업 {label} 이(가) 과제에 여러 개 있습니다. 선행 단계를 적어 주세요."));
            }
        }

        // 붙여넣은 줄끼리의 순환 (이미 있는 작업은 새 줄을 선행으로 둘 수 없으므로 순환은 묶음 안에서만 생긴다)
        var remaining = plans.Where(x => x.Preds.Any(pr => pr.Row != null)).ToHashSet();
        bool progress;
        do
        {
            progress = false;
            foreach (var x in remaining.ToList())
                if (x.Preds.All(pr => pr.Row == null || !remaining.Contains(pr.Row))) { remaining.Remove(x); progress = true; }
        } while (progress);
        foreach (var x in remaining) x.Issues.Add(new(WorkColumns.Predecessor, "선행 관계가 순환합니다."));

        // 선행 일정: 아직 시작하지 않은 새 작업은 '끝나면 시작' 선행이 허용하는 날 이후에 시작해야 한다(작업 시트의 새 작업과 같은 규칙).
        // 완료한 단계의 작업은 밀기 대상이 아니므로 보지 않는다(Rules.IsMovable)
        foreach (var x in plans)
        {
            if (x.Start == null || x.End == null || x.WorkDays == null || !Rules.IsMovable((DateOnly?)null, x.Stage)) continue;
            foreach (var (row, work) in x.Preds)
            {
                DateOnly? ps = row?.Start ?? work?.PlanStart, pe = row?.End ?? work?.PlanEnd;
                if (ps == null || pe == null) continue;
                var earliest = Rules.EarliestStart(new WorkLink { Type = LinkType.FS }, ps.Value, pe.Value, x.WorkDays.Value, cal);
                if (Rules.StartsBefore(x.Start.Value, earliest, cal))
                    x.Issues.Add(new(WorkColumns.Start, $"선행 작업 '{row?.Title ?? work!.Title}' 이(가) {Fmt(pe)} 에 끝나므로 {Fmt(earliest)} 이후에 시작해야 합니다."));
            }
        }
        return plans;
    }

    // 선행 칸의 이름들. 칸 전체가 작업 이름이면 그 하나, 아니면 쉼표·세미콜론으로 나눈다(이름에 쉼표가 있어도 통째로 맞으면 그대로)
    private static List<string> PredecessorNames(string text, List<WorkRowPlan> plans, IEnumerable<WorkItem> existing)
    {
        if (plans.Any(x => x.Title == text) || existing.Any(w => w.Title == text)) return new List<string> { text };
        return SplitList(text);
    }

    private static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private static DateOnly? ReadDate(WorkRowPlan x, string? text, string column, DateOnly today, bool required)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required) x.Issues.Add(new(column, $"{column}일을 입력해 주세요."));
            return null;
        }
        if (InputText.TryDate(text, today, out var d)) return d;
        x.Issues.Add(new(column, $"'{text.Trim()}' 을(를) 날짜로 읽지 못했습니다. 2026-10-12, 2026.10.12, 10/12 처럼 적어 주세요."));
        return null;
    }

    private static void TryRule(WorkRowPlan x, string column, Action check)
    {
        try { check(); }
        catch (RuleException ex) { x.Issues.Add(new(column, ex.Message)); }
    }

    // 검증을 통과한 줄을 작업으로 만든다(저장하지 않는다). 담당은 사용자 확인(ResolveAsync — 새 이름은 같은 저장에서 함께 등록),
    // 선행은 '끝나면 시작'·간격 0. 활동은 작업마다 '작업 추가', 연결마다 '선행 연결 추가'를 batch 하나로 남긴다.
    // 끝으로 밀기 계산을 한 번 더 돌려 옮겨야 할 작업이 없는지 본다(검증과 저장이 같은 규칙인지 지키는 안전장치)
    private async Task<List<WorkItem>> ApplyWorkRowsAsync(Project p, List<WorkRowPlan> plans, Guid batch, string source)
    {
        var created = new List<WorkItem>();
        foreach (var x in plans)
        {
            var owner = await _users.ResolveAsync(x.Owner);
            var w = new WorkItem { Title = x.Title!, Owner = owner, PlanStart = x.Start!.Value, PlanEnd = x.End!.Value, CreatedAt = _clock.UtcNow };
            x.Stage.Works.Add(w);
            x.Work = w;
            created.Add(w);
            var added = NewActivity(ActivityType.WorkAdded, $"[{x.Stage.Name}] 작업 추가: {w.Title} ({source})",
                stageId: x.Stage.Id == 0 ? null : x.Stage.Id, batch: batch);
            p.Activities.Add(added);
            InvolveOwners(added, owner?.Id, null);
        }
        foreach (var x in plans)
            foreach (var (row, work) in x.Preds)
            {
                var from = row?.Work ?? work!;
                if (p.Links.Any(l => ReferenceEquals(l.Predecessor, from) && ReferenceEquals(l.Successor, x.Work))) continue;
                p.Links.Add(new WorkLink { Predecessor = from, Successor = x.Work!, Type = LinkType.FS, LagDays = 0 });
                p.Activities.Add(NewActivity(ActivityType.LinkChanged, $"[{x.Work!.Title}] 선행 연결 추가: {from.Title} ({Rules.LinkText(LinkType.FS, 0)})",
                    stageId: x.Stage.Id == 0 ? null : x.Stage.Id, batch: batch));
            }

        var cal = await CalendarAsync();
        if (Rules.Reschedule(p, cal, created).FirstOrDefault() is { } move)
            throw new RuleException($"작업 '{move.Work.Title}' 이(가) 선행 일정보다 이르게 잡혀 있습니다. {Fmt(move.NewStart)} 이후로 고쳐 주세요.", "rows");
        return created;
    }
}
