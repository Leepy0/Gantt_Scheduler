using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// DueThisWeek: 'n일 내 목표' — 그 기간에 목표일이 있는 미완료 단계를 가진 진행 과제 수. 누르면 가는 목록(과제)과 숫자가 같도록 과제로 센다
public record Kpi(int Active, int Delayed, int DueThisWeek, int DoneThisYear, int Hold);
// Id: 유형(CategoryId) 또는 법인(CorporationId) — 막대를 누르면 그 조건의 목록으로 간다. 같은 이름의 유형도 따로 센다
public record GroupStat(int Id, string Name, int OnTrack, int Delayed, int Done, int Paused, int Total);
public record DueItem(int ProjectId, string Project, string Stage, DateOnly Date, int DDay)
{
    public bool Soon => Rules.IsSoon(DDay);                    // 임박 (Rules.SoonDays)
    public ChipView Chip => Views.DueChip(DDay, late: false);
}

// Timeline: 간트용 가벼운 행(TimelineView) — 페이지에 JSON 으로 심으므로 쓰는 값만 싣는다.
// UpcomingTotal: 다가오는 일정(UpcomingDays 안)의 전체 단계 수 — 화면은 앞의 몇 건만 보이고 나머지가 있으면 목록으로 잇는다.
// UpcomingProjects: 그 단계들이 걸린 과제 수 — 이어지는 목록은 과제를 보이므로 링크에는 이 수를 적는다
public record DashboardView(
    DateOnly Today, Kpi Kpi,
    List<GroupStat> ByCategory, List<GroupStat> ByCorporation,
    List<ProjectView> Delayed, List<DueItem> Upcoming, List<TimelineView> Timeline, int UpcomingTotal = 0, int UpcomingProjects = 0);

// DoneYear: 그 해에 끝난 완료 과제(Rules.CompletedOn — 대시보드 '올해 완료'와 같은 판정).
// DueDays: 미완료 단계 목표일이 오늘부터 그 일수 안에 있는 진행 과제('n일 내 목표'·다가오는 일정과 같은 판정)
public record ProjectFilter(string? Q, int? CategoryId, int? CorporationId, string? State, bool Mine, int? DoneYear = null, int? DueDays = null);

public class DashboardService
{
    private readonly ProjectService _projects;
    private readonly IClock _clock;
    private readonly CurrentUser _me;

    public DashboardService(ProjectService projects, IClock clock, CurrentUser me)
    {
        _projects = projects;
        _clock = clock;
        _me = me;
    }

    public async Task<DashboardView> BuildAsync(bool mine)
    {
        var all = await _projects.AllAsync();
        if (mine) all = all.Where(p => Dashboard.IsMine(p, _me.Id)).ToList();
        return Dashboard.Build(all, await _projects.LocationsAsync(), _clock.Today);
    }

    // 목록: 상태 탭·유형·법인은 DB 에서 거른다(두 DB 에서 똑같이 번역되는 같음 조건만). 지연 탭은 진행 과제만 읽고
    // 지연 판정(Rules)은 메모리에서 한다. 내 과제·검색은 계산 값(담당 작업·위치 경로)을 보므로 메모리에서
    public async Task<List<ProjectView>> ListAsync(ProjectFilter filter)
    {
        var all = await _projects.AllAsync(q =>
        {
            if (Dashboard.StateOf(filter.State) is { } state) q = q.Where(p => p.State == state);
            if (filter.CategoryId is int category) q = q.Where(p => p.CategoryId == category);
            if (filter.CorporationId is int corp) q = q.Where(p => p.CorporationId == corp);
            return q;
        });
        return Dashboard.Filter(all, await _projects.LocationsAsync(), _clock.Today, filter, _me.Id);
    }
}

// 대시보드·목록 집계 (순수 계산)
public static class Dashboard
{
    public const int UpcomingDays = 14;
    public const int UpcomingShown = 8;        // 대시보드 카드에 보이는 다가오는 일정 수
    public const int MaxDueDays = 90;          // 목록의 '목표일 n일 안' 조건으로 받는 가장 긴 기간

    // 목록 상태 탭이 DB 에서 거를 과제 상태 ('delayed' 는 진행 과제 중 지연, 'all'·모르는 값은 null = 거르지 않음)
    public static ProjectState? StateOf(string? tab) => tab switch
    {
        "active" or "delayed" => ProjectState.Active,
        "hold" => ProjectState.Hold,
        "done" => ProjectState.Done,
        "dropped" => ProjectState.Dropped,
        _ => null,
    };

    public static bool IsMine(Project p, int userId) =>
        p.OwnerId == userId || p.Stages.Any(s => s.Works.Any(w => w.OwnerId == userId || w.CoOwnerId == userId));

