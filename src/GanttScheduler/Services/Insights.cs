using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// ── 리드타임 통계 ────────────────────────────────────────────────────────
// 단계 소요일 = 앞 단계 완료일(첫 단계는 착수일 — 지금까지 가장 늦은 완료일)부터 이 단계 완료일까지 (Reporting.LeadSamples).
// Count·AvgDays·Min·Max·AvgWorkDays 는 소요일 표본(0일 표본 제외를 고르면 뺀 뒤), ZeroCount 는 근무일 0 이하 표본 수(같은 날·순서를 거슬러 몰아 완료).
// 목표 대비(완료일 − 목표일, 달력일 — 기준선 대비 일수와 같은 단위)는 소요일과 관계없이 완료한 단계 모두로 센다:
//  AvgOver/LateCount/OverCount: 지금 목표일 대비(여러 번 미룬 단계일수록 지킨 것처럼 보인다), AvgOverFirst/FirstCount: 처음 정한 목표일 대비,
//  AvgOverBaseline/BaselineCount: 기준선 대비(기준선이 있는 단계만)
public record LeadRow(string Stage, int Count, double AvgDays, int MinDays, int MaxDays, double? AvgOver, int LateCount,
    double AvgWorkDays = 0, int ZeroCount = 0, double? AvgOverFirst = null, int FirstCount = 0, double? AvgOverBaseline = null, int BaselineCount = 0,
    int OverCount = 0);

public record LeadGroup(int CategoryId, string Category, int Projects, int DoneProjects, double? AvgTotalDays, int ScaleMax, List<LeadRow> Rows,
    double? AvgTotalWorkDays = null);

// 단계 리드타임 표본 하나 (Days: 달력일, 음수는 0 / WorkDays: 근무일 거리 WorkCalendar.Distance). Zero: 근무일 0 이하
public record LeadSample(Stage Stage, int Days, int WorkDays)
{
    public bool Zero => WorkDays <= 0;
}

// ── 주간 보고 ────────────────────────────────────────────────────────────
// 보고 한 줄. More: 같은 결정(BatchId)으로 함께 바뀐 다른 대상(연쇄 이동·선행 연동) — 화면은 접어서 보이고 결정은 1건으로 센다
public record WeeklyItem(string Text, string? Reason, string User, DateOnly Date, List<string>? More = null)
{
    public int Targets => 1 + (More?.Count ?? 0);
}

// 밀린 항목: 보고 기준일에 목표일(작업은 목표 종료일)이 지났는데 끝나지 않은 단계·작업. Owner: 단계는 과제 담당, 작업은 작업 담당
public record WeeklyLate(string Kind, string Title, string? Owner, DateOnly Due, int LateDays)
{
    public ChipView Chip => Views.DueChip(-LateDays, late: true);
}

// Changes: 일정 변경 결정(목표일·작업 일정). States: 상태 변경·기준선 확정(자동 완료 포함). Overdue: 밀린 항목
public record WeeklyProject(
    int Id, string Title, string Category, string Corporation, string Owner, string StateLabel,
    int Progress, int DelayDays, string? CurrentStage, DateOnly? CurrentTarget,
    List<WeeklyItem> Done, List<WeeklyItem> Changes, List<WeeklyItem> Comments, List<string> Next, ChipView? Chip = null,
    List<WeeklyItem>? States = null, List<WeeklyLate>? Overdue = null, int RiskDays = 0)
{
    // RiskDays: 예상 지연 일수(Rules.RiskDays — 아직 지연이 아닌 진행 과제의 단계 예상 종료가 목표일을 넘는 일수). 이번 주 보고에서만 센다(지금 계산이라)
    // 이번 주 기록된 변동(실적·일정 변경·상태·진행 내용)이 없다
    public bool Quiet => Done.Count + Changes.Count + (States?.Count ?? 0) + Comments.Count == 0;
    // 카드로 보일 과제: 변동이 있거나 지연이거나 밀린 항목이 있다. 나머지 무변동 과제는 끝의 표 하나로 접는다(화면·Excel·PDF)
    // 예상 지연(조기 경보)이 있는 과제도 카드로 보인다 — 조용해 보여도 늦어질 과제를 접어 두지 않는다
    public bool Card => !Quiet || DelayDays > 0 || (Overdue?.Count ?? 0) > 0 || RiskDays > 0;
    public ChipView? RiskChip => RiskDays > 0 ? new("warn", "clock", $"예상 지연 +{RiskDays}일") : null;
    public int ChangeTargets => Changes.Sum(c => c.Targets);
}

