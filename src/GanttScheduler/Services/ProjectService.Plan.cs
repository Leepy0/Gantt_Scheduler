using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 선행 연결 입력 하나: 선행 작업 Id, 간격(근무일), 유형('fs'·'ss'·'ff', 비우면 fs)
public record LinkInput(int PredecessorId, int LagDays = 0, string? Type = null);

// ── 저장 전 미리보기 (What-if) ───────────────────────────────────────────
// 미리보기는 저장할 때와 같은 검사·같은 계산(Rules.Reschedule·ForwardPass)을 과제의 복사본에 적용한 결과다. 저장·활동은 없다

// 함께 옮겨지는 작업 하나. Shift: 시작이 옮겨진 근무일 수(늦어지면 양수)
public record PreviewMove(int Id, string Title, string Stage, DateOnly OldStart, DateOnly OldEnd, DateOnly NewStart, DateOnly NewEnd,
                          int Shift, int Version, bool StartLate = false);

// 단계 하나의 전/후: 목표일(연쇄 이동이면 바뀜)과 작업 기준 예상 종료(Rules.ForwardPass), 목표일을 넘는 일수.
// Late: 바뀐 뒤에도 미완료인데 목표일이 이미 지남(지연 — 화면은 빨강, 넘기만 하면 노랑)
public record PreviewStage(int Id, string Name, DateOnly? Target, DateOnly? NewTarget, DateOnly? Before, DateOnly? After,
                           int OverBefore, int OverAfter, bool Late);

public record ForecastChange(DateOnly? Before, DateOnly? After);

// Blocking: 저장하면 이 문구로 거절된다(field 는 그 입력). 아니면 저장은 되지만 알아 둘 일(이미 시작한 후행과 어긋남, 다른 과제 단계와 어긋남)
public record PreviewIssue(string Text, bool Blocking, string? Field = null);

public record PlanPreview
{
    public int? WorkDays { get; init; }                 // 작업 미리보기: 바뀐 목표 기간의 근무일 수
    public int? Shift { get; init; }                    // 단계 미리보기: 목표일이 옮겨지는 근무일 수
    public List<PreviewMove> Moved { get; init; } = new();
    public List<PreviewStage> Stages { get; init; } = new();
    public ForecastChange? StageForecast { get; init; } // 고친 작업·단계가 속한 단계의 예상 종료 전/후
    public int ForecastOver { get; init; }              // 그 단계의 예상 종료가 (바뀐) 목표일을 넘는 일수
    public List<PreviewIssue> Violations { get; init; } = new();
    public List<string> Notes { get; init; } = new();   // 휴일 안내 (서버 달력 기준)
}

// 작업 시트의 실적 입력. 미리보기가 저장(SaveWorkAsync)과 같은 실적 시작으로 밀기 대상 여부·선행 검사·예상 종료를 정하게 한다 —
// 진행률만 올리면 접속자의 오늘(ClientToday) 시작, 실적 종료가 있으면 완료. 비운 실적 시작은 null 로 온다
public record PreviewActuals(DateOnly? ActualStart, DateOnly? ActualEnd, int Progress, DateOnly? ClientToday = null);

// 재계획 저장: 미리보기에서 본 이동(작업·버전·새 일정)을 함께 보내 그사이 일정이 바뀌었으면 다시 보게 한다
public record ReplanRow(int WorkId, int? Version, DateOnly PlanStart, DateOnly PlanEnd);

public record ReplanInput(int ProjectId, string? Reason, int? Version = null, List<ReplanRow>? Rows = null);

// 일정 변경 미리보기·오늘 기준 재계획
public partial class ProjectService
{
    private Dictionary<DateOnly, string>? _holidayNames;
    private readonly Dictionary<WorkItem, int> _readVersions = new();      // 미리보기 복사본 작업 → 읽은 때의 버전

    // ── 미리보기: 작업 목표 일정 ─────────────────────────────────────────