    public static DashboardView Build(List<Project> all, LocationIndex locations, DateOnly today)
    {
        var active = all.Where(p => p.State == ProjectState.Active).ToList();
        var delayed = active.Where(p => Rules.DelayDays(p, today) > 0).ToList();

        // 진행 과제의 미완료 단계 중 목표일이 오늘 이후인 것
        var due = active
            .SelectMany(p => p.Stages
                .Where(s => s.CompletedDate == null && s.TargetDate != null && s.TargetDate.Value >= today)
                .Select(s => new DueItem(p.Id, p.Title, s.Name, s.TargetDate!.Value, s.TargetDate.Value.DayNumber - today.DayNumber)))
            .OrderBy(d => d.Date).ThenBy(d => d.Project)
            .ToList();

        var doneThisYear = all.Count(p => p.State == ProjectState.Done && Rules.CompletedOn(p)?.Year == today.Year);

        var kpi = new Kpi(
            active.Count, delayed.Count,
            due.Where(d => d.Soon).Select(d => d.ProjectId).Distinct().Count(),
            doneThisYear,
            all.Count(p => p.State == ProjectState.Hold));

        GroupStat Stat(int id, string name, IEnumerable<Project> group)
        {
            var list = group.ToList();
            var late = list.Count(p => Rules.DelayDays(p, today) > 0);
            var act = list.Count(p => p.State == ProjectState.Active);
            return new GroupStat(id, name, act - late, late,
                list.Count(p => p.State == ProjectState.Done),
                list.Count(p => p.State is ProjectState.Hold or ProjectState.Dropped),
                list.Count);
        }

        // 이름이 아니라 Id 로 묶는다 — 막대가 목록 조건(유형·법인 Id)과 같은 묶음이어야 숫자가 맞는다
        var byCategory = all.GroupBy(p => p.CategoryId)
            .Select(g => (Sort: g.First().Category?.SortOrder ?? 0, Stat: Stat(g.Key, g.First().Category?.Name ?? "", g)))
            .OrderBy(x => x.Sort).ThenBy(x => x.Stat.Id).Select(x => x.Stat).ToList();

        var byCorp = all.GroupBy(p => p.CorporationId)
            .Select(g => Stat(g.Key, locations.Get(g.Key)?.Name ?? "", g))
            .OrderByDescending(s => s.Total).ThenBy(s => s.Name).ToList();

        // 간트: 진행 과제를 지연 큰 순 → 목표 종료 빠른 순으로
        var views = active
            .OrderByDescending(p => Rules.DelayDays(p, today))
            .ThenBy(p => Rules.EndTarget(p) ?? DateOnly.MaxValue)
            .ThenBy(p => p.Title)
            .Select(p => Views.Project(p, today, locations, detail: false, withWorks: false))
            .ToList();

        var delayedViews = views.Where(v => v.DelayDays > 0).ToList();

        var upcoming = due.Where(d => d.DDay <= UpcomingDays).ToList();
        return new DashboardView(today, kpi, byCategory, byCorp, delayedViews,
            upcoming.Take(UpcomingShown).ToList(), views.Select(TimelineView.From).ToList(), upcoming.Count,
            upcoming.Select(d => d.ProjectId).Distinct().Count());
    }

    public static List<ProjectView> Filter(List<Project> all, LocationIndex locations, DateOnly today, ProjectFilter f, int userId)
    {
        IEnumerable<Project> q = all;
        if (f.Mine) q = q.Where(p => IsMine(p, userId));
        if (f.CategoryId != null) q = q.Where(p => p.CategoryId == f.CategoryId.Value);
        if (f.CorporationId != null) q = q.Where(p => p.CorporationId == f.CorporationId.Value);
        // 대시보드 숫자에서 온 조건: 그 숫자와 같은 판정으로 거른다
        if (f.DoneYear is int year) q = q.Where(p => p.State == ProjectState.Done && Rules.CompletedOn(p)?.Year == year);
        if (f.DueDays is int days) q = q.Where(p => p.State == ProjectState.Active && Rules.HasStageDueWithin(p, today, Math.Clamp(days, 0, MaxDueDays)));

        q = (f.State ?? "") switch
        {
            "active" => q.Where(p => p.State == ProjectState.Active),
            "delayed" => q.Where(p => Rules.DelayDays(p, today) > 0),
            "hold" => q.Where(p => p.State == ProjectState.Hold),
            "done" => q.Where(p => p.State == ProjectState.Done),
            "dropped" => q.Where(p => p.State == ProjectState.Dropped),
            _ => q,
        };

        var views = q.Select(p => Views.Project(p, today, locations, detail: false, withWorks: false));

        var term = (f.Q ?? "").Trim();
        if (term.Length > 0)
        {
            views = views.Where(v =>
                v.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || v.Owner.Contains(term, StringComparison.OrdinalIgnoreCase)
                || v.Corporation.Contains(term, StringComparison.OrdinalIgnoreCase)
                || v.LocationPath.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // 진행 → 보류 → 완료 → 중단, 그 안에서는 지연 큰 순 → 목표 종료 빠른 순
        return views
            .OrderBy(v => v.State switch { "active" => 0, "hold" => 1, "done" => 2, _ => 3 })
            .ThenByDescending(v => v.DelayDays)
            .ThenBy(v => v.EndTarget ?? DateOnly.MaxValue)
            .ThenBy(v => v.Title)
            .ToList();
    }
}