// ChangeCount: 일정 변경 결정 수(BatchId 묶음은 1건), ChangeTargets: 그 결정이 바꾼 대상(행) 수.
// StateCount: 사람이 바꾼 상태·기준선 확정 수, Completed: 모든 단계를 끝내 자동으로 완료된 과제 수.
// AsOf: 지연·밀린 항목을 센 기준일 = min(그 주 마지막 날, 오늘). 진행률·현재 단계는 지금 값이다
public record WeeklyReport(
    DateOnly From, DateOnly To, DateOnly Today, int Active, int Delayed, int DoneCount, int ChangeCount, List<WeeklyProject> Projects,
    int ChangeTargets = 0, int StateCount = 0, int Completed = 0, DateOnly? AsOf = null)
{
    // 지난 주 보고 — 화면에 '지연은 그 주 기준, 진행률·현재 단계는 지금 기준'을 밝힌다
    public bool Past => AsOf != null && AsOf.Value < Today;
    public List<WeeklyProject> Cards() => Projects.Where(p => p.Card).ToList();
    public List<WeeklyProject> Folded() => Projects.Where(p => !p.Card).ToList();
}

// ── 변경 이력 ────────────────────────────────────────────────────────────
// ProjectId: 과제 하나의 이력만 (상세 활동 탭의 '이 과제 전체 이력')
public record HistoryFilter(DateOnly? From, DateOnly? To, string? User, string? Kind, string? Q, int Page, int? ProjectId = null);

public record HistoryRow(int Id, int ProjectId, string Project, string Type, string Kind, string Text, string? Reason, string User, DateTime At);

// Notice: 조건을 고쳐 조회했을 때의 안내(기간 상한 등). Project: 과제 조건이 있으면 그 과제명(없는 과제면 null)
public record HistoryPage(DateOnly From, DateOnly To, List<HistoryRow> Rows, int Total, int Page, int Pages, List<string> Users,
    string? Notice = null, int? ProjectId = null, string? Project = null);

