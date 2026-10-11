using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// ── 분석 화면용 모델 (S10) ──────────────────────────────────────────────

// 분석 화면 공통 조건: 유형·법인·조직(하위 파트 포함 — 과제 담당자의 소속)
public record AnalyticsFilter(int? CategoryId = null, int? CorporationId = null, int? OrgUnitId = null)
{
    public bool Any => CategoryId != null || CorporationId != null || OrgUnitId != null;
}

// 고르기 목록 한 줄 (Level: 조직은 1 그룹·2 파트)
public record PickOption(int Id, string Name, int Level = 1, bool Active = true);

// 추세 한 점: 그날 스냅샷의 진행 과제 수·지연 과제 수·지연 위험 과제 수·진행 과제 평균 진행률
public record TrendPoint(DateOnly Date, int Active, int Delayed, int Risk, double? AvgProgress);

// Range: '12w'(12주, 날마다) 또는 '1y'(1년, 주마다 — 그 주의 마지막 기록). FirstSnapshot: 수집을 시작한 날(없으면 null)
public record TrendView(string Range, DateOnly From, DateOnly To, DateOnly? FirstSnapshot, List<TrendPoint> Points, AnalyticsFilter Filter,
    List<PickOption> Categories, List<PickOption> Corporations, List<PickOption> Orgs, EquipmentMatrix Equipment);

// 일정 변경 활동 한 줄(분석에 필요한 열만)
public record ChangeActivity(int Id, int ProjectId, ActivityType Type, string? OldValue, string? NewValue, Guid? BatchId, int? ReasonCode,
    string? Reason, DateTime At);

// 일정 변경 결정 하나: 같은 BatchId(없으면 활동 하나)로 묶은 변경. Slip: 그 결정이 밀어낸 가장 큰 달력 일수(셀 수 없으면 null), Targets: 바뀐 대상 수
public record ChangeDecision(int ProjectId, DateTime At, int? Slip, int? ReasonCode, string? Reason, int Targets);

// 월별: 결정 수와 그 달 결정이 밀어낸 일수 합(양수만 — 당긴 것은 빼지 않는다), 누적 밀림
public record ChangeMonth(string Month, int Count, int Slip, int Cumulative);
// 유형·법인별 월간 결정 수 (Counts 는 Months 순서)
public record ChangeGroupRow(int Id, string Name, int[] Counts, int Total);
// 재계획 상위 과제: 결정 수와 밀어낸 일수 합
public record ReplanTop(int ProjectId, string Project, string Category, string Corporation, int Decisions, int Slip);
// 원인 파레토 한 줄: 분류별 결정 수, 분류된 결정 중 비율과 누적 비율(%)
public record ParetoRow(int Code, string Name, int Count, double Share, double Cumulative, bool Active = true);

public record ChangesView(DateOnly From, DateOnly To, List<string> Months, List<ChangeMonth> ByMonth, List<ChangeGroupRow> ByCategory,
    List<ChangeGroupRow> ByCorporation, List<ReplanTop> Top, List<ParetoRow> Pareto, int Unclassified, int Total, AnalyticsFilter Filter,
    List<PickOption> Categories, List<PickOption> Corporations, List<PickOption> Orgs, int MonthsCount);

// 법인 × 기기 종류 계획 수량 (Active: 진행·보류 과제, Done: 완료 과제 — 중단은 뺀다)
public record EquipmentColumn(int Id, string Name);
public record EquipmentRow(int CorporationId, string Corporation, int[] Active, int[] Done)
{
    public int ActiveTotal => Active.Sum();
    public int DoneTotal => Done.Sum();
}
public record EquipmentMatrix(List<EquipmentColumn> Columns, List<EquipmentRow> Rows)
{
    public int ActiveTotal(int i) => Rows.Sum(r => r.Active[i]);
    public int DoneTotal(int i) => Rows.Sum(r => r.Done[i]);
}

// 부하: 사람 한 줄. HasIp: 접속한 적이 있는 사람(이름만 등록된 담당자는 false — 부하에는 그대로 포함)
public record LoadPerson(int UserId, string Name, bool HasIp, List<Rules.LoadCell> Cells);
// 조직 한 덩어리(그룹 머리 → 파트 머리 → 사람). Level 0: 소속 없음
public record LoadSection(int? OrgId, string Name, int Level, List<LoadPerson> People);
public record LoadView(List<DateOnly> Weeks, List<int> Available, List<LoadSection> Sections, int WeekCount, int? OrgUnitId, List<PickOption> Orgs);

// 단계 병목: 진행 과제가 지금 단계에 머문 근무일이 같은 유형·단계의 평균 소요일(근무일)을 넘는 과제
public record BottleneckItem(int ProjectId, string Project, string Stage, int Dwell, double Avg);