    // 작업의 목표 일정을 from~to 로 바꾸면(선택: 다른 단계로 옮기거나 선행 연결을 바꾸면) 무엇이 달라지는가.
    // workId 가 없으면 stageId 단계에 새로 넣는 작업으로 본다. links 가 null 이면 지금 연결 그대로.
    // actuals 가 null 이면 실적은 저장된 값으로 본다(끌기처럼 일정만 바꾸는 시트). 작업 시트는 고친 실적을 보낸다 —
    // 실적 시작이 있으면 밀지도 선행 날짜를 검사하지도 않으므로, 저장된 값으로 보면 미리보기와 저장 결과가 어긋난다
    public async Task<PlanPreview> PreviewWorkPlanAsync(int? workId, DateOnly from, DateOnly to, int? stageId = null, List<LinkInput>? links = null,
                                                        PreviewActuals? actuals = null)
    {
        var targetStageId = stageId;
        if (workId != null)
        {
            var current = await _db.WorkItems.AsNoTracking().Where(w => w.Id == workId.Value).Select(w => new { w.StageId }).FirstOrDefaultAsync()
                ?? throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
            targetStageId ??= current.StageId;
        }
        if (targetStageId == null) throw new RuleException("단계를 선택해 주세요.", "stageId");
        var p = await PlanGraphAsync(await ProjectOfStageAsync(targetStageId.Value));
        var stage = p.Stages.FirstOrDefault(s => s.Id == targetStageId.Value)
            ?? throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.", "stageId");
        var cal = await CalendarAsync();
        var today = _clock.Today;
        var all = Rules.AllWorks(p).ToDictionary(w => w.Id);

        WorkItem w;
        if (workId == null) w = new WorkItem { Title = "새 작업", StageId = stage.Id, PlanStart = from, PlanEnd = to };
        else if (!all.TryGetValue(workId.Value, out w!)) throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        var isNew = workId == null;

        var notes = new List<string>();
        if (await DayNoteAsync(from, "시작일") is { } n1) notes.Add(n1);
        if (await DayNoteAsync(to, "종료일") is { } n2) notes.Add(n2);
        var result = new PlanPreview { WorkDays = from <= to ? cal.WorkingDays(from, to) : null, Notes = notes };

        // 실적: 저장과 같은 식으로 정한다(종료가 있으면 100%, 진행률만 올렸으면 오늘 시작)
        var actualStart = w.ActualStart;
        var actualEnd = w.ActualEnd;
        var progress = w.Progress;
        if (actuals != null)
        {
            actualEnd = actuals.ActualEnd;
            (progress, actualStart) = Rules.WorkActuals(actuals.Progress, actuals.ActualStart, actuals.ActualEnd, actuals.ClientToday, today);
        }

        // 저장(SaveWorkAsync)과 같은 순서로 검사한다 — 저장이 거절할 내용이면 막는 문제로 알린다
        List<LinkSpec> wanted;
        var planChanged = isNew || w.PlanStart != from || w.PlanEnd != to;
        try
        {
            if (from > to) throw new RuleException("목표 시작일이 종료일보다 늦습니다.", "planStart");
            if (actuals != null)
            {
                Rules.ValidateActuals(actuals.ActualStart, actuals.ActualEnd, actuals.Progress);
            }
            ValidateWorkDates(p, isNew ? null : w, from, to, actualStart, actualEnd);
            var movable = Rules.IsMovable(actualStart, stage);
            var becameMovable = !isNew && movable && !Rules.IsMovable(w, p.Stages.First(s => s.Works.Contains(w)));
            wanted = ValidatePredecessors(p, isNew ? null : w, all, WantedLinks(p, isNew ? null : w, links, null), from, to, movable,
                planChanged || becameMovable, cal);
        }
        catch (RuleException ex)
        {
            return result with { Violations = new() { new PreviewIssue(ex.Message, true, ex.Field) } };
        }

        var before = Snapshot(p, cal, today);
        // 실적은 '이미 시작한 후행과 어긋남' 비교 전에 넣는다 — 그 안내는 일정 변경이 새로 만든 어긋남만 알린다.
        // 실적 시작이 생긴 작업은 밀기(Reschedule)에서도 빠진다(저장과 같다)
        (w.ActualStart, w.ActualEnd, w.Progress) = (actualStart, actualEnd, progress);
        var conflictsBefore = StartedConflicts(p, cal);

        // 복사본에 저장과 같은 변경을 적용한다: 단계 이동, 목표 일정, 선행 연결
        var home = p.Stages.FirstOrDefault(s => s.Works.Contains(w));
        if (!ReferenceEquals(home, stage))
        {
            home?.Works.Remove(w);
            stage.Works.Add(w);
            w.StageId = stage.Id;
        }
        w.PlanStart = from;
        w.PlanEnd = to;
        var linksChanged = ReplaceLinks(p, w, all, wanted);

        var plan = new PlanBatch();
        if (planChanged || linksChanged)
            foreach (var m in Rules.Reschedule(p, cal, new[] { w }))
                plan.Move(m.Work, m.NewStart, m.NewEnd, "", "");
        try
        {
            // 함께 옮겨지는 작업의 날짜 범위도 저장과 같이 본다 (복사본이라 남기는 활동은 버려진다)
            CommitPlan(p, plan, Guid.Empty, "planEnd");
        }
        catch (RuleException ex)
        {
            return result with { Violations = new() { new PreviewIssue(ex.Message, true, ex.Field) } };
        }

        var after = Snapshot(p, cal, today);
        var issues = NewConflicts(p, conflictsBefore, StartedConflicts(p, cal), all);
        var stages = StageRows(p, before, after, today, s => ReferenceEquals(s, stage) || plan.Entries.Any(x => s.Works.Contains(x.Work)));
        var main = stages.First(x => x.Id == stage.Id);
        return result with
        {
            Moved = Moves(p, plan, cal),
            Stages = stages,
            StageForecast = new ForecastChange(main.Before, main.After),
            ForecastOver = main.OverAfter,
            Violations = issues,
        };
    }