// ── 내 할 일 ─────────────────────────────────────────────────────────────
// Version·ActualStart·ActualEnd: 내 할 일에서 바로 갱신(UpdateWorkProgressAsync)할 때 화면이 본 값. Group: 서버가 나눈 묶음
// ('late' 지연 · 'soon' 이번 주 · 'later' 이후) — 저장하면 다시 나눠 그 행만 옮긴다
public record MyWork(
    int ProjectId, string Project, int WorkId, string Stage, string Title, string Role,
    DateOnly PlanStart, DateOnly PlanEnd, int Progress, string Status, bool Late, int DDay,
    int Version = 0, DateOnly? ActualStart = null, DateOnly? ActualEnd = null, string Group = "")
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

    // 리드타임은 단계 완료일만 본다 — 작업·담당자는 읽지 않는다. 중단 과제는 표본에서 빠지므로 DB 에서 거른다.
    // excludeZero: 근무일 0 이하 표본(몰아서 완료 처리한 기록)을 소요일 평균에서 뺀다
    public async Task<List<LeadGroup>> LeadTimesAsync(bool excludeZero = false) =>
        Reporting.LeadTimes(await _db.LiveProjects.AsNoTracking().Include(p => p.Category).Include(p => p.Stages)
            .Where(p => p.State != ProjectState.Dropped).AsSplitQuery().ToListAsync(),
            await _projects.CalendarAsync(), await FirstTargetsAsync(), excludeZero);

    // 단계마다 처음 정한 목표일: 첫 목표일 변경(TargetChanged)의 이전 값. 바꾼 적이 없는 단계는 목록에 없다(지금 목표일이 처음 값).
    // 목표일 변경 행만 열 몇 개로 읽는다
    public async Task<Dictionary<int, DateOnly>> FirstTargetsAsync()
    {
        var rows = await _db.LiveActivities.AsNoTracking()
            .Where(a => a.Type == ActivityType.TargetChanged && a.StageId != null && a.Project!.State != ProjectState.Dropped)
            .Select(a => new { a.StageId, a.OldValue, a.At, a.Id }).ToListAsync();
        var result = new Dictionary<int, DateOnly>();
        foreach (var g in rows.GroupBy(a => a.StageId!.Value))
        {
            var first = g.OrderBy(a => a.At).ThenBy(a => a.Id).First();
            if (DateOnly.TryParseExact(first.OldValue, "yyyy-MM-dd", out var d)) result[g.Key] = d;
        }
        return result;
    }

    // 내 할 일은 진행 과제만 본다(Reporting.My)
    public async Task<MyView> MyAsync() =>
        Reporting.My(await _projects.AllAsync(q => q.Where(p => p.State == ProjectState.Active)), _me.Id, _clock.Today);

    // 내 할 일 한 행만 다시 계산한다(빠른 갱신 뒤 그 행을 옮기기 위해). 그 과제만 읽는다.
    // 더 이상 내 할 일이 아니면(완료·단계 완료·담당 변경) null
    public async Task<MyWork?> MyWorkAsync(int projectId, int workId) =>
        Reporting.My(await _projects.AllAsync(q => q.Where(p => p.Id == projectId && p.State == ProjectState.Active)), _me.Id, _clock.Today) is var v
            ? v.Late.Concat(v.Soon).Concat(v.Later).FirstOrDefault(w => w.WorkId == workId)
            : null;

    // 작업 Id 만 알 때(저장 충돌로 과제 Id 를 모를 때) 그 작업의 내 할 일 행. 작업이 지워졌거나 내 할 일이 아니면 null
    public async Task<MyWork?> MyWorkAsync(int workId)
    {
        var projectId = await _db.WorkItems.AsNoTracking().Where(w => w.Id == workId).Select(w => (int?)w.Stage!.ProjectId).FirstOrDefaultAsync();
        return projectId is int id ? await MyWorkAsync(id, workId) : null;
    }

    // from 이 속한 주(월~일)의 보고
    public async Task<WeeklyReport> WeeklyAsync(DateOnly? day, bool mine)
    {
        var (from, to) = Reporting.WeekOf(day ?? _clock.Today);
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));
        var activities = await _db.LiveActivities.AsNoTracking().Include(a => a.User)
            .Where(a => a.At >= start && a.At < end).ToListAsync();
        // 보고에 오르는 과제: 그 주가 끝나기 전에 등록했고, 진행 중이었거나 그 주에 움직임(활동)이 있던 과제뿐이다(Reporting.Weekly).
        // 지난 주라면 그 뒤에 마지막 단계를 끝낸 완료 과제도 그때는 진행 중이었다. 나머지 완료·중단 과제는 읽지 않는다
        var past = to < _clock.Today;
        var all = await _projects.AllAsync(q => q.Where(p => p.CreatedAt < end
            && (p.State == ProjectState.Active
                || (past && p.State == ProjectState.Done && p.Stages.Any(s => s.CompletedDate > to))
                || p.Activities.Any(a => a.At >= start && a.At < end && a.Type != ActivityType.CommentChanged && a.DeletedAt == null))),
            withLinks: true);
        if (mine) all = all.Where(p => Dashboard.IsMine(p, _me.Id)).ToList();
        return Reporting.Weekly(all, activities, await _projects.LocationsAsync(), from, to, _clock.Today, await _projects.CalendarAsync());
    }

    // 여러 과제를 가로지르는 이력의 기간 상한. 넓은 기간을 고르면 사용자가 크기를 정하는 조회가 되므로 막는다
    public const int HistoryMaxYears = 1;

    // 변경 이력: 기간·과제·사람·종류 조건과 건수·정렬·쪽 나누기는 DB 에서 한다(필요한 열만 투영 — 과제 행 전체를 읽지 않는다).
    // 글자 검색만 투영한 뒤 메모리에서 한다 — SQLite(대소문자 구분)와 SQL Server(정렬 규칙)의 결과가 다르기 때문이다
    public async Task<HistoryPage> HistoryAsync(HistoryFilter f)
    {
        string? notice = null;
        string? projectTitle = null;
        if (f.ProjectId is int pid)
        {
            projectTitle = await _db.LiveProjects.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Title).FirstOrDefaultAsync();
        }

        var to = f.To ?? _clock.Today;
        // 과제 하나만 볼 때는 기본 기간을 그 과제의 처음부터로 둔다('이 과제 전체 이력'). 활동 수가 과제 하나로 묶이므로 상한도 두지 않는다
        var from = f.From ?? (f.ProjectId != null ? await FirstActivityDayAsync(f.ProjectId.Value, to) : to.AddDays(-30));
        if (from > to) (from, to) = (to, from);
        if (f.ProjectId == null && from < to.AddYears(-HistoryMaxYears))
        {
            from = to.AddYears(-HistoryMaxYears);
            notice = $"여러 과제의 이력은 한 번에 {HistoryMaxYears}년까지 볼 수 있어 시작일을 {from:yyyy-MM-dd} 로 맞췄습니다. 더 오래된 이력은 기간을 나눠 보거나 과제를 골라 보세요.";
        }
        var start = KoreaClock.StartOfDayUtc(from);
        var end = KoreaClock.StartOfDayUtc(to.AddDays(1));

        // 휴지통에 있는 과제의 이력은 보이지 않는다(복원하면 돌아온다)
        var range = _db.LiveActivities.AsNoTracking().Where(a => a.At >= start && a.At < end);
        if (f.ProjectId is int projectId) range = range.Where(a => a.ProjectId == projectId);

        // 사용자 고르기 목록: 이 기간(·과제)에 이력을 남긴 사람
        var users = (await range.Select(a => a.User!.Name).Distinct().ToListAsync()).Where(n => n.Length > 0).OrderBy(n => n).ToList();

        var q = range;
        if (!string.IsNullOrWhiteSpace(f.User))
        {
            var name = f.User.Trim();
            var userId = await _db.Users.AsNoTracking().Where(u => u.Name == name).Select(u => (int?)u.Id).FirstOrDefaultAsync();
            q = userId == null ? q.Where(a => false) : q.Where(a => a.UserId == userId.Value);
        }
        if (!string.IsNullOrWhiteSpace(f.Kind)) q = q.Where(Reporting.KindIs(f.Kind));
        var ordered = q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id);

        var text = (f.Q ?? "").Trim();
        List<HistoryRow> rows;
        int total, page, pages;
        if (text.Length == 0)
        {
            total = await q.CountAsync();
            pages = Math.Max(1, (int)Math.Ceiling(total / (double)HistoryPageSize));
            page = Math.Clamp(f.Page, 1, pages);
            rows = ToRows(await Rows(ordered.Skip((page - 1) * HistoryPageSize).Take(HistoryPageSize)).ToListAsync());
        }
        else
        {
            var found = ToRows(await Rows(ordered).ToListAsync())
                .Where(r => r.Project.Contains(text, StringComparison.OrdinalIgnoreCase)
                            || r.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                            || (r.Reason ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            total = found.Count;
            pages = Math.Max(1, (int)Math.Ceiling(total / (double)HistoryPageSize));
            page = Math.Clamp(f.Page, 1, pages);
            rows = found.Skip((page - 1) * HistoryPageSize).Take(HistoryPageSize).ToList();
        }
        return new HistoryPage(from, to, rows, total, page, pages, users, notice, f.ProjectId, projectTitle);

        // 화면에 보이는 열만 읽는다 (과제는 제목만)
        static IQueryable<HistoryRowData> Rows(IQueryable<Activity> source) => source.Select(a => new HistoryRowData(
            a.Id, a.ProjectId, a.Project!.Title, a.Type, a.Text, a.Reason, a.User!.Name, a.At));

        static List<HistoryRow> ToRows(List<HistoryRowData> list) => list.Select(a => new HistoryRow(
            a.Id, a.ProjectId, a.Project ?? "", Views.Key(a.Type), Reporting.KindOf(a.Type), a.Text, a.Reason, a.User ?? "", a.At)).ToList();
    }

    // 과제의 가장 오래된 활동 날짜(한국 날짜). 활동이 없으면 to
    private async Task<DateOnly> FirstActivityDayAsync(int projectId, DateOnly to)
    {
        var first = await _db.LiveActivities.AsNoTracking().Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.At).Select(a => (DateTime?)a.At).FirstOrDefaultAsync();
        return first == null ? to : KoreaClock.KstDate(DateTime.SpecifyKind(first.Value, DateTimeKind.Utc));
    }

    private sealed record HistoryRowData(int Id, int ProjectId, string? Project, ActivityType Type, string Text, string? Reason, string? User, DateTime At);
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
        ActivityType.Comment or ActivityType.CommentChanged => "comment",
        ActivityType.TargetChanged or ActivityType.WorkPlanChanged or ActivityType.BaselineSet => "schedule",
        ActivityType.StageCompleted or ActivityType.StageReopened or ActivityType.WorkEdited or ActivityType.CheckChanged => "progress",
        ActivityType.StateChanged or ActivityType.Created or ActivityType.Cloned or ActivityType.ProjectDeleted or ActivityType.ProjectRestored => "state",
        ActivityType.AttachmentAdded or ActivityType.AttachmentRemoved => "file",
        _ => "structure",
    };

    // 종류(KindOf) 조건을 DB 조건으로: 그 종류로 분류되는 활동 유형 전부(기본 분기 '구성 변경'에 떨어지는 유형 포함)를
    // KindOf 로 직접 골라 '유형 = A 또는 유형 = B …' 식을 만든다. 목록 매개 변수(IN·OPENJSON)를 쓰지 않아 두 DB 에서 같게 번역된다
    public static System.Linq.Expressions.Expression<Func<Activity, bool>> KindIs(string kind)
    {
        var a = System.Linq.Expressions.Expression.Parameter(typeof(Activity), "a");
        var type = System.Linq.Expressions.Expression.Property(a, nameof(Activity.Type));
        System.Linq.Expressions.Expression body = System.Linq.Expressions.Expression.Constant(false);
        foreach (var t in Enum.GetValues<ActivityType>().Where(t => KindOf(t) == kind))
            body = System.Linq.Expressions.Expression.OrElse(body,
                System.Linq.Expressions.Expression.Equal(type, System.Linq.Expressions.Expression.Constant(t)));
        return System.Linq.Expressions.Expression.Lambda<Func<Activity, bool>>(body, a);
    }

    // 해당 날짜가 속한 주의 월요일~일요일
    public static (DateOnly From, DateOnly To) WeekOf(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7;
        var from = day.AddDays(-offset);
        return (from, from.AddDays(6));
    }

    // 단계 리드타임 표본: 완료한 단계마다 앞 단계 완료일(첫 단계는 착수일)부터 이 단계 완료일까지.
    // 기준점은 지금까지 가장 늦은 완료일이다 — 순서를 거슬러 완료한 단계 뒤의 단계가 길게 잡히지 않게. 리드타임 화면과
    // 단계 기본 기간 힌트(StageLeadHints)가 같은 표본을 쓴다
    public static List<LeadSample> LeadSamples(Project p, WorkCalendar cal)
    {
        var list = new List<LeadSample>();
        var prev = p.StartDate;
        foreach (var s in Rules.Ordered(p))
        {
            if (s.CompletedDate == null) continue;
            var done = s.CompletedDate.Value;
            list.Add(new LeadSample(s, Math.Max(0, done.DayNumber - prev.DayNumber), cal.Distance(prev, done)));
            if (done > prev) prev = done;
        }
        return list;
    }

    // firstTargets: 단계 Id → 처음 정한 목표일(InsightService.FirstTargetsAsync). 없는 단계는 지금 목표일이 처음 값이다
    public static List<LeadGroup> LeadTimes(List<Project> all, WorkCalendar? cal = null, IReadOnlyDictionary<int, DateOnly>? firstTargets = null, bool excludeZero = false)
    {
        cal ??= WorkCalendar.WeekendsOnly;
        var groups = new List<LeadGroup>();
        foreach (var g in all.Where(p => p.State != ProjectState.Dropped).GroupBy(p => p.CategoryId)
                     .OrderBy(g => g.First().Category?.SortOrder ?? 0))
        {
            // 단계 이름별 표본
            var samples = new Dictionary<string, (int Order, List<LeadSample> List)>();
            var totals = new List<(int Days, int WorkDays)>();
            foreach (var p in g)
            {
                foreach (var x in LeadSamples(p, cal))
                {
                    if (!samples.TryGetValue(x.Stage.Name, out var e)) samples[x.Stage.Name] = e = (x.Stage.SortOrder, new List<LeadSample>());
                    e.List.Add(x);
                }
                if (p.State == ProjectState.Done && Rules.AllStagesDone(p))
                {
                    var end = p.Stages.Max(s => s.CompletedDate!.Value);
                    totals.Add((end.DayNumber - p.StartDate.DayNumber, cal.Distance(p.StartDate, end)));
                }
            }

            static double? Avg(List<int> v) => v.Count > 0 ? Math.Round(v.Average(), 1) : null;
            DateOnly? FirstTarget(Stage s) => firstTargets != null && firstTargets.TryGetValue(s.Id, out var f) ? f : s.TargetDate;
            var rows = new List<LeadRow>();
            foreach (var (name, (_, list)) in samples.OrderBy(kv => kv.Value.Order))
            {
                var used = excludeZero ? list.Where(x => !x.Zero).ToList() : list;
                var over = list.Where(x => x.Stage.TargetDate != null)
                    .Select(x => x.Stage.CompletedDate!.Value.DayNumber - x.Stage.TargetDate!.Value.DayNumber).ToList();
                var overFirst = list.Select(x => (Done: x.Stage.CompletedDate!.Value, First: FirstTarget(x.Stage)))
                    .Where(t => t.First != null).Select(t => t.Done.DayNumber - t.First!.Value.DayNumber).ToList();
                var overBase = list.Where(x => x.Stage.BaselineDate != null)
                    .Select(x => x.Stage.CompletedDate!.Value.DayNumber - x.Stage.BaselineDate!.Value.DayNumber).ToList();
                rows.Add(new LeadRow(name, used.Count,
                    used.Count > 0 ? Math.Round(used.Average(x => x.Days), 1) : 0,
                    used.Count > 0 ? used.Min(x => x.Days) : 0, used.Count > 0 ? used.Max(x => x.Days) : 0,
                    Avg(over), over.Count(o => o > 0),
                    used.Count > 0 ? Math.Round(used.Average(x => Math.Max(0, x.WorkDays)), 1) : 0,
                    list.Count(x => x.Zero),
                    Avg(overFirst), overFirst.Count, Avg(overBase), overBase.Count, over.Count));
            }

            groups.Add(new LeadGroup(g.Key, g.First().Category?.Name ?? "", g.Count(), totals.Count,
                totals.Count > 0 ? Math.Round(totals.Average(t => t.Days), 1) : null,
                rows.Count > 0 ? Math.Max(1, rows.Max(r => r.MaxDays)) : 1, rows,
                totals.Count > 0 ? Math.Round(totals.Average(t => t.WorkDays), 1) : null));
        }
        return groups;
    }

    // 활동 하나를 보고 줄로
    private static WeeklyItem Item(Activity a, List<string>? more = null) =>
        new(a.Text, a.Reason, a.User?.Name ?? "", KoreaClock.KstDate(a.At), more);

    // 사람이 아니라 단계 완료·완료 취소에 따라 저절로 바뀐 과제 상태 (ProjectService.SyncState)
    public static bool IsAutoState(Activity a) =>
        a.Type == ActivityType.StateChanged && (a.Text == ProjectService.AutoDoneText || a.Text == ProjectService.AutoReopenText);

    // 일정 변경 결정: 목표일·작업 일정 변경을 (BatchId ?? Id) 로 묶는다. 묶음의 첫 행(먼저 남긴 — 사람이 직접 바꾼 대상)을 대표로,
    // 나머지(연쇄 이동·선행 연동으로 함께 옮겨진 대상)는 More 로 접는다
    public static List<WeeklyItem> Decisions(IEnumerable<Activity> acts) =>
        acts.Where(a => a.Type is ActivityType.TargetChanged or ActivityType.WorkPlanChanged)
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .GroupBy(a => a.BatchId?.ToString() ?? "#" + a.Id)
            .Select(g =>
            {
                var rows = g.ToList();
                return Item(rows[0], rows.Count > 1 ? rows.Skip(1).Select(a => a.Text).ToList() : null);
            })
            .ToList();

    // 주간 보고. 지연·밀린 항목은 asOf = min(그 주 마지막 날, 오늘) 기준(Rules.DelayDaysAsOf)이라 지난 주를 다시 열어도 그 주의 모습이다.
    // 그 주가 끝난 뒤 등록한 과제는 넣지 않는다. 진행률·현재 단계는 지금 값이다(스냅샷이 없다 — 화면이 밝힌다).
    // 실적은 날짜로 본다: 단계는 완료일, 작업은 실적 종료일이 그 주 안이면 그 주 실적이다(활동을 남긴 시각이 아니라)
    // cal: 근무일 달력 — 주면 이번 주 보고에 '예상 지연 +n일'(전진 계산, 과제의 Links 가 읽혀 있어야 한다)을 싣는다
    public static WeeklyReport Weekly(List<Project> all, List<Activity> activities, LocationIndex locations, DateOnly from, DateOnly to, DateOnly today,
        WorkCalendar? cal = null)
    {
        var asOf = to < today ? to : today;
        var byProject = activities.GroupBy(a => a.ProjectId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.At).ThenBy(a => a.Id).ToList());
        var nextFrom = to.AddDays(1);
        var nextTo = to.AddDays(7);
        var projects = new List<WeeklyProject>();
        // 그 주가 끝나기 전에 등록한 과제만 (지난 주 보고에 그 뒤 등록한 과제가 끼지 않게)
        var existing = all.Where(p => KoreaClock.KstDate(p.CreatedAt) <= to).ToList();
        int stateCount = 0, completed = 0;

        foreach (var p in existing)
        {
            byProject.TryGetValue(p.Id, out var acts);
            acts ??= new List<Activity>();
            var active = Rules.ActiveAsOf(p, asOf, today);
            // 움직임 없는 보류·완료·중단 과제는 뺀다. 댓글 수정·삭제 기록과 지운 댓글은 보고에 실을 내용이 없으므로 움직임으로 보지 않는다
            if (!active && !acts.Any(a => a.Type != ActivityType.CommentChanged && a.DeletedAt == null)) continue;

            // 이번 주 실적: 단계 완료일·작업 실적 종료일이 그 주 안. 단계를 끝낸 사람은 그 주의 완료 활동에서 찾는다
            var done = Rules.Ordered(p)
                .Where(s => s.CompletedDate != null && s.CompletedDate.Value >= from && s.CompletedDate.Value <= to)
                .Select(s => new WeeklyItem($"[{s.Name}] 단계 완료", null,
                    acts.LastOrDefault(a => a.Type == ActivityType.StageCompleted && a.StageId == s.Id)?.User?.Name ?? "", s.CompletedDate!.Value))
                .Concat(Rules.AllWorks(p)
                    .Where(w => w.ActualEnd != null && w.ActualEnd.Value >= from && w.ActualEnd.Value <= to)
                    .Select(w => new WeeklyItem($"작업 완료: {w.Title}", null, w.Owner?.Name ?? "", w.ActualEnd!.Value)))
                .OrderBy(x => x.Date)
                .ToList();

            var changes = Decisions(acts);
            var states = acts.Where(a => a.Type is ActivityType.StateChanged or ActivityType.BaselineSet).Select(a => Item(a)).ToList();
            stateCount += acts.Count(a => (a.Type is ActivityType.StateChanged or ActivityType.BaselineSet) && !IsAutoState(a));
            completed += acts.Count(a => a.Type == ActivityType.StateChanged && a.Text == ProjectService.AutoDoneText);
            var comments = acts.Where(a => a.Type == ActivityType.Comment && a.DeletedAt == null).Select(a => Item(a)).ToList();

            // 밀린 항목: 기준일에 목표일이 지났는데 끝나지 않은 단계·작업 (다음 주 계획에는 기한이 지난 것이 오르지 않으므로 따로 모은다)
            var overdue = new List<WeeklyLate>();
            var next = new List<string>();
            if (active)
            {
                foreach (var s in Rules.Ordered(p))
                {
                    var late = Rules.StageLateDaysAsOf(s, asOf, today);
                    if (late > 0) overdue.Add(new WeeklyLate("단계", s.Name, p.Owner?.Name, s.TargetDate!.Value, late));
                    foreach (var w in s.Works)
                    {
                        var wl = Rules.WorkLateDaysAsOf(w, s, asOf, today);
                        if (wl > 0) overdue.Add(new WeeklyLate("작업", w.Title, w.Owner?.Name, w.PlanEnd, wl));
                    }
                }
                overdue = overdue.OrderByDescending(x => x.LateDays).ThenBy(x => x.Kind).ThenBy(x => x.Title).ToList();

                next.AddRange(Rules.Ordered(p)
                    .Where(s => s.CompletedDate == null && s.TargetDate != null && s.TargetDate.Value >= nextFrom && s.TargetDate.Value <= nextTo)
                    .Select(s => $"[{s.Name}] 단계 목표 {s.TargetDate!.Value:MM-dd}"));
                next.AddRange(p.Stages.SelectMany(s => s.Works.Where(w => Rules.IsOpen(w, s)))   // 완료된 단계의 작업은 뺀다
                    .Where(w => w.PlanEnd >= nextFrom && w.PlanEnd <= nextTo)
                    .OrderBy(w => w.PlanEnd)
                    .Select(w => $"{w.Title} 종료 목표 {w.PlanEnd:MM-dd}" + (w.Owner != null ? $" ({w.Owner.Name})" : "")));
            }

            // 상태도 기준일로: 그때 진행 중이던 과제(지금은 완료)는 '진행'으로 보인다
            var state = active ? ProjectState.Active : p.State;
            var delay = Rules.DelayDaysAsOf(p, asOf, today);
            var current = Rules.CurrentStage(p);
            projects.Add(new WeeklyProject(p.Id, p.Title, p.Category?.Name ?? "", locations.Get(p.CorporationId)?.Name ?? "", p.Owner?.Name ?? "",
                Views.StateLabel(state, delay),
                Rules.ProjectProgress(p), delay, current?.Name, current?.TargetDate,
                done, changes, comments, next, Views.ProjectChip(state, delay), states, overdue,
                cal != null && asOf >= today && delay == 0 ? Rules.RiskDays(Rules.RiskReasons(p, today, Rules.ForwardPass(p, cal, today))) : 0));
        }

        projects = projects.OrderByDescending(x => x.DelayDays).ThenBy(x => x.Title).ToList();
        return new WeeklyReport(from, to, today,
            existing.Count(p => Rules.ActiveAsOf(p, asOf, today)),
            existing.Count(p => Rules.DelayDaysAsOf(p, asOf, today) > 0),
            projects.Sum(x => x.Done.Count), projects.Sum(x => x.Changes.Count), projects,
            projects.Sum(x => x.ChangeTargets), stateCount, completed, asOf);
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
                    var late = Rules.IsLate(w, s, today);
                    var dday = Rules.DaysLeft(w.PlanEnd, today);
                    works.Add(new MyWork(p.Id, p.Title, w.Id, s.Name, w.Title, w.OwnerId == userId ? "담당" : "부담당",
                        w.PlanStart, w.PlanEnd, w.Progress, Views.Key(Rules.StatusOf(w)), late, dday,
                        w.Version, w.ActualStart, w.ActualEnd, MyGroup(late, dday, w.PlanStart, today)));
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
            ordered.Where(w => w.Group == "late").ToList(),
            ordered.Where(w => w.Group == "soon").ToList(),
            ordered.Where(w => w.Group == "later").ToList(),
            stages.OrderBy(s => s.Target ?? DateOnly.MaxValue).ToList());
    }

    // 내 할 일 묶음: 지연 → 이번 주(곧 Rules.SoonDays 안에 끝나야 하거나 이미 시작했어야 하는 작업) → 이후
    public static string MyGroup(bool late, int dday, DateOnly planStart, DateOnly today) =>
        late ? "late" : Rules.IsSoon(dday) || planStart <= today ? "soon" : "later";

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
            // 리드타임 화면과 같은 표본(LeadSamples) — 0일 표본은 늘 뺀다
            var seen = new HashSet<string>();
            foreach (var x in LeadSamples(p, cal))
            {
                if (!seen.Add(x.Stage.Name) || x.Zero) continue;
                if (!samples.TryGetValue((p.CategoryId, x.Stage.Name), out var list)) samples[(p.CategoryId, x.Stage.Name)] = list = new List<int>();
                list.Add(x.WorkDays);
            }
        }
        return samples.Where(kv => kv.Value.Count >= LeadHintMinSamples)
            .ToDictionary(kv => kv.Key, kv => new StageLeadHint(Math.Round(kv.Value.Average(), 1), kv.Value.Count));
    }
}

// 단계 기본 기간 힌트: 과거 평균 근무일과 표본(완료 과제) 수
public record StageLeadHint(double AvgWorkDays, int Count);
