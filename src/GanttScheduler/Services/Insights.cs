using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// ── 리드타임 통계 ────────────────────────────────────────────────────────
// 단계 소요일 = 직전 완료 단계의 완료일(첫 단계는 착수일)부터 이 단계 완료일까지
public record LeadRow(string Stage, int Count, double AvgDays, int MinDays, int MaxDays, double? AvgOver, int LateCount);

public record LeadGroup(int CategoryId, string Category, int Projects, int DoneProjects, double? AvgTotalDays, int ScaleMax, List<LeadRow> Rows);

// ── 주간 보고 ────────────────────────────────────────────────────────────
public record WeeklyItem(string Text, string? Reason, string User, DateOnly Date);

public record WeeklyProject(
    int Id, string Title, string Category, string Corporation, string Owner, string StateLabel,
    int Progress, int DelayDays, string? CurrentStage, DateOnly? CurrentTarget,
    List<WeeklyItem> Done, List<WeeklyItem> Changes, List<WeeklyItem> Comments, List<string> Next, ChipView? Chip = null);

public record WeeklyReport(
    DateOnly From, DateOnly To, DateOnly Today, int Active, int Delayed, int DoneCount, int ChangeCount, List<WeeklyProject> Projects);

// ── 변경 이력 ────────────────────────────────────────────────────────────
public record HistoryFilter(DateOnly? From, DateOnly? To, string? User, string? Kind, string? Q, int Page);

public record HistoryRow(int Id, int ProjectId, string Project, string Type, string Kind, string Text, string? Reason, string User, DateTime At);

public record HistoryPage(DateOnly From, DateOnly To, List<HistoryRow> Rows, int Total, int Page, int Pages, List<string> Users);

// ── 내 할 일 ─────────────────────────────────────────────────────────────
public record MyWork(
    int ProjectId, string Project, int WorkId, string Stage, string Title, string Role,
    DateOnly PlanStart, DateOnly PlanEnd, int Progress, string Status, bool Late, int DDay)
{
    public bool Soon => !Late && Rules.IsSoon(DDay);           // 임박 (Rules.SoonDays)
    public ChipView Chip => Views.DueChip(DDay, Late);
}

public record MyStage(int ProjectId, string Project, string Stage, DateOnly? Target, int? DDay, bool Late, int Progress)
{
    public ChipView? Chip => DDay == null ? null : Views.DueChip(DDay.Value, Late);
}

public record MyView(List<MyWork> Late, List<MyWork> Soon, List<MyWork> Later, List<MyStage> Stages);

public class InsightService
{
    public const int HistoryPageSize = 50;

    private readonly AppDb _db;
    private readonly ProjectService _projects;
    private readonly IClock _clock;
    private readonly CurrentUser _me;

    public InsightService(AppDb db, ProjectService projects, IClock clock, CurrentUser me)
    {
        _db = db;
        _projects = projects;
        _clock = clock;
        _me = me;
    }

    public async Task<List<LeadGroup>> LeadTimesAsync() => Reporting.LeadTimes(await _projects.AllAsync());

    public async Task<MyView> MyAsync() => Reporting.My(await _projects.AllAsync(), _me.Id, _clock.Today);