public static partial class Reporting
{
    // ── 스냅샷 ──────────────────────────────────────────────────────────

    // 과제 하나의 그날 모습. 화면 모델(Dashboard.ViewOf — 대시보드와 같은 계산)의 값을 그대로 옮긴다
    public static ProjectSnapshot Snapshot(Project p, ProjectView v, DateOnly date) => new()
    {
        Date = date, ProjectId = p.Id, State = p.State, Progress = v.Progress, DelayDays = v.DelayDays, RiskDays = v.RiskDays,
        AtRisk = v.AtRisk, EndTarget = v.EndTarget, SlipDays = v.SlipDays, CurrentStage = v.CurrentStage,
        CategoryId = p.CategoryId, CorporationId = p.CorporationId, OwnerId = p.OwnerId, OrgUnitId = p.Owner?.OrgUnitId,
    };

    // 같은 날 다시 남길 때 기존 행을 덮어쓴다(유니크 (ProjectId, Date) 를 지키며)
    public static void CopySnapshot(ProjectSnapshot from, ProjectSnapshot to)
    {
        to.State = from.State; to.Progress = from.Progress; to.DelayDays = from.DelayDays; to.RiskDays = from.RiskDays; to.AtRisk = from.AtRisk;
        to.EndTarget = from.EndTarget; to.SlipDays = from.SlipDays; to.CurrentStage = from.CurrentStage;
        to.CategoryId = from.CategoryId; to.CorporationId = from.CorporationId; to.OwnerId = from.OwnerId; to.OrgUnitId = from.OrgUnitId;
    }

    // 조직 범위(하위 파트 포함)에 드는 조직 Id 들
    public static HashSet<int> OrgScope(int orgId, IEnumerable<OrgUnit> units) =>
        units.Where(u => Rules.InOrg(u.Id, u.ParentId, orgId)).Select(u => u.Id).ToHashSet();

    public static IEnumerable<ProjectSnapshot> Filter(IEnumerable<ProjectSnapshot> rows, AnalyticsFilter f, IReadOnlyCollection<int>? orgScope) =>
        rows.Where(s => (f.CategoryId == null || s.CategoryId == f.CategoryId)
                        && (f.CorporationId == null || s.CorporationId == f.CorporationId)
                        && (orgScope == null || (s.OrgUnitId is int o && orgScope.Contains(o))));

    // ── 추세 ────────────────────────────────────────────────────────────

    // 날짜마다 센다. weekly 면 주(월~일)마다 그 주의 마지막 기록 하나로 줄인다(1년 범위 — 점이 너무 많지 않게).
    // 기록이 없는 날(서버가 꺼져 있던 날)은 점이 없다 — 0 으로 지어내지 않는다
    public static List<TrendPoint> Trend(IEnumerable<ProjectSnapshot> rows, bool weekly)
    {
        var byDate = rows.GroupBy(s => s.Date).OrderBy(g => g.Key).Select(g =>
        {
            var active = g.Where(s => s.State == ProjectState.Active).ToList();
            return new TrendPoint(g.Key, active.Count, active.Count(s => s.DelayDays > 0), active.Count(s => s.AtRisk && s.DelayDays == 0),
                active.Count > 0 ? Math.Round(active.Average(s => s.Progress), 1) : null);
        }).ToList();
        if (!weekly) return byDate;
        return byDate.GroupBy(p => WeekOf(p.Date).From).Select(g => g.Last()).ToList();
    }

    // 스냅샷으로 본 그날의 진행·지연 과제 수 (주간 보고 '전주 대비'). 그날 기록이 없으면 null
    public static (int Active, int Delayed)? CountsOn(IEnumerable<ProjectSnapshot> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return null;
        var active = list.Where(s => s.State == ProjectState.Active).ToList();
        return (active.Count, active.Count(s => s.DelayDays > 0));
    }

    // ── 일정 변경 분석 ──────────────────────────────────────────────────

    // 목표일·작업 일정 변경을 결정 단위((BatchId ?? Id) 묶음)로 모은다(주간 보고 Decisions·Rules.DecisionCount 와 같은 묶음).
    // 밀린 일수는 묶음 안 변경의 (새 − 이전) 최댓값 — 작업 값은 '시작~종료'의 종료로 보고, '미정'(처음 정함·지움)은 셀 수 없어 뺀다
    public static List<ChangeDecision> ScheduleChanges(IEnumerable<ChangeActivity> acts) =>
        acts.Where(a => a.Type is ActivityType.TargetChanged or ActivityType.WorkPlanChanged)
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .GroupBy(a => a.BatchId?.ToString() ?? "#" + a.Id)
            .Select(g =>
            {
                var rows = g.ToList();
                var slips = rows.Select(a => Rules.ScheduleSlip(a.OldValue, a.NewValue)).Where(x => x != null).Select(x => x!.Value).ToList();
                return new ChangeDecision(rows[0].ProjectId, rows[0].At, slips.Count > 0 ? slips.Max() : null,
                    rows.Select(a => a.ReasonCode).FirstOrDefault(c => c != null), rows.Select(a => a.Reason).FirstOrDefault(r => !string.IsNullOrEmpty(r)),
                    rows.Count);
            })
            .ToList();

