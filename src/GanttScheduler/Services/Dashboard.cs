using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// DueThisWeek: 'n일 내 목표' — 그 기간에 목표일이 있는 미완료 단계를 가진 진행 과제 수. 누르면 가는 목록(과제)과 숫자가 같도록 과제로 센다
// Risk·Stale: 지연 위험(아직 지연은 아니지만 이유가 있는 진행 과제 — Rules.AtRisk)·정체(Rules.IsStale) 과제 수
public record Kpi(int Active, int Delayed, int DueThisWeek, int DoneThisYear, int Hold, int Risk = 0, int Stale = 0);
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
// Risk: 지연 위험 과제(위험 이유와 함께). Notes: 지연·위험 과제의 최근 사유 한 줄(ProjectService.RecentNotesAsync — 서비스가 채운다).
// Timeline 은 Query.Scope(기본 '주요' = 지연 + 위험 + FocusDays 안 목표)로 거르고 Query.Sort 로 줄 세운 것, TimelineTotal 은 거르기 전 진행 과제 수
public record DashboardView(
    DateOnly Today, Kpi Kpi,
    List<GroupStat> ByCategory, List<GroupStat> ByCorporation,
    List<ProjectView> Delayed, List<DueItem> Upcoming, List<TimelineView> Timeline, int UpcomingTotal = 0, int UpcomingProjects = 0,
    List<ProjectView>? Risk = null, int TimelineTotal = 0, DashboardQuery? Query = null, Dictionary<int, RecentNote>? Notes = null);

// 대시보드 조건(URL 쿼리). 유형·법인·내 과제는 화면 전체(숫자·목록 링크 포함)를 좁힌다.
// Scope: 간트 범위 'focus'(지연 + 위험 + FocusDays 안 목표, 기본)·'delayed'(지연만)·'all'(전체 진행 과제).
// Sort: 간트 순서 'delay'(지연 큰 순, 기본)·'end'(목표 종료 빠른 순)·'recent'(최근 변경 순)
public record DashboardQuery(bool Mine = false, int? CategoryId = null, int? CorporationId = null, string? Scope = null, string? Sort = null)
{
    public string ScopeKey => Scope is "delayed" or "all" ? Scope : "focus";
    public string SortKey => Sort is "end" or "recent" ? Sort : "delay";
}

// 최근 사유 한 줄: 사유가 붙은 일정 변경이나 댓글 중 가장 최근 것
public record RecentNote(string Text, string User, DateTime At);

// 위험 판정에 쓰는 것: 근무일 달력(전진 계산), 과제 간 단계 연결(위반), 정체 기준 시각. 없으면(시험·옛 호출) 위험·정체는 예전 화면처럼 비어 있다
public record RiskContext(WorkCalendar Calendar, IReadOnlyList<StageLink> CrossLinks, DateTime UtcNow);

// DoneYear: 그 해에 끝난 완료 과제(Rules.CompletedOn — 대시보드 '올해 완료'와 같은 판정).
// DueDays: 미완료 단계 목표일이 오늘부터 그 일수 안에 있는 진행 과제('n일 내 목표'·다가오는 일정과 같은 판정)
// Sort·Desc: 목록 머리글 정렬(Dashboard.SortKeys — 없으면 상태 → 지연 큰 순 → 목표 종료). Excel 도 같은 순서로 내보낸다
public record ProjectFilter(string? Q, int? CategoryId, int? CorporationId, string? State, bool Mine, int? DoneYear = null, int? DueDays = null,
    string? Sort = null, bool Desc = false);

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

    public Task<DashboardView> BuildAsync(bool mine) => BuildAsync(new DashboardQuery(mine));

    // 위험 판정(전진 계산)에 작업 의존(Links)·근무일 달력·과제 간 연결이 필요하다 — 대시보드·목록은 작업까지 이미 읽으므로
    // 의존·연결 표 두 개를 더 읽는 비용으로 상세와 같은 판정을 쓴다(DESIGN 6장)
    public async Task<DashboardView> BuildAsync(DashboardQuery query)
    {
        var all = await _projects.AllAsync(q =>
        {
            if (query.CategoryId is int category) q = q.Where(p => p.CategoryId == category);
            if (query.CorporationId is int corp) q = q.Where(p => p.CorporationId == corp);
            return q;
        }, withLinks: true);
        if (query.Mine) all = all.Where(p => Dashboard.IsMine(p, _me.Id)).ToList();
        var view = Dashboard.Build(all, await _projects.LocationsAsync(), _clock.Today, query, await RiskAsync());
        // 지연·위험 과제의 최근 사유 한 줄 — 카드에 보이는 과제 Id 목록으로 한 번에 읽는다. 목록 매개 변수(IN)의 예외라 길이에 상한을 둔다:
        // 위험 카드는 보이는 RiskShown 건만, 지연 카드는 지연 큰 순으로 앞의 NotesMax 건만(그 뒤 행은 사유 줄 없이 보인다)
        var ids = view.Delayed.Take(Dashboard.NotesMax).Select(p => p.Id).Concat(view.Risk!.Take(Dashboard.RiskShown).Select(p => p.Id)).ToList();
        return view with { Notes = await _projects.RecentNotesAsync(ids) };
    }

    private async Task<RiskContext> RiskAsync() =>
        new(await _projects.CalendarAsync(), await _projects.AllCrossLinksAsync(), _clock.UtcNow);

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
        }, withLinks: true);
        return Dashboard.Filter(all, await _projects.LocationsAsync(), _clock.Today, filter, _me.Id, await RiskAsync());
    }
}