    // ── 미리보기: 단계 목표일 ───────────────────────────────────────────

    // 단계 목표일을 date 로 바꾸면(선택: 이후 미완료 단계 연쇄 이동, 이후 단계의 미착수 작업 포함) 무엇이 달라지는가.
    // SaveStageAsync 의 목표일 분기와 같은 계산이다 — 처음 정하는 목표일(지금 없음)이나 지우는 경우는 아무것도 옮기지 않는다
    public async Task<PlanPreview> PreviewStageTargetAsync(int stageId, DateOnly? date, bool cascade, bool includeWorks)
    {
        var p = await PlanGraphAsync(await ProjectOfStageAsync(stageId));
        var s = p.Stages.FirstOrDefault(x => x.Id == stageId) ?? throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        var cal = await CalendarAsync();
        var today = _clock.Today;
        var notes = new List<string>();
        if (await DayNoteAsync(date, "목표일") is { } note) notes.Add(note);

        var targets = p.Stages.ToDictionary(x => x.Id, x => x.TargetDate);
        var before = Snapshot(p, cal, today);
        var old = s.TargetDate;
        var delta = old == null || date == null ? 0 : cal.Distance(old.Value, date.Value);
        var result = new PlanPreview { Shift = old == null || date == null ? null : delta, Notes = notes };
        var plan = new PlanBatch();
        try
        {
            if (old != date) Rules.ValidateDate(date, p.StartDate, "목표일", "targetDate");
            s.TargetDate = date;
            if (old != null && cascade && delta != 0)
            {
                foreach (var later in p.Stages.Where(x => x.SortOrder > s.SortOrder && x.CompletedDate == null && x.TargetDate != null))
                {
                    var to = cal.AddWorkingDays(later.TargetDate!.Value, delta);
                    if (to == later.TargetDate) continue;
                    Rules.ValidateDate(to, p.StartDate, $"연쇄 이동할 '{later.Name}' 목표일", "targetDate");
                    later.TargetDate = to;
                }
            }
            if (old != null && includeWorks && delta != 0)
            {
                foreach (var w in Rules.CascadeWorks(p, s).ToList())
                {
                    var (ns, ne) = Rules.ShiftWork(w, delta, cal);
                    plan.Move(w, ns, ne, "", "");
                }
                foreach (var m in Rules.Reschedule(p, cal, plan.Works.ToList()))
                    plan.Move(m.Work, m.NewStart, m.NewEnd, "", "");
                CommitPlan(p, plan, Guid.Empty, "targetDate");
            }
        }
        catch (RuleException ex)
        {
            return result with { Violations = new() { new PreviewIssue(ex.Message, true, ex.Field) } };
        }

        var after = Snapshot(p, cal, today);
        var stages = StageRows(p, before, after, today,
            x => ReferenceEquals(x, s) || targets[x.Id] != x.TargetDate || plan.Entries.Any(e => x.Works.Contains(e.Work)), targets);
        var main = stages.First(x => x.Id == s.Id);

        // 다른 과제 단계와의 연결: 이 변경으로 새로 어긋나는 것을 알린다(자동으로 옮기지 않으므로 상대 담당자와 맞춰야 한다)
        var moved = p.Stages.Where(x => targets[x.Id] != x.TargetDate).ToList();
        var issues = await CrossLinkIssuesAsync(p, moved, targets, cal);
        return result with
        {
            Moved = Moves(p, plan, cal),
            Stages = stages,
            StageForecast = new ForecastChange(main.Before, main.After),
            ForecastOver = main.OverAfter,
            Violations = issues,
        };
    }