    // from 이 속한 주(월~일)의 보고
    public async Task<WeeklyReport> WeeklyAsync(DateOnly? day, bool mine)
    {
        var (from, to) = Reporting.WeekOf(day ?? _clock.Today);
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));
        var activities = await _db.Activities.AsNoTracking().Include(a => a.User)
            .Where(a => a.At >= start && a.At < end).ToListAsync();
        var all = await _projects.AllAsync();
        if (mine) all = all.Where(p => Dashboard.IsMine(p, _me.Id)).ToList();
        return Reporting.Weekly(all, activities, await _projects.LocationsAsync(), from, to, _clock.Today);
    }

    public async Task<HistoryPage> HistoryAsync(HistoryFilter f)
    {
        var to = f.To ?? _clock.Today;
        var from = f.From ?? to.AddDays(-30);
        if (from > to) (from, to) = (to, from);
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));

        var list = await _db.Activities.AsNoTracking().Include(a => a.User).Include(a => a.Project)
            .Where(a => a.At >= start && a.At < end).ToListAsync();

        var rows = list
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Select(a => new HistoryRow(a.Id, a.ProjectId, a.Project?.Title ?? "", Views.Key(a.Type), Reporting.KindOf(a.Type),
                a.Text, a.Reason, a.User?.Name ?? "", a.At));

        var users = list.Select(a => a.User?.Name ?? "").Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();

        if (!string.IsNullOrWhiteSpace(f.User)) rows = rows.Where(r => r.User == f.User);
        if (!string.IsNullOrWhiteSpace(f.Kind)) rows = rows.Where(r => r.Kind == f.Kind);
        var q = (f.Q ?? "").Trim();
        if (q.Length > 0)
        {
            rows = rows.Where(r => r.Project.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || r.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (r.Reason ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = rows.ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)HistoryPageSize));
        var page = Math.Clamp(f.Page, 1, pages);
        return new HistoryPage(from, to, filtered.Skip((page - 1) * HistoryPageSize).Take(HistoryPageSize).ToList(),
            filtered.Count, page, pages, users);
    }
}

// 통계·보고 계산 (순수 함수)
public static class Reporting
{
    public static readonly (string Key, string Label)[] Kinds =
    {
        ("schedule", "일정 변경"), ("progress", "진행"), ("comment", "댓글"), ("state", "등록·상태"), ("structure", "구성 변경"), ("file", "첨부"),
    };

    public static string KindOf(ActivityType t) => t switch
    {
        ActivityType.Comment => "comment",
        ActivityType.TargetChanged or ActivityType.WorkPlanChanged or ActivityType.BaselineSet => "schedule",
        ActivityType.StageCompleted or ActivityType.StageReopened or ActivityType.WorkEdited or ActivityType.CheckChanged => "progress",
        ActivityType.StateChanged or ActivityType.Created or ActivityType.Cloned => "state",
        ActivityType.AttachmentAdded or ActivityType.AttachmentRemoved => "file",
        _ => "structure",
    };

    // 해당 날짜가 속한 주의 월요일~일요일
    public static (DateOnly From, DateOnly To) WeekOf(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7;
        var from = day.AddDays(-offset);
        return (from, from.AddDays(6));
    }

    public static List<LeadGroup> LeadTimes(List<Project> all)
    {
        var groups = new List<LeadGroup>();
        foreach (var g in all.Where(p => p.State != ProjectState.Dropped).GroupBy(p => p.CategoryId)
                     .OrderBy(g => g.First().Category?.SortOrder ?? 0))
        {
            // 단계 이름별 표본: (소요일, 목표 대비 초과일)
            var samples = new Dictionary<string, (int Order, List<int> Days, List<int> Over)>();
            var totals = new List<int>();
            foreach (var p in g)
            {
                var prev = p.StartDate;
                foreach (var s in Rules.Ordered(p))
                {
                    if (s.CompletedDate == null) continue;
                    if (!samples.TryGetValue(s.Name, out var x)) samples[s.Name] = x = (s.SortOrder, new List<int>(), new List<int>());
                    x.Days.Add(Math.Max(0, s.CompletedDate.Value.DayNumber - prev.DayNumber));
                    if (s.TargetDate != null) x.Over.Add(s.CompletedDate.Value.DayNumber - s.TargetDate.Value.DayNumber);
                    prev = s.CompletedDate.Value;
                }
                if (p.State == ProjectState.Done && Rules.AllStagesDone(p))
                    totals.Add(p.Stages.Max(s => s.CompletedDate!.Value).DayNumber - p.StartDate.DayNumber);
            }

            var rows = samples.OrderBy(kv => kv.Value.Order)
                .Select(kv => new LeadRow(kv.Key, kv.Value.Days.Count, Math.Round(kv.Value.Days.Average(), 1),
                    kv.Value.Days.Min(), kv.Value.Days.Max(),
                    kv.Value.Over.Count > 0 ? Math.Round(kv.Value.Over.Average(), 1) : null,
                    kv.Value.Over.Count(o => o > 0)))
                .ToList();

            groups.Add(new LeadGroup(g.Key, g.First().Category?.Name ?? "", g.Count(), totals.Count,
                totals.Count > 0 ? Math.Round(totals.Average(), 1) : null,
                rows.Count > 0 ? Math.Max(1, rows.Max(r => r.MaxDays)) : 1, rows));
        }
        return groups;
    }