    public static string MonthKey(DateOnly d) => $"{d.Year:0000}-{d.Month:00}";

    // 분석 기간의 달 목록(오래된 달부터). to 가 속한 달까지 months 개
    public static List<string> Months(DateOnly to, int months) =>
        Enumerable.Range(0, months).Select(i => MonthKey(new DateOnly(to.Year, to.Month, 1).AddMonths(i - months + 1))).ToList();

    // 원인 파레토: 분류된 결정을 분류별로 세어 많은 순으로, 비율·누적 비율은 분류된 결정 수 기준. 분류 없는 결정은 따로 센다
    public static List<ParetoRow> Pareto(IEnumerable<ChangeDecision> decisions, IReadOnlyDictionary<int, (string Name, bool Active)> reasons)
    {
        var groups = decisions.Where(d => d.ReasonCode != null).GroupBy(d => d.ReasonCode!.Value)
            .Select(g => (Code: g.Key, Count: g.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Code).ToList();
        var total = groups.Sum(x => x.Count);
        var cum = 0;
        var list = new List<ParetoRow>();
        foreach (var (code, count) in groups)
        {
            cum += count;
            var (name, active) = reasons.TryGetValue(code, out var r) ? r : ($"(지운 분류 #{code})", false);
            list.Add(new ParetoRow(code, name, count, Math.Round(count * 100.0 / total, 1), Math.Round(cum * 100.0 / total, 1), active));
        }
        return list;
    }

    // 변경 분석 화면 모델. projects: 결정이 걸린 과제의 (제목·유형·법인) — 조건(필터)은 이미 거른 것
    public static ChangesView Changes(List<ChangeDecision> decisions, IReadOnlyDictionary<int, (string Title, int CategoryId, int CorporationId)> projects,
        IReadOnlyDictionary<int, string> categories, IReadOnlyDictionary<int, string> corporations, IReadOnlyDictionary<int, (string Name, bool Active)> reasons,
        DateOnly from, DateOnly to, int monthCount, AnalyticsFilter filter, List<PickOption> categoryOptions, List<PickOption> corpOptions, List<PickOption> orgOptions)
    {
        var months = Months(to, monthCount);
        var index = months.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        int MonthOf(ChangeDecision d) => index.GetValueOrDefault(MonthKey(KoreaClock.KstDate(d.At)), -1);
        var inRange = decisions.Where(d => projects.ContainsKey(d.ProjectId) && MonthOf(d) >= 0).ToList();

        var byMonth = new List<ChangeMonth>();
        var cumulative = 0;
        foreach (var m in months)
        {
            var list = inRange.Where(d => MonthKey(KoreaClock.KstDate(d.At)) == m).ToList();
            var slip = list.Sum(d => Math.Max(0, d.Slip ?? 0));
            cumulative += slip;
            byMonth.Add(new ChangeMonth(m, list.Count, slip, cumulative));
        }

        List<ChangeGroupRow> Group(Func<int, int> key, IReadOnlyDictionary<int, string> names) =>
            inRange.GroupBy(d => key(d.ProjectId)).Select(g =>
            {
                var counts = new int[months.Count];
                foreach (var d in g) counts[MonthOf(d)]++;
                return new ChangeGroupRow(g.Key, names.GetValueOrDefault(g.Key, ""), counts, g.Count());
            }).OrderByDescending(r => r.Total).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var top = inRange.GroupBy(d => d.ProjectId).Select(g =>
            {
                var p = projects[g.Key];
                return new ReplanTop(g.Key, p.Title, categories.GetValueOrDefault(p.CategoryId, ""), corporations.GetValueOrDefault(p.CorporationId, ""),
                    g.Count(), g.Sum(d => Math.Max(0, d.Slip ?? 0)));
            })
            .OrderByDescending(t => t.Decisions).ThenByDescending(t => t.Slip).ThenBy(t => t.Project, StringComparer.Ordinal).Take(10).ToList();

        return new ChangesView(from, to, months, byMonth,
            Group(id => projects[id].CategoryId, categories), Group(id => projects[id].CorporationId, corporations), top,
            Pareto(inRange, reasons), inRange.Count(d => d.ReasonCode == null), inRange.Count, filter, categoryOptions, corpOptions, orgOptions, monthCount);
    }

    // ── 단계 병목 ───────────────────────────────────────────────────────

    // 진행 과제마다 지금 단계와 체류 근무일(Rules.StageDwell) — 리드타임 표의 '진행 중'과 같은 유형·단계 이름으로 묶는다
    public static List<(int CategoryId, string Stage, Project Project, int Dwell)> Dwells(IEnumerable<Project> all, WorkCalendar cal, DateOnly today) =>
        all.Where(p => p.State == ProjectState.Active)
            .Select(p => (Project: p, Stage: Rules.CurrentStage(p), Dwell: Rules.StageDwell(p, cal, today)))
            .Where(x => x.Stage != null && x.Dwell != null)
            .Select(x => (x.Project.CategoryId, x.Stage!.Name, x.Project, x.Dwell!.Value))
            .ToList();

    // ── 법인 × 기기 계획 수량 ───────────────────────────────────────────

    // equipments: (과제 법인, 과제 상태, 기기 종류 Id, 수량). 열은 쓰인 기기 종류만(관리 화면 순서), 행은 법인(이름 순)
    public static EquipmentMatrix Equipment(IEnumerable<(int CorporationId, ProjectState State, int TypeId, int Quantity)> equipments,
        IReadOnlyList<EquipmentColumn> types, LocationIndex locations)
    {
        var list = equipments.Where(e => e.State != ProjectState.Dropped).ToList();
        var used = list.Select(e => e.TypeId).ToHashSet();
        var columns = types.Where(t => used.Contains(t.Id)).ToList();
        var col = columns.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var rows = list.GroupBy(e => e.CorporationId).Select(g =>
            {
                var active = new int[columns.Count];
                var done = new int[columns.Count];
                foreach (var e in g)
                {
                    if (!col.TryGetValue(e.TypeId, out var i)) continue;
                    if (e.State == ProjectState.Done) done[i] += e.Quantity; else active[i] += e.Quantity;
                }
                return new EquipmentRow(g.Key, locations.Get(g.Key)?.Name ?? "", active, done);
            })
            .OrderBy(r => r.Corporation, StringComparer.Ordinal).ToList();
        return new EquipmentMatrix(columns, rows);
    }

    // ── 부하 ────────────────────────────────────────────────────────────

    // 조직 트리 순서(그룹 → 그 그룹 사람 → 파트 → 그 파트 사람)로 사람 행을 묶는다. 배정이 하나도 없는 사람은 빼고, 소속 없는 사람은 끝에 모은다.
    // orgScope 가 있으면 그 조직(하위 파트 포함) 사람만
    public static List<LoadSection> LoadSections(Dictionary<int, List<Rules.LoadCell>> load, IReadOnlyList<AppUser> users, IReadOnlyList<OrgUnit> units,
        IReadOnlyCollection<int>? orgScope)
    {
        var people = users.Where(u => load.ContainsKey(u.Id))
            .Where(u => orgScope == null || (u.OrgUnitId is int o && orgScope.Contains(o)))
            .Select(u => (User: u, Row: new LoadPerson(u.Id, u.Name, u.Addresses.Count > 0, load[u.Id])))
            .ToList();
        List<LoadPerson> In(int? org) => people.Where(x => x.User.OrgUnitId == org).Select(x => x.Row).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();

        var sections = new List<LoadSection>();
        foreach (var g in units.Where(u => u.Level == 1).OrderBy(u => u.SortOrder).ThenBy(u => u.Id))
        {
            var parts = units.Where(u => u.ParentId == g.Id).OrderBy(u => u.SortOrder).ThenBy(u => u.Id).ToList();
            var direct = In(g.Id);
            var partSections = parts.Select(pt => new LoadSection(pt.Id, pt.Name, 2, In(pt.Id))).Where(s => s.People.Count > 0).ToList();
            if (direct.Count == 0 && partSections.Count == 0) continue;
            sections.Add(new LoadSection(g.Id, g.Name, 1, direct));
            sections.AddRange(partSections);
        }
        var none = In(null);
        // 조직 트리에 없는(지운) 조직에 속한 사람도 소속 없음으로
        var known = units.Select(u => u.Id).ToHashSet();
        none.AddRange(people.Where(x => x.User.OrgUnitId is int o && !known.Contains(o)).Select(x => x.Row));
        if (none.Count > 0) sections.Add(new LoadSection(null, "소속 없음", 0, none.OrderBy(r => r.Name, StringComparer.Ordinal).ToList()));
        return sections;
    }
}