    // ── 오늘 기준 재계획 ─────────────────────────────────────────────

    // 재계획하면 옮겨질 작업: 예측(Rules.ForwardPass)과 목표가 다른 미착수 작업 (Rules.ReplanMoves)
    public async Task<PlanPreview> ReplanPreviewAsync(int projectId)
    {
        var p = await PlanGraphAsync(projectId);
        var cal = await CalendarAsync();
        var today = _clock.Today;
        var before = Snapshot(p, cal, today);
        var plan = new PlanBatch();
        var startLate = new HashSet<WorkItem>();
        foreach (var m in Rules.ReplanMoves(p, cal, today))
        {
            plan.Move(m.Work, m.NewStart, m.NewEnd, "", "");
            if (m.StartLate) startLate.Add(m.Work);
        }
        var result = new PlanPreview();
        try
        {
            CommitPlan(p, plan, Guid.Empty, "reason");
        }
        catch (RuleException ex)
        {
            return result with { Moved = Moves(p, plan, cal, startLate), Violations = new() { new PreviewIssue(ex.Message, true, ex.Field) } };
        }
        var after = Snapshot(p, cal, today);
        return result with
        {
            Moved = Moves(p, plan, cal, startLate),
            Stages = StageRows(p, before, after, today, s => plan.Entries.Any(x => s.Works.Contains(x.Work))),
        };
    }

