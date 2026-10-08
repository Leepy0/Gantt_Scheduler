namespace GanttScheduler.Domain;

public enum StageStatus { Planned, Current, Late, Done }
public enum WorkStatus { Planned, InProgress, Done }

// 규칙 위반 — 화면에는 400 응답의 메시지로 그대로 전달된다
public class RuleException : Exception
{
    public string? Field { get; }
    public RuleException(string message, string? field = null) : base(message) => Field = field;
}

// 상태·진행률·예상 종료 계산. DB 접근 없는 순수 함수만 둔다
public static class Rules
{
    public const int ProgressStep = 5;

    public static IEnumerable<Stage> Ordered(Project p) => p.Stages.OrderBy(s => s.SortOrder).ThenBy(s => s.Id);

    public static WorkStatus StatusOf(WorkItem w) =>
        w.ActualEnd != null ? WorkStatus.Done
        : w.ActualStart != null ? WorkStatus.InProgress
        : WorkStatus.Planned;

    // 미완료인데 목표 종료일이 지남
    public static bool IsLate(WorkItem w, DateOnly today) => w.ActualEnd == null && w.PlanEnd < today;

    // 완료했지만 목표 종료일보다 늦게 끝남
    public static bool IsDoneLate(WorkItem w) => w.ActualEnd != null && w.ActualEnd.Value > w.PlanEnd;

    // 단계 진행률: 완료 100, 작업이 있으면 작업 진행률 평균, 없으면 0
    public static int StageProgress(Stage s)
    {
        if (s.CompletedDate != null) return 100;
        if (s.Works.Count == 0) return 0;
        return (int)Math.Round(s.Works.Average(w => (double)w.Progress), MidpointRounding.AwayFromZero);
    }

    // 과제 진행률: 단계 진행률의 평균 (단계마다 같은 비중)
    public static int ProjectProgress(Project p)
    {
        if (p.Stages.Count == 0) return 0;
        return (int)Math.Round(p.Stages.Average(s => (double)StageProgress(s)), MidpointRounding.AwayFromZero);
    }

    public static Stage? CurrentStage(Project p) => Ordered(p).FirstOrDefault(s => s.CompletedDate == null);

    public static StageStatus StatusOf(Stage s, Project p, DateOnly today)
    {
        if (s.CompletedDate != null) return StageStatus.Done;
        if (s.TargetDate != null && s.TargetDate.Value < today) return StageStatus.Late;
        // 순서상 첫 미완료 단계가 '진행 중'
        return ReferenceEquals(CurrentStage(p), s) ? StageStatus.Current : StageStatus.Planned;
    }

    // 작업 일정에서 올려 계산한 단계 예상 종료일. 작업이 없거나 단계가 완료됐으면 null
    public static DateOnly? Forecast(Stage s, DateOnly today)
    {
        if (s.CompletedDate != null || s.Works.Count == 0) return null;
        DateOnly max = DateOnly.MinValue;
        foreach (var w in s.Works)
        {
            // 미완료 작업이 목표일을 넘겼으면 최소 오늘까지는 걸린다
            var end = w.ActualEnd ?? (w.PlanEnd < today ? today : w.PlanEnd);
            if (end > max) max = end;
        }
        return max;
    }

    // 예상 종료일이 단계 목표일을 넘는 일수 (넘지 않으면 0)
    public static int ForecastOverDays(Stage s, DateOnly today)
    {
        var f = Forecast(s, today);
        if (f == null || s.TargetDate == null) return 0;
        var d = f.Value.DayNumber - s.TargetDate.Value.DayNumber;
        return d > 0 ? d : 0;
    }

    // 과제 지연 일수: 진행 중 과제에서 목표일이 지난 미완료 단계 중 가장 오래 밀린 것 기준
    public static int DelayDays(Project p, DateOnly today)
    {
        if (p.State != ProjectState.Active) return 0;
        int max = 0;
        foreach (var s in p.Stages)
        {
            if (s.CompletedDate != null || s.TargetDate == null) continue;
            var d = today.DayNumber - s.TargetDate.Value.DayNumber;
            if (d > max) max = d;
        }
        return max;
    }

    // 지연 기준이 된 단계 (가장 오래 밀린 미완료 단계)
    public static Stage? MostDelayedStage(Project p, DateOnly today) =>
        p.Stages.Where(s => s.CompletedDate == null && s.TargetDate != null && s.TargetDate.Value < today)
                .OrderBy(s => s.TargetDate).FirstOrDefault();

    // 마지막 단계 목표일 = 과제 목표 종료일
    public static DateOnly? EndTarget(Project p)
    {
        DateOnly? max = null;
        foreach (var s in p.Stages)
            if (s.TargetDate != null && (max == null || s.TargetDate.Value > max.Value)) max = s.TargetDate;
        return max;
    }