    public static WeeklyReport Weekly(List<Project> all, List<Activity> activities, LocationIndex locations, DateOnly from, DateOnly to, DateOnly today)
    {
        var byProject = activities.GroupBy(a => a.ProjectId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.At).ToList());
        var nextFrom = to.AddDays(1);
        var nextTo = to.AddDays(7);
        var projects = new List<WeeklyProject>();

        foreach (var p in all)
        {
            byProject.TryGetValue(p.Id, out var acts);
            acts ??= new List<Activity>();
            if (p.State != ProjectState.Active && acts.Count == 0) continue;   // 움직임 없는 보류·완료 과제는 뺀다

            WeeklyItem Item(Activity a) => new(a.Text, a.Reason, a.User?.Name ?? "", KoreaClock.KstDate(a.At));

            var done = acts.Where(a => a.Type == ActivityType.StageCompleted).Select(Item).ToList();
            done.AddRange(Rules.AllWorks(p)
                .Where(w => w.ActualEnd != null && w.ActualEnd.Value >= from && w.ActualEnd.Value <= to)
                .OrderBy(w => w.ActualEnd)
                .Select(w => new WeeklyItem($"작업 완료: {w.Title}", null, w.Owner?.Name ?? "", w.ActualEnd!.Value)));

            var changes = acts.Where(a => a.Type is ActivityType.TargetChanged or ActivityType.WorkPlanChanged or ActivityType.StateChanged or ActivityType.BaselineSet)
                .Select(Item).ToList();
            var comments = acts.Where(a => a.Type == ActivityType.Comment).Select(Item).ToList();

            var next = new List<string>();
            if (p.State == ProjectState.Active)
            {
                next.AddRange(Rules.Ordered(p)
                    .Where(s => s.CompletedDate == null && s.TargetDate != null && s.TargetDate.Value >= nextFrom && s.TargetDate.Value <= nextTo)
                    .Select(s => $"[{s.Name}] 단계 목표 {s.TargetDate!.Value:MM-dd}"));
                next.AddRange(p.Stages.SelectMany(s => s.Works.Where(w => Rules.IsOpen(w, s)))   // 완료된 단계의 작업은 뺀다
                    .Where(w => w.PlanEnd >= nextFrom && w.PlanEnd <= nextTo)
                    .OrderBy(w => w.PlanEnd)
                    .Select(w => $"{w.Title} 종료 목표 {w.PlanEnd:MM-dd}" + (w.Owner != null ? $" ({w.Owner.Name})" : "")));
            }

            var current = Rules.CurrentStage(p);
            projects.Add(new WeeklyProject(p.Id, p.Title, p.Category?.Name ?? "", locations.Get(p.CorporationId)?.Name ?? "", p.Owner?.Name ?? "",
                Views.StateLabel(p.State, Rules.DelayDays(p, today)),
                Rules.ProjectProgress(p), Rules.DelayDays(p, today), current?.Name, current?.TargetDate,
                done, changes, comments, next, Views.ProjectChip(p.State, Rules.DelayDays(p, today))));
        }