    // 예측과 다른 미착수 작업의 목표 일정을 예측대로 옮긴다. 한 번의 사유로 BatchId 하나, 작업마다 WorkPlanChanged 1건·Version++.
    // 화면이 본 미리보기(Rows)와 지금 계산이 다르면(그사이 누가 일정을 바꿈) 409 로 다시 보게 한다
    public async Task ReplanAsync(ReplanInput input)
    {
        var reason = Text(input.Reason, Limits.Reason, "사유", "reason") ?? throw new RuleException("재계획 사유를 입력해 주세요.", "reason");
        var p = await RequireAsync(input.ProjectId);
        CheckVersion(input.Version, p.Version, p.Id);
        var cal = await CalendarAsync();
        var moves = Rules.ReplanMoves(p, cal, _clock.Today);
        if (input.Rows != null)
        {
            var same = input.Rows.Count == moves.Count && moves.All(m => input.Rows.Any(r =>
                r.WorkId == m.Work.Id && (r.Version == null || r.Version == m.Work.Version) && r.PlanStart == m.NewStart && r.PlanEnd == m.NewEnd));
            if (!same) throw new ConflictException(p.Id);
        }
        if (moves.Count == 0) throw new RuleException("예측과 다른 미착수 작업이 없어 옮길 작업이 없습니다.");

        var plan = new PlanBatch();
        foreach (var m in moves) plan.Move(m.Work, m.NewStart, m.NewEnd, "재계획", reason);
        CommitPlan(p, plan, Guid.NewGuid(), "reason");
        Touch(p);
        await _db.SaveChangesAsync();
    }

    // ── 공통 ─────────────────────────────────────────────────────────

    private async Task<int> ProjectOfStageAsync(int stageId)
    {
        var row = await _db.Stages.AsNoTracking().Where(s => s.Id == stageId).Select(s => new { s.ProjectId }).FirstOrDefaultAsync();
        return row?.ProjectId ?? throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.", "stageId");
    }

    // 미리보기용 과제 복사본 (단계·작업·의존만). 읽은 엔티티를 그대로 고치지 않고 새 객체에 옮겨 담는다 —
    // 같은 요청에서 다른 저장이 일어나도, 엔티티를 공유하는 저장소(섀도 빌드의 가짜 EF)에서도 미리보기가 데이터를 바꾸지 않게
    private async Task<Project> PlanGraphAsync(int projectId)
    {
        var src = await _db.LiveProjects.AsNoTracking()
            .Include(p => p.Stages).ThenInclude(s => s.Works)
            .Include(p => p.Links)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");
        var copy = new Project { Id = src.Id, Title = src.Title, State = src.State, StartDate = src.StartDate, Version = src.Version };
        foreach (var s in src.Stages)
        {
            var stage = new Stage
            {
                Id = s.Id, ProjectId = s.ProjectId, Name = s.Name, Kind = s.Kind, SortOrder = s.SortOrder,
                TargetDate = s.TargetDate, CompletedDate = s.CompletedDate, BaselineDate = s.BaselineDate, Version = s.Version,
            };
            foreach (var w in s.Works)
            {
                var copyWork = new WorkItem
                {
                    Id = w.Id, StageId = w.StageId, Title = w.Title, Progress = w.Progress, PlanStart = w.PlanStart, PlanEnd = w.PlanEnd,
                    ActualStart = w.ActualStart, ActualEnd = w.ActualEnd, Version = w.Version,
                };
                _readVersions[copyWork] = w.Version;
                stage.Works.Add(copyWork);
            }
            copy.Stages.Add(stage);
        }
        foreach (var l in src.Links)
            copy.Links.Add(new WorkLink { Id = l.Id, ProjectId = l.ProjectId, PredecessorId = l.PredecessorId, SuccessorId = l.SuccessorId, LagDays = l.LagDays, Type = l.Type });
        return copy;
    }

    // 복사본의 선행 연결을 wanted 로 바꾼다 (저장의 ApplyLinks 와 같은 결과, 이력 없이). 바뀌었으면 true
    private static bool ReplaceLinks(Project p, WorkItem w, Dictionary<int, WorkItem> all, List<LinkSpec> wanted)
    {
        var current = w.Id == 0 ? new List<WorkLink>() : p.Links.Where(l => l.SuccessorId == w.Id).ToList();
        var changed = current.Count != wanted.Count
            || wanted.Any(x => !current.Any(l => l.PredecessorId == x.PredecessorId && l.Type == x.Type && l.LagDays == x.LagDays));
        if (!changed) return false;
        foreach (var l in current) p.Links.Remove(l);
        foreach (var spec in wanted)
            p.Links.Add(new WorkLink { Predecessor = all[spec.PredecessorId], PredecessorId = spec.PredecessorId, Successor = w, SuccessorId = w.Id,
                Type = spec.Type, LagDays = spec.LagDays });
        return true;
    }

