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

    // 완료된 단계의 작업은 단계와 함께 끝난 것으로 본다. 단계만 완료하는 운영(규칙 2)을 허용하므로
    // 실적 종료가 비어 있어도 지연·임계·내 할 일에 남기지 않는다 (상세·내 할 일·주간 보고가 이 판정을 함께 쓴다)
    public static bool IsOpen(WorkItem w, Stage stage) => w.ActualEnd == null && stage.CompletedDate == null;

    public static bool IsLate(WorkItem w, Stage stage, DateOnly today) => IsOpen(w, stage) && w.PlanEnd < today;

    // 의존 관계에 따라 일정을 옮길 수 있는 작업: 아직 시작하지 않았고 단계도 끝나지 않았다.
    // 밀기·위반 표시·저장할 때의 선행 검사가 모두 이 판정을 쓴다 — 서로 다르게 보면 '표시는 없는데 저장이 막히는' 일이 생긴다
    public static bool IsMovable(WorkItem w, Stage stage) => IsMovable(w.ActualStart, stage);

    // 저장하려는 값(실적 시작, 옮겨 갈 단계)으로 판정할 때
    public static bool IsMovable(DateOnly? actualStart, Stage stage) => actualStart == null && stage.CompletedDate == null;

    // 실적 종료 없이 단계 완료로 함께 끝난 것으로 보는 작업 (화면은 '진행 중'·'시작 전' 대신 단계 완료일에 끝난 것으로 그린다)
    public static bool IsClosedByStage(WorkItem w, Stage stage) => w.ActualEnd == null && stage.CompletedDate != null;

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

    // 의존 관계 하나가 후행 작업에 거는 가장 이른 시작일. 지금은 "끝나야 시작"(FS) 한 가지다.
    // 의존 판정은 모두 이 함수를 거친다 — 유형(SS·FF)을 더할 때 여기만 바꾸면 밀기·검증·여유·위반 표시가 함께 따라온다
    public static DateOnly EarliestStart(WorkLink link, DateOnly predecessorStart, DateOnly predecessorEnd, WorkCalendar cal) =>
        cal.EarliestStartAfter(predecessorEnd, link.LagDays);

    public static DateOnly EarliestStart(WorkLink link, WorkItem predecessor, WorkCalendar cal) =>
        EarliestStart(link, predecessor.PlanStart, predecessor.PlanEnd, cal);

    // 후행 작업이 선행 작업이 허용하는 날보다 먼저 시작하는가
    public static bool Violates(WorkLink link, WorkItem predecessor, WorkItem successor, WorkCalendar cal) =>
        successor.PlanStart < EarliestStart(link, predecessor, cal);

    public static bool Violates(WorkItem predecessor, WorkItem successor, int lagDays, WorkCalendar cal) =>
        Violates(new WorkLink { LagDays = lagDays }, predecessor, successor, cal);

    // 작업의 근무일 기간. 주말에만 걸친 작업도 1일로 본다(옮긴 뒤 기간이 0 이 되지 않게)
    public static int WorkDays(WorkItem w, WorkCalendar cal) => Math.Max(1, cal.WorkingDays(w.PlanStart, w.PlanEnd));

    // 시작일(휴일이면 다음 근무일)부터 근무일 days 일을 차지하는 일정
    public static (DateOnly Start, DateOnly End) Place(DateOnly start, int days, WorkCalendar cal)
    {
        var s = cal.NextWorkingDay(start);
        return (s, cal.AddWorkingDays(s, Math.Max(1, days) - 1));
    }

    // 작업을 근무일 n 일만큼 옮긴다(연쇄 이동). 근무일 기간은 그대로
    public static (DateOnly Start, DateOnly End) ShiftWork(WorkItem w, int workingDays, WorkCalendar cal)
    {
        var days = WorkDays(w, cal);
        var start = cal.AddWorkingDays(cal.NextWorkingDay(w.PlanStart), workingDays);
        return Place(start, days, cal);
    }

    // 작업별 여유(근무일): 이 작업이 며칠 밀려도 단계 목표일과 후행 작업이 지켜지는가.
    // 자기 단계 목표일까지 남은 근무일과, 후행 작업 시작까지의 틈 + 후행 작업의 여유 중 가장 작은 값.
    // 근무일로 세므로 목표일이 주말이고 작업이 직전 금요일에 끝나면 여유 0(임계)이다.
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
                best = cal.Distance(x.Work.PlanEnd, x.Stage.TargetDate.Value);

            if (successors.TryGetValue(id, out var links))
            {
                foreach (var l in links)
                {
                    if (!works.TryGetValue(l.SuccessorId, out var next)) continue;
                    var later = Float(l.SuccessorId);
                    if (later == null) continue;
                    var gap = cal.Distance(EarliestStart(l, x.Work, cal), next.Work.PlanStart);
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

    // ── 일정 재계산(의존 밀기) ────────────────────────────────────────────

    // 단계 목표일 연쇄 이동 때 함께 옮길 수 있는 작업: 이 단계보다 뒤에 있는 미완료 단계의 미착수 작업
    public static IEnumerable<WorkItem> CascadeWorks(Project p, Stage stage) =>
        Ordered(p).Where(x => x.SortOrder > stage.SortOrder && x.CompletedDate == null)
                  .SelectMany(x => x.Works.Where(w => IsMovable(w, x)).OrderBy(w => w.PlanStart).ThenBy(w => w.Id));

    // 재계산이 옮긴 작업 하나. Cause 는 가장 늦은 시작을 요구한 선행 작업
    public record ScheduleMove(WorkItem Work, DateOnly OldStart, DateOnly OldEnd, DateOnly NewStart, DateOnly NewEnd, WorkItem Cause);

    // 선행 일정에 맞춰 후행 작업을 뒤로 민다. 엔티티는 바꾸지 않고 이동 목록만 돌려준다(저장·미리보기가 같은 계산을 쓴다).
    // - from 과 거기서 의존 관계로 닿는 작업만 본다(from 이 null 이면 과제 전체). 선행이 둘 이상 모이는 작업도
    //   위상 정렬(Kahn) 순서로 모든 선행이 정해진 뒤 한 번만 계산한다 — 깊이 우선으로 한 경로씩 밀면 덜 밀린다.
    // - 가장 이른 시작 = 모든 선행(범위 밖 선행은 지금 일정)의 EarliestStart 중 최댓값. 그보다 이르면 근무일 기간을 지킨 채 옮긴다.
    // - 이미 시작했거나 완료된 단계의 작업은 옮기지 않는다. 앞으로 당기지 않는다.
    // - 순환이 있으면(입력 단계에서 막지만) 순환에 걸린 작업은 건드리지 않고 멈춘다.
    // 연결은 탐색 속성(Predecessor/Successor)을 먼저 보고, 없으면 Id 로 찾는다 — 아직 저장 전(Id 0)인 작업·연결도 다룬다
    public static List<ScheduleMove> Reschedule(Project p, WorkCalendar cal, IEnumerable<WorkItem>? from = null)
    {
        var stageOf = new Dictionary<WorkItem, Stage>();
        var byId = new Dictionary<int, WorkItem>();
        foreach (var s in p.Stages)
            foreach (var w in s.Works)
            {
                stageOf.TryAdd(w, s);
                if (w.Id != 0) byId.TryAdd(w.Id, w);
            }

        WorkItem? Resolve(WorkItem? nav, int id) =>
            nav != null && stageOf.ContainsKey(nav) ? nav : id != 0 && byId.TryGetValue(id, out var w) ? w : null;

        var preds = new Dictionary<WorkItem, List<(WorkLink Link, WorkItem Work)>>();
        var succs = new Dictionary<WorkItem, List<WorkItem>>();
        foreach (var l in p.Links)
        {
            var a = Resolve(l.Predecessor, l.PredecessorId);
            var b = Resolve(l.Successor, l.SuccessorId);
            if (a == null || b == null || ReferenceEquals(a, b)) continue;
            if (!preds.TryGetValue(b, out var pl)) preds[b] = pl = new();
            pl.Add((l, a));
            if (!succs.TryGetValue(a, out var sl)) succs[a] = sl = new();
            sl.Add(b);
        }

        // 영향받는 부분 그래프: 시작 작업과 그 후행 전부 (원래 순서를 지켜 결과를 안정적으로)
        var nodes = new HashSet<WorkItem>();
        var order = new List<WorkItem>();
        var stack = new Stack<WorkItem>();
        foreach (var w in (from ?? stageOf.Keys).Where(stageOf.ContainsKey).Reverse()) stack.Push(w);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!nodes.Add(cur)) continue;
            order.Add(cur);
            if (succs.TryGetValue(cur, out var next))
                for (var i = next.Count - 1; i >= 0; i--) stack.Push(next[i]);
        }

        var indegree = order.ToDictionary(w => w, w => preds.TryGetValue(w, out var pl) ? pl.Count(x => nodes.Contains(x.Work)) : 0);
        var queue = new Queue<WorkItem>(order.Where(w => indegree[w] == 0));
        var dates = new Dictionary<WorkItem, (DateOnly Start, DateOnly End)>();
        var moves = new List<ScheduleMove>();

        while (queue.Count > 0)
        {
            var w = queue.Dequeue();
            if (IsMovable(w, stageOf[w]) && preds.TryGetValue(w, out var incoming))
            {
                DateOnly? earliest = null;
                WorkItem? cause = null;
                foreach (var (link, pred) in incoming)
                {
                    var (ps, pe) = dates.TryGetValue(pred, out var moved) ? moved : (pred.PlanStart, pred.PlanEnd);
                    var e = EarliestStart(link, ps, pe, cal);
                    if (earliest == null || e > earliest.Value) { earliest = e; cause = pred; }
                }
                if (earliest != null && w.PlanStart < earliest.Value)
                {
                    var (ns, ne) = Place(earliest.Value, WorkDays(w, cal), cal);
                    dates[w] = (ns, ne);
                    moves.Add(new ScheduleMove(w, w.PlanStart, w.PlanEnd, ns, ne, cause!));
                }
            }
            if (succs.TryGetValue(w, out var next))
                foreach (var n in next)
                    if (nodes.Contains(n) && --indegree[n] == 0) queue.Enqueue(n);
        }
        return moves;
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

    // ── 날짜 범위 ───────────────────────────────────────────────────────

    // 오타(2620년, 9999-12-31 등) 하나가 근무일 계산·간트 그리기를 멈추게 하지 않도록 받는 범위를 정한다
    public static readonly DateOnly MinDate = new(2000, 1, 1);
    public static readonly DateOnly MaxDate = new(2099, 12, 31);
    public const int YearsBeforeStart = 1;          // 일정은 과제 착수일 1년 전부터
    public const int YearsAfterStart = 10;          // 착수일 10년 뒤까지
    public const int StartYearsFromToday = 10;      // 착수일 자체는 오늘 기준 앞뒤 10년

    // 단계·작업 날짜가 받아들여지는 범위 (절대 범위 ∩ 착수일 −1년 ~ +10년)
    public static (DateOnly Min, DateOnly Max) DateRange(DateOnly start)
    {
        var anchor = Clamp(start, MinDate, MaxDate);    // 범위 밖 옛 데이터의 착수일이어도 AddYears 가 넘치지 않게
        return (Clamp(anchor.AddYears(-YearsBeforeStart), MinDate, MaxDate), Clamp(anchor.AddYears(YearsAfterStart), MinDate, MaxDate));
    }

    public static (DateOnly Min, DateOnly Max) StartDateRange(DateOnly today)
    {
        var anchor = Clamp(today, MinDate, MaxDate);
        return (Clamp(anchor.AddYears(-StartYearsFromToday), MinDate, MaxDate), Clamp(anchor.AddYears(StartYearsFromToday), MinDate, MaxDate));
    }

    // 과제 일정 날짜 검사 (null 은 통과)
    public static void ValidateDate(DateOnly? date, DateOnly start, string label, string field)
    {
        if (date == null) return;
        var (min, max) = DateRange(start);
        if (date.Value < min || date.Value > max)
            throw new RuleException($"{label}은(는) {min:yyyy-MM-dd} ~ {max:yyyy-MM-dd} 사이로 입력해 주세요. (착수일 기준 {YearsBeforeStart}년 전 ~ {YearsAfterStart}년 뒤)", field);
    }

    // 과제 착수일 검사 (등록·수정·복제)
    public static void ValidateStartDate(DateOnly date, DateOnly today, string field = "startDate")
    {
        var (min, max) = StartDateRange(today);
        if (date < min || date > max)
            throw new RuleException($"착수일은 {min:yyyy-MM-dd} ~ {max:yyyy-MM-dd} 사이로 입력해 주세요.", field);
    }

    // 과제의 날짜 중 착수일 start 기준 범위를 벗어난 첫 번째 (착수일을 옮기거나 복제할 때 기존 일정이 범위 밖으로 밀려나는지 본다)
    public static DateOnly? FirstOutOfRange(Project p, DateOnly start)
    {
        var (min, max) = DateRange(start);
        foreach (var s in p.Stages)
        {
            foreach (var d in new[] { s.TargetDate, s.CompletedDate })
                if (d != null && (d.Value < min || d.Value > max)) return d;
            foreach (var w in s.Works)
                foreach (var d in new DateOnly?[] { w.PlanStart, w.PlanEnd, w.ActualStart, w.ActualEnd })
                    if (d != null && (d.Value < min || d.Value > max)) return d;
        }
        return null;
    }

    private static DateOnly Clamp(DateOnly d, DateOnly min, DateOnly max) => d < min ? min : d > max ? max : d;

    public static void ValidateProgress(int progress)
    {
        if (progress < 0 || progress > 100 || progress % ProgressStep != 0)
            throw new RuleException("진행률은 0~100 사이 5 단위로 입력합니다.", "progress");
    }
}