    // 모든 단계가 끝났는가 (단계가 하나도 없으면 false)
    public static bool AllStagesDone(Project p) => p.Stages.Count > 0 && p.Stages.All(s => s.CompletedDate != null);

    // 입고 추천일 = 검수 단계 일자(완료일 우선, 없으면 목표일) + 법인 해상 운송 소요일
    public static DateOnly? InboundRecommendation(Project p, int oceanLeadDays)
    {
        var inspection = Ordered(p).LastOrDefault(s => s.Kind == StageKind.Inspection);
        var baseDate = inspection?.CompletedDate ?? inspection?.TargetDate;
        return baseDate?.AddDays(oceanLeadDays);
    }

    // ── 기준선 ───────────────────────────────────────────────────────────

    // 기준선 대비 밀린 일수 (당겨졌으면 음수). 기준선이나 목표일이 없으면 null
    public static int? SlipDays(Stage s) =>
        s.BaselineDate == null || s.TargetDate == null ? null : s.TargetDate.Value.DayNumber - s.BaselineDate.Value.DayNumber;

    public static DateOnly? BaselineEnd(Project p)
    {
        DateOnly? max = null;
        foreach (var s in p.Stages)
            if (s.BaselineDate != null && (max == null || s.BaselineDate.Value > max.Value)) max = s.BaselineDate;
        return max;
    }

    // ── 의존 관계 ────────────────────────────────────────────────────────

    public static IEnumerable<WorkItem> AllWorks(Project p) => p.Stages.SelectMany(s => s.Works);

    // 후행 작업이 선행 작업 종료(다음 근무일 + 간격)보다 먼저 시작하는가
    public static bool Violates(WorkItem predecessor, WorkItem successor, int lagDays, WorkCalendar cal) =>
        successor.PlanStart < cal.EarliestStartAfter(predecessor.PlanEnd, lagDays);

    // 작업별 여유 일수: 이 작업이 며칠 밀려도 단계 목표일이 지켜지는가.
    // 자기 단계 목표일까지 남은 날과, 후행 작업 시작까지의 틈 + 후행 작업의 여유 중 가장 작은 값.
    // 목표일이 없는 단계처럼 제약이 없으면 결과에 넣지 않는다
    public static Dictionary<int, int> Floats(Project p, WorkCalendar cal)
    {
        var works = new Dictionary<int, (WorkItem Work, Stage Stage)>();
        foreach (var s in p.Stages)
            foreach (var w in s.Works) works[w.Id] = (w, s);
        var successors = p.Links.GroupBy(l => l.PredecessorId).ToDictionary(g => g.Key, g => g.ToList());
        var memo = new Dictionary<int, int?>();
        var visiting = new HashSet<int>();

        int? Float(int id)
        {
            if (memo.TryGetValue(id, out var known)) return known;
            if (!works.TryGetValue(id, out var x) || !visiting.Add(id)) return null;   // 순환은 저장 시 막지만 방어

            int? best = null;
            if (x.Stage.TargetDate != null && x.Stage.CompletedDate == null)
                best = x.Stage.TargetDate.Value.DayNumber - x.Work.PlanEnd.DayNumber;

            if (successors.TryGetValue(id, out var links))
            {
                foreach (var l in links)
                {
                    if (!works.TryGetValue(l.SuccessorId, out var next)) continue;
                    var later = Float(l.SuccessorId);
                    if (later == null) continue;
                    var gap = next.Work.PlanStart.DayNumber - cal.EarliestStartAfter(x.Work.PlanEnd, l.LagDays).DayNumber;
                    var total = gap + later.Value;
                    if (best == null || total < best.Value) best = total;
                }
            }

            visiting.Remove(id);
            memo[id] = best;
            return best;
        }

        var result = new Dictionary<int, int>();
        foreach (var id in works.Keys)
            if (Float(id) is int f) result[id] = f;
        return result;
    }

    // from 에서 의존 관계를 따라가면 to 에 닿는가 (새 관계가 순환을 만드는지 검사)
    public static bool Reaches(IEnumerable<(int From, int To)> links, int from, int to)
    {
        var next = links.GroupBy(l => l.From).ToDictionary(g => g.Key, g => g.Select(l => l.To).ToList());
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == to) return true;
            if (!seen.Add(cur) || !next.TryGetValue(cur, out var list)) continue;
            foreach (var n in list) stack.Push(n);
        }
        return false;
    }

    public static void ValidateProgress(int progress)
    {
        if (progress < 0 || progress > 100 || progress % ProgressStep != 0)
            throw new RuleException("진행률은 0~100 사이 5 단위로 입력합니다.", "progress");
    }
}