    // 단계별 작업 기준 예상 종료 (전진 계산)
    private static Dictionary<int, DateOnly?> Snapshot(Project p, WorkCalendar cal, DateOnly today)
    {
        var expected = Rules.ForwardPass(p, cal, today);
        return p.Stages.ToDictionary(s => s.Id, s => Rules.Forecast(s, today, expected));
    }

    // 보여 줄 단계: 고친 단계, 목표일이 옮겨진 단계, 작업이 옮겨진 단계, 예상 종료가 바뀐 단계 (단계 순서대로)
    private static List<PreviewStage> StageRows(Project p, Dictionary<int, DateOnly?> before, Dictionary<int, DateOnly?> after, DateOnly today,
                                                Func<Stage, bool> touched, Dictionary<int, DateOnly?>? oldTargets = null)
    {
        var rows = new List<PreviewStage>();
        foreach (var s in Rules.Ordered(p))
        {
            var target = oldTargets != null ? oldTargets[s.Id] : s.TargetDate;
            var b = before.GetValueOrDefault(s.Id);
            var a = after.GetValueOrDefault(s.Id);
            if (!touched(s) && b == a) continue;
            rows.Add(new PreviewStage(s.Id, s.Name, target, s.TargetDate, b, a, Rules.OverDays(b, target), Rules.OverDays(a, s.TargetDate),
                Rules.IsLate(s, today)));
        }
        return rows;
    }

    // 실제로 옮겨지는 작업 (처음 → 마지막 일정이 다른 것만, 원래 시작 순서대로). Version 은 복사본을 읽은 때의 값이다
    // (CommitPlan 이 복사본의 버전을 올리므로 PlanGraphAsync 가 기억해 둔 값을 쓴다 — 재계획 저장이 화면이 본 버전과 비교한다)
    private List<PreviewMove> Moves(Project p, PlanBatch plan, WorkCalendar cal, HashSet<WorkItem>? startLate = null)
    {
        var stageName = p.Stages.SelectMany(s => s.Works.Select(w => (w, s.Name))).ToDictionary(x => x.w, x => x.Name);
        return plan.Entries
            .Where(e => e.Work.PlanStart != e.Start || e.Work.PlanEnd != e.End)
            .OrderBy(e => e.Start).ThenBy(e => e.Work.Id)
            .Select(e => new PreviewMove(e.Work.Id, e.Work.Title, stageName.GetValueOrDefault(e.Work) ?? "", e.Start, e.End, e.Work.PlanStart, e.Work.PlanEnd,
                cal.Distance(cal.NextWorkingDay(e.Start), cal.NextWorkingDay(e.Work.PlanStart)),
                _readVersions.GetValueOrDefault(e.Work, e.Work.Version), startLate?.Contains(e.Work) ?? false))
            .ToList();
    }

    // 이미 시작한(그래서 밀지 않는) 후행 작업이 선행이 허용하는 날보다 먼저 시작한 연결 — 일정이 바뀌어 새로 생기면 알린다.
    // FF 는 시작이 아니라 끝을 맞추는 연결이라 진행 중인 작업은 예측(ForwardPass)이 끝을 늘려 보여 주므로 여기서 보지 않는다
    private static HashSet<(int Pred, int Succ)> StartedConflicts(Project p, WorkCalendar cal)
    {
        var works = p.Stages.SelectMany(s => s.Works.Select(w => (w, s))).Where(x => x.w.Id != 0).ToDictionary(x => x.w.Id);
        var set = new HashSet<(int, int)>();
        foreach (var l in p.Links)
        {
            if (l.Type == LinkType.FF || !works.TryGetValue(l.PredecessorId, out var a) || !works.TryGetValue(l.SuccessorId, out var b)) continue;
            if (b.w.ActualStart == null || b.w.ActualEnd != null || b.s.CompletedDate != null) continue;
            if (Rules.StartsBefore(b.w.ActualStart.Value, Rules.EarliestStart(l, a.w, b.w, cal), cal)) set.Add((a.w.Id, b.w.Id));
        }
        return set;
    }