// 대시보드·목록 집계 (순수 계산)
public static class Dashboard
{
    public const int UpcomingDays = 14;
    public const int UpcomingShown = 8;        // 대시보드 카드에 보이는 다가오는 일정 수
    public const int MaxDueDays = 90;          // 목록의 '목표일 n일 안' 조건으로 받는 가장 긴 기간
    public const int FocusDays = 28;           // 대시보드 간트 기본 범위('주요')에 넣는 목표일 기간 (4주)
    public const int RiskShown = 8;            // 대시보드 '지연 위험' 카드에 보이는 과제 수
    public const int NotesMax = 40;            // 대시보드 '지연 과제' 카드에서 최근 사유를 붙이는 과제 수 상한(RecentNotesAsync 의 IN 목록 길이)

    // 목록 상태 탭이 DB 에서 거를 과제 상태 ('delayed' 는 진행 과제 중 지연, 'all'·모르는 값은 null = 거르지 않음)
    public static ProjectState? StateOf(string? tab) => tab switch
    {
        "active" or "delayed" or "risk" or "stale" => ProjectState.Active,
        "hold" => ProjectState.Hold,
        "done" => ProjectState.Done,
        "dropped" => ProjectState.Dropped,
        _ => null,
    };

    public static bool IsMine(Project p, int userId) =>
        p.OwnerId == userId || p.Stages.Any(s => s.Works.Any(w => w.OwnerId == userId || w.CoOwnerId == userId));

    // 목록·대시보드가 쓰는 과제 화면 모델. risk 가 있으면 전진 계산·과제 간 연결·정체까지 판정한다
    public static ProjectView ViewOf(Project p, LocationIndex locations, DateOnly today, RiskContext? risk) =>
        Views.Project(p, today, locations, detail: false, withWorks: false,
            calendar: risk?.Calendar, crossLinks: risk?.CrossLinks, forecast: risk != null, utcNow: risk?.UtcNow);

    public static DashboardView Build(List<Project> all, LocationIndex locations, DateOnly today, DashboardQuery? query = null, RiskContext? risk = null)
    {
        query ??= new DashboardQuery();
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

        // 진행 과제 화면 모델(위험·정체 판정 포함)은 한 번만 만든다 — KPI·카드·간트가 같은 값을 쓴다
        var views = active.ToDictionary(p => p.Id, p => ViewOf(p, locations, today, risk));

        var kpi = new Kpi(
            active.Count, delayed.Count,
            due.Where(d => d.Soon).Select(d => d.ProjectId).Distinct().Count(),
            doneThisYear,
            all.Count(p => p.State == ProjectState.Hold),
            views.Values.Count(v => v.AtRisk),
            views.Values.Count(v => v.Stale));

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

        // 지연 카드: 지연 큰 순 → 목표 종료 빠른 순 / 위험 카드: 예상 지연 큰 순 → 이유 많은 순
        var delayedViews = Sort(views.Values.Where(v => v.DelayDays > 0), "delay", desc: true).ToList();
        var riskViews = views.Values.Where(v => v.AtRisk)
            .OrderByDescending(v => v.RiskDays).ThenByDescending(v => v.Risks.Count).ThenBy(v => v.EndTarget ?? DateOnly.MaxValue).ThenBy(v => v.Title, StringComparer.Ordinal)
            .ToList();

        // 간트 범위: 기본은 손볼 과제(지연·위험·4주 안 목표)만 — 진행 과제가 수백 건이면 전체 간트는 훑어볼 수 없다
        var byId = active.ToDictionary(p => p.Id);
        IEnumerable<ProjectView> scoped = query.ScopeKey switch
        {
            "all" => views.Values,
            "delayed" => views.Values.Where(v => v.DelayDays > 0),
            _ => views.Values.Where(v => v.DelayDays > 0 || v.AtRisk || Rules.HasStageDueWithin(byId[v.Id], today, FocusDays)),
        };
        var timeline = Sort(scoped, query.SortKey, desc: query.SortKey is "delay" or "recent").Select(TimelineView.From).ToList();

        var upcoming = due.Where(d => d.DDay <= UpcomingDays).ToList();
        return new DashboardView(today, kpi, byCategory, byCorp, delayedViews,
            upcoming.Take(UpcomingShown).ToList(), timeline, upcoming.Count,
            upcoming.Select(d => d.ProjectId).Distinct().Count(),
            riskViews, active.Count, query, new());
    }

