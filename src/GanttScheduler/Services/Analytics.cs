using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 분석 탭(추세·변경 분석·부하)과 법인 × 기기 수량 표. 읽기만 한다. 계산은 Reporting(집계)·Rules(판정)에 있다
public class AnalyticsService
{
    public const int TrendShortWeeks = 12;
    public static readonly int[] ChangeMonthOptions = { 6, 12 };
    public static readonly int[] LoadWeekOptions = { 8, 12 };

    private readonly AppDb _db;
    private readonly ProjectService _projects;
    private readonly IClock _clock;

    public AnalyticsService(AppDb db, ProjectService projects, IClock clock)
    {
        _db = db;
        _projects = projects;
        _clock = clock;
    }

    // ── 고르기 목록 ───────────────────────────────────────────────────

    public async Task<List<PickOption>> CategoryOptionsAsync() =>
        (await _db.Categories.AsNoTracking().ToListAsync()).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new PickOption(c.Id, c.Name, 1, c.IsActive)).ToList();

    public async Task<List<PickOption>> CorporationOptionsAsync() =>
        (await _projects.LocationsAsync()).All.Where(l => l.Level == 1).OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
            .Select(l => new PickOption(l.Id, l.Name, 1, l.IsActive)).ToList();

    private List<OrgUnit>? _units;
    public async Task<List<OrgUnit>> OrgUnitsAsync() => _units ??= await _db.OrgUnits.AsNoTracking().ToListAsync();

    // 조직 고르기: 그룹 다음에 그 그룹의 파트 (그룹을 고르면 파트까지 포함한다)
    public async Task<List<PickOption>> OrgOptionsAsync()
    {
        var units = await OrgUnitsAsync();
        var list = new List<PickOption>();
        foreach (var g in units.Where(u => u.Level == 1).OrderBy(u => u.SortOrder).ThenBy(u => u.Id))
        {
            list.Add(new PickOption(g.Id, g.Name, 1, g.IsActive));
            list.AddRange(units.Where(u => u.ParentId == g.Id).OrderBy(u => u.SortOrder).ThenBy(u => u.Id).Select(u => new PickOption(u.Id, u.Name, 2, u.IsActive)));
        }
        return list;
    }

    private async Task<HashSet<int>?> OrgScopeAsync(int? orgId) => orgId is int id ? Reporting.OrgScope(id, await OrgUnitsAsync()) : null;

    // ── 추세 ──────────────────────────────────────────────────────────

    // range: '12w'(기본, 날마다) 또는 '1y'(주마다). 스냅샷은 수집을 시작한 날 이후만 있다(FirstSnapshot)
    public async Task<TrendView> TrendAsync(string? range, AnalyticsFilter filter)
    {
        var key = range == "1y" ? "1y" : "12w";
        var to = _clock.Today;
        var from = key == "1y" ? to.AddYears(-1).AddDays(1) : to.AddDays(-7 * TrendShortWeeks + 1);
        var q = _db.ProjectSnapshots.AsNoTracking().Where(s => s.Date >= from && s.Date <= to);
        if (filter.CategoryId is int c) q = q.Where(s => s.CategoryId == c);
        if (filter.CorporationId is int corp) q = q.Where(s => s.CorporationId == corp);
        var rows = Reporting.Filter(await q.ToListAsync(), filter with { CategoryId = null, CorporationId = null }, await OrgScopeAsync(filter.OrgUnitId));
        var first = await _db.ProjectSnapshots.AsNoTracking().OrderBy(s => s.Date).Select(s => (DateOnly?)s.Date).FirstOrDefaultAsync();
        return new TrendView(key, from, to, first, Reporting.Trend(rows, weekly: key == "1y"), filter,
            await CategoryOptionsAsync(), await CorporationOptionsAsync(), await OrgOptionsAsync(), await EquipmentAsync());
    }

    // ── 법인 × 기기 계획 수량 ─────────────────────────────────────────

    // 휴지통이 아닌 과제의 기기 내역(ProjectEquipment)을 법인 × 기기 종류로 더한다. 진행(진행·보류)과 완료를 나눠 센다
    public async Task<EquipmentMatrix> EquipmentAsync()
    {
        var rows = await _db.ProjectEquipments.AsNoTracking()
            .Where(e => e.Project!.DeletedAt == null && e.Project.State != ProjectState.Dropped)
            .Select(e => new { e.Project!.CorporationId, e.Project.State, e.EquipmentTypeId, e.Quantity }).ToListAsync();
        var types = await _db.EquipmentTypes.AsNoTracking().ToListAsync();
        var byId = types.ToDictionary(t => t.Id);
        // 관리 화면 순서: 대분류 → 그 하위 타입. 하위 타입은 '대분류 · 하위' 로 보인다
        var ordered = new List<EquipmentColumn>();
        foreach (var top in types.Where(t => t.ParentId == null).OrderBy(t => t.SortOrder).ThenBy(t => t.Id))
        {
            ordered.Add(new EquipmentColumn(top.Id, top.Name));
            ordered.AddRange(types.Where(t => t.ParentId == top.Id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
                .Select(t => new EquipmentColumn(t.Id, $"{top.Name} · {t.Name}")));
        }
        ordered.AddRange(types.Where(t => t.ParentId != null && !byId.ContainsKey(t.ParentId.Value)).Select(t => new EquipmentColumn(t.Id, t.Name)));
        return Reporting.Equipment(rows.Select(r => (r.CorporationId, r.State, r.EquipmentTypeId, r.Quantity)), ordered, await _projects.LocationsAsync());
    }

    // ── 일정 변경 분석 ────────────────────────────────────────────────

    // months 개월(이번 달 포함)의 목표일·작업 일정 변경을 결정 단위로 센다. 활동은 필요한 열만 읽고, 과제는 제목·유형·법인·담당 소속만 읽는다
    public async Task<ChangesView> ChangesAsync(int? months, AnalyticsFilter filter)
    {
        var count = months is int m && ChangeMonthOptions.Contains(m) ? m : 12;
        var to = _clock.Today;
        var from = new DateOnly(to.Year, to.Month, 1).AddMonths(1 - count);
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));
        var acts = await _db.LiveActivities.AsNoTracking()
            .Where(a => a.At >= start && a.At < end && (a.Type == ActivityType.TargetChanged || a.Type == ActivityType.WorkPlanChanged))
            .Select(a => new ChangeActivity(a.Id, a.ProjectId, a.Type, a.OldValue, a.NewValue, a.BatchId, a.ReasonCode, a.Reason, a.At))
            .ToListAsync();
        var decisions = Reporting.ScheduleChanges(acts);

        var ids = decisions.Select(d => d.ProjectId).ToHashSet();
        var info = (await _db.LiveProjects.AsNoTracking()
                .Select(p => new { p.Id, p.Title, p.CategoryId, p.CorporationId, OrgId = p.Owner!.OrgUnitId }).ToListAsync())
            .Where(p => ids.Contains(p.Id)).ToList();
        var scope = await OrgScopeAsync(filter.OrgUnitId);
        var projects = info.Where(p => (filter.CategoryId == null || p.CategoryId == filter.CategoryId)
                                       && (filter.CorporationId == null || p.CorporationId == filter.CorporationId)
                                       && (scope == null || (p.OrgId is int o && scope.Contains(o))))
            .ToDictionary(p => p.Id, p => (p.Title, p.CategoryId, p.CorporationId));

        var categories = await CategoryOptionsAsync();
        var corps = await CorporationOptionsAsync();
        var reasons = (await _db.DelayReasons.AsNoTracking().ToListAsync()).ToDictionary(r => r.Id, r => (r.Name, r.IsActive));
        return Reporting.Changes(decisions, projects, categories.ToDictionary(c => c.Id, c => c.Name),
            (await _projects.LocationsAsync()).All.Where(l => l.Level == 1).ToDictionary(l => l.Id, l => l.Name), reasons,
            from, to, count, filter, categories, corps, await OrgOptionsAsync());
    }

    // ── 부하 ──────────────────────────────────────────────────────────

    // 이번 주(월요일)부터 weeks 주. 진행 과제의 열린 작업을 오늘 기준 예상 일정으로 사람 × 주에 나눠 담는다(Rules.LoadAssignments·Load)
    public async Task<LoadView> LoadAsync(int? weeks, int? orgUnitId)
    {
        var count = weeks is int w && LoadWeekOptions.Contains(w) ? w : LoadWeekOptions[0];
        var today = _clock.Today;
        var start = Reporting.WeekOf(today).From;
        var cal = await _projects.CalendarAsync();
        var projects = await _projects.AllAsync(q => q.Where(p => p.State == ProjectState.Active), withLinks: true);
        var load = Rules.Load(projects.SelectMany(p => Rules.LoadAssignments(p, cal, today)), start, count, cal);
        var users = await _db.Users.AsNoTracking().Include(u => u.Addresses).ToListAsync();
        var units = await OrgUnitsAsync();
        var weeksList = Enumerable.Range(0, count).Select(i => start.AddDays(7 * i)).ToList();
        return new LoadView(weeksList, weeksList.Select(d => cal.WorkingDays(d, d.AddDays(6))).ToList(),
            Reporting.LoadSections(load, users, units, await OrgScopeAsync(orgUnitId)), count, orgUnitId, await OrgOptionsAsync());
    }
}