    private static List<PreviewIssue> NewConflicts(Project p, HashSet<(int Pred, int Succ)> before, HashSet<(int Pred, int Succ)> after,
                                                   Dictionary<int, WorkItem> all) =>
        after.Where(x => !before.Contains(x))
            .Select(x => new PreviewIssue($"이미 시작한 '{Title(all, x.Succ)}' 은(는) 옮기지 않습니다. 선행 '{Title(all, x.Pred)}' 의 바뀐 일정보다 먼저 시작한 것으로 남습니다.", false))
            .ToList();

    // 목표일이 바뀌는 단계들의 과제 간 연결 중 이 변경으로 새로 어긋나는 것
    private async Task<List<PreviewIssue>> CrossLinkIssuesAsync(Project p, List<Stage> moved, Dictionary<int, DateOnly?> oldTargets, WorkCalendar cal)
    {
        var issues = new List<PreviewIssue>();
        if (moved.Count == 0) return issues;
        var mine = p.Stages.ToDictionary(s => s.Id);
        foreach (var l in await CrossLinksAsync(moved.Select(s => s.Id).ToList()))
        {
            if (l.PredecessorStage == null || l.SuccessorStage == null) continue;
            Stage Now(Stage x) => mine.TryGetValue(x.Id, out var m) ? m : x;
            Stage Was(Stage x) => mine.TryGetValue(x.Id, out var m)
                ? new Stage { Id = m.Id, Name = m.Name, TargetDate = oldTargets[m.Id], CompletedDate = m.CompletedDate } : x;
            var pred = Now(l.PredecessorStage);
            var succ = Now(l.SuccessorStage);
            if (!Rules.StageLinkViolated(l, pred, succ, cal) || Rules.StageLinkViolated(l, Was(l.PredecessorStage), Was(l.SuccessorStage), cal)) continue;
            var other = mine.ContainsKey(pred.Id) ? l.SuccessorStage : l.PredecessorStage;
            var earliest = Rules.StageLinkEarliest(l, pred, cal);
            issues.Add(new PreviewIssue(
                $"다른 과제 '{other.Project?.Title}' 의 '{other.Name}' 단계와 어긋납니다: 후행 단계 '{succ.Name}' 목표일 {Fmt(succ.TargetDate)} 은(는) {Fmt(earliest)} 이후여야 합니다. 자동으로 옮기지 않으니 상대 담당자와 맞춰 주세요.",
                false));
        }
        return issues;
    }

    // 날짜 하나의 휴일 안내만 (새 단계의 목표일처럼 옮길 것이 없는 입력)
    public async Task<PlanPreview> DayPreviewAsync(DateOnly? date, string what) =>
        new() { Notes = await DayNoteAsync(date, what) is { } note ? new() { note } : new() };

    // 날짜 입력 아래의 휴일 안내 (서버 달력 기준 — 화면은 근무일을 세거나 휴일을 판정하지 않는다)
    internal async Task<string?> DayNoteAsync(DateOnly? date, string what)
    {
        if (date == null) return null;
        var cal = await CalendarAsync();
        if (cal.IsWorkingDay(date.Value)) return null;
        // 공휴일 표는 요청 안에서 한 번만 읽는다(HolidayRowsAsync — 달력과 같은 행)
        _holidayNames ??= (await HolidayRowsAsync()).GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First().Name);
        return $"{what}이 휴일{(_holidayNames.TryGetValue(date.Value, out var name) ? $"({name})" : "")}입니다.";
    }
}