    // 목록 머리글로 고를 수 있는 정렬 (키, 기본 방향이 내림차순인지). 'state' 는 기본 순서(상태 → 지연 큰 순 → 목표 종료)
    public static readonly (string Key, bool DescFirst)[] SortKeys =
    {
        ("title", false), ("corp", false), ("owner", false), ("stage", false), ("progress", true), ("end", false), ("state", false),
        ("delay", true), ("recent", true),
    };

    // 정렬 — 목록·Excel·대시보드 간트가 같은 함수를 쓴다. 값이 없는 행(목표일 미정 등)은 방향과 관계없이 뒤로 보낸다.
    // 같은 값이면 과제명 → Id 순(언제 불러도 같은 순서)
    public static IEnumerable<ProjectView> Sort(IEnumerable<ProjectView> views, string? sort, bool desc)
    {
        static int StateRank(ProjectView v) => v.State switch { "active" => 0, "hold" => 1, "done" => 2, _ => 3 };
        IOrderedEnumerable<ProjectView> o;
        IOrderedEnumerable<ProjectView> By<T>(IEnumerable<ProjectView> src, Func<ProjectView, T> key, IComparer<T>? cmp = null) =>
            desc ? src.OrderByDescending(key, cmp) : src.OrderBy(key, cmp);
        IOrderedEnumerable<ProjectView> NullLast<T>(Func<ProjectView, T?> key) where T : struct =>
            desc ? views.OrderBy(v => key(v) == null).ThenByDescending(key) : views.OrderBy(v => key(v) == null).ThenBy(key);

        switch (sort)
        {
            case "title": o = By(views, v => v.Title, StringComparer.Ordinal); break;
            case "corp": o = By(views, v => v.Corporation + "\u0001" + v.LocationPath, StringComparer.Ordinal); break;
            case "owner": o = By(views, v => v.Owner, StringComparer.Ordinal); break;
            case "stage": o = NullLast(v => v.CurrentTarget); break;
            case "progress": o = By(views, v => v.Progress); break;
            case "end": o = NullLast(v => v.EndTarget); break;
            case "delay":
                // 지연 일수 → 예상 지연 일수 → (같으면) 목표 종료가 빠른 쪽
                o = By(views, v => (v.DelayDays, v.RiskDays)).ThenBy(v => v.EndTarget ?? DateOnly.MaxValue);
                break;
            case "recent": o = NullLast(v => v.UpdatedAt); break;
            default:
                // 진행 → 보류 → 완료 → 중단, 그 안에서는 지연 큰 순 → 목표 종료 빠른 순 (desc 면 상태 순서만 뒤집는다)
                o = By(views, StateRank).ThenByDescending(v => v.DelayDays).ThenBy(v => v.EndTarget ?? DateOnly.MaxValue);
                break;
        }
        return o.ThenBy(v => v.Title, StringComparer.Ordinal).ThenBy(v => v.Id);
    }

    // 목록. risk 가 없으면(옛 호출·시험) 위험·정체 판정은 비어 있다
    public static List<ProjectView> Filter(List<Project> all, LocationIndex locations, DateOnly today, ProjectFilter f, int userId, RiskContext? risk = null)
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
            "active" or "risk" or "stale" => q.Where(p => p.State == ProjectState.Active),
            "delayed" => q.Where(p => Rules.DelayDays(p, today) > 0),
            "hold" => q.Where(p => p.State == ProjectState.Hold),
            "done" => q.Where(p => p.State == ProjectState.Done),
            "dropped" => q.Where(p => p.State == ProjectState.Dropped),
            _ => q,
        };

        // 검색: 과제명·담당·법인·위치에 더해 협력사와 작업 담당·부담당 이름까지 (작업을 맡은 사람 이름으로도 과제를 찾는다)
        var term = (f.Q ?? "").Trim();
        if (term.Length > 0)
        {
            bool Has(string? text) => text != null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
            q = q.Where(p => Has(p.Title) || Has(p.Owner?.Name) || Has(p.Vendor) || Has(locations.Get(p.CorporationId)?.Name)
                || Has(locations.PathBelowCorporation(p.LocationId))
                || p.Stages.Any(s => s.Works.Any(w => Has(w.Owner?.Name) || Has(w.CoOwner?.Name))));
        }

        var views = q.Select(p => ViewOf(p, locations, today, risk));
        views = (f.State ?? "") switch
        {
            "risk" => views.Where(v => v.AtRisk),
            "stale" => views.Where(v => v.Stale),
            _ => views,
        };

        return Sort(views.ToList(), f.Sort, f.Desc).ToList();
    }
}