        projects = projects.OrderByDescending(x => x.DelayDays).ThenBy(x => x.Title).ToList();
        return new WeeklyReport(from, to, today,
            all.Count(p => p.State == ProjectState.Active),
            all.Count(p => Rules.DelayDays(p, today) > 0),
            projects.Sum(x => x.Done.Count), projects.Sum(x => x.Changes.Count), projects);
    }

    public static MyView My(List<Project> all, int userId, DateOnly today)
    {
        var works = new List<MyWork>();
        var stages = new List<MyStage>();
        foreach (var p in all.Where(p => p.State == ProjectState.Active))
        {
            foreach (var s in Rules.Ordered(p))
            {
                // 완료된 단계의 작업은 단계와 함께 끝난 것으로 본다(Rules.IsOpen) — 유령 지연을 남기지 않는다
                foreach (var w in s.Works.Where(w => Rules.IsOpen(w, s) && (w.OwnerId == userId || w.CoOwnerId == userId)))
                {
                    works.Add(new MyWork(p.Id, p.Title, w.Id, s.Name, w.Title, w.OwnerId == userId ? "담당" : "부담당",
                        w.PlanStart, w.PlanEnd, w.Progress, Views.Key(Rules.StatusOf(w)), Rules.IsLate(w, s, today),
                        Rules.DaysLeft(w.PlanEnd, today)));
                }
            }
            // 내가 담당인 과제는 지금 진행 중인 단계도 챙긴다
            if (p.OwnerId == userId && Rules.CurrentStage(p) is { } cur)
            {
                stages.Add(new MyStage(p.Id, p.Title, cur.Name, cur.TargetDate,
                    cur.TargetDate == null ? null : Rules.DaysLeft(cur.TargetDate.Value, today),
                    Rules.IsLate(cur, today), Rules.StageProgress(cur)));
            }
        }

        var ordered = works.OrderBy(w => w.PlanEnd).ThenBy(w => w.Title).ToList();
        return new MyView(
            ordered.Where(w => w.Late).ToList(),
            // 곧(Rules.SoonDays 안) 끝나야 하거나 이미 시작했어야 하는 작업
            ordered.Where(w => !w.Late && (w.Soon || w.PlanStart <= today)).ToList(),
            ordered.Where(w => !w.Late && !w.Soon && w.PlanStart > today).ToList(),
            stages.OrderBy(s => s.Target ?? DateOnly.MaxValue).ToList());
    }

    // ── 단계 기본 기간 힌트 ─────────────────────────────────────────────

    // 표본이 이보다 적으면 평균이 우연에 좌우되므로 힌트를 보이지 않는다
    public const int LeadHintMinSamples = 3;

    // 관리 화면의 단계 템플릿 기본 기간 옆에 보이는 참고값: 같은 유형·같은 이름 단계가 완료된 과제에서 실제로 걸린 근무일의 평균.
    // 기본 기간과 같은 거리로 잰다 — 앞 단계 완료일(첫 단계는 착수일)부터 이 단계 완료일까지의 근무일(WorkCalendar.Distance).
    // - 완료된 과제(모든 단계 완료)만 본다. 진행 중 과제의 완료 단계는 뒤 단계가 어떻게 될지 몰라 표본이 치우친다.
    // - 앞 단계와 같은 날 또는 먼저 완료한 단계(0 이하)는 뺀다 — 몰아서 완료 처리한 기록이라 실제 기간을 알 수 없다.
    //   기준점은 지금까지 가장 늦은 완료일로 둬, 순서를 거슬러 완료한 단계 뒤의 단계가 길게 잡히지 않게 한다.
    // - 한 과제에 같은 이름의 단계가 여럿이면 처음 것만 센다(표본 수 = 과제 수).
    public static Dictionary<(int CategoryId, string Stage), StageLeadHint> StageLeadHints(IEnumerable<Project> projects, WorkCalendar cal)
    {
        var samples = new Dictionary<(int, string), List<int>>();
        foreach (var p in projects.Where(p => p.State == ProjectState.Done && Rules.AllStagesDone(p)))
        {
            var prev = p.StartDate;
            var seen = new HashSet<string>();
            foreach (var s in Rules.Ordered(p))
            {
                var done = s.CompletedDate!.Value;
                var days = cal.Distance(prev, done);
                if (done > prev) prev = done;
                if (!seen.Add(s.Name) || days <= 0) continue;
                if (!samples.TryGetValue((p.CategoryId, s.Name), out var list)) samples[(p.CategoryId, s.Name)] = list = new List<int>();
                list.Add(days);
            }
        }
        return samples.Where(kv => kv.Value.Count >= LeadHintMinSamples)
            .ToDictionary(kv => kv.Key, kv => new StageLeadHint(Math.Round(kv.Value.Average(), 1), kv.Value.Count));
    }
}

// 단계 기본 기간 힌트: 과거 평균 근무일과 표본(완료 과제) 수
public record StageLeadHint(double AvgWorkDays, int Count);
