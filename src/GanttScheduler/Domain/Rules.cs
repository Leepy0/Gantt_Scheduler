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
        if (IsLate(s, today)) return StageStatus.Late;
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
    public static int ForecastOverDays(Stage s, DateOnly today) => OverDays(Forecast(s, today), s.TargetDate);

    // 전진 계산(ForwardPass)의 예상 종료로 올린 단계 예상 종료 — 선행 작업의 지연이 후행으로 전파된 값.
    // 작업을 읽는 화면(상세)에서 쓰고, 작업 의존을 다루지 않는 대시보드·목록은 위의 간단한 계산을 쓴다
    public static DateOnly? Forecast(Stage s, DateOnly today, IReadOnlyDictionary<WorkItem, Expected>? expected)
    {
        if (expected == null) return Forecast(s, today);
        if (s.CompletedDate != null || s.Works.Count == 0) return null;
        DateOnly max = DateOnly.MinValue;
        foreach (var w in s.Works)
        {
            var end = expected.TryGetValue(w, out var e) ? e.End : w.ActualEnd ?? (w.PlanEnd < today ? today : w.PlanEnd);
            if (end > max) max = end;
        }
        return max;
    }

    public static int ForecastOverDays(Stage s, DateOnly today, IReadOnlyDictionary<WorkItem, Expected>? expected) =>
        OverDays(Forecast(s, today, expected), s.TargetDate);

    // 예상 종료가 목표일을 넘는 달력 일수 (넘지 않거나 어느 하나가 없으면 0)
    public static int OverDays(DateOnly? forecast, DateOnly? target)
    {
        if (forecast == null || target == null) return 0;
        var d = forecast.Value.DayNumber - target.Value.DayNumber;
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
    public static DateOnly? InboundRecommendation(Project p, int oceanLeadDays) => InboundRecommendation(Ordered(p), oceanLeadDays);

    // 순서대로 놓인 단계로 계산한다 — 단계 일정 표·등록 초안처럼 아직 저장하지 않은 날짜로 추천일을 다시 볼 때
    public static DateOnly? InboundRecommendation(IEnumerable<Stage> ordered, int oceanLeadDays)
    {
        var inspection = ordered.LastOrDefault(s => s.Kind == StageKind.Inspection);
        var baseDate = inspection?.CompletedDate ?? inspection?.TargetDate;
        if (baseDate == null) return null;
        // 아직 검증하지 않은 날짜(초안·미리보기의 9999-12-31 같은 오타)로도 부르므로 DateOnly 범위를 넘으면 추천하지 않는다
        var day = (long)baseDate.Value.DayNumber + oceanLeadDays;
        return day < DateOnly.MinValue.DayNumber || day > DateOnly.MaxValue.DayNumber ? null : DateOnly.FromDayNumber((int)day);
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

    // 의존 간격(근무일)의 범위. 음수(앞당김)는 받지 않는다
    public const int MaxLagDays = 250;

    public static void ValidateLag(int lagDays, string field = "predecessors")
    {
        if (lagDays < 0 || lagDays > MaxLagDays)
            throw new RuleException($"의존 간격은 0~{MaxLagDays} 근무일로 입력해 주세요.", field);
    }

    // 의존 관계 하나가 후행 작업에 거는 가장 이른 시작일 (결과는 근무일). 의존 판정은 모두 이 함수를 거친다 —
    // 밀기(Reschedule)·저장 검사·여유(Floats)·위반 표시·전진 계산(ForwardPass)이 같은 답을 쓴다.
    //  FS(끝나면 시작): 선행 종료 다음 근무일 + 간격
    //  SS(같이 시작): 선행 시작일(휴일이면 다음 근무일) + 간격 — 같은 날 시작해도 된다
    //  FF(같이 끝): 후행 종료가 선행 종료일 + 간격(EarliestEnd) 이후가 되는 시작일 — 후행의 근무일 기간만큼 거꾸로 센다
    public static DateOnly EarliestStart(WorkLink link, DateOnly predecessorStart, DateOnly predecessorEnd, int successorWorkDays, WorkCalendar cal)
    {
        var lag = Math.Max(0, link.LagDays);
        return link.Type switch
        {
            LinkType.SS => cal.AddWorkingDays(cal.NextWorkingDay(predecessorStart), lag),
            LinkType.FF => cal.AddWorkingDays(EarliestEnd(link, predecessorEnd, cal), -(Math.Max(1, successorWorkDays) - 1)),
            _ => cal.EarliestStartAfter(predecessorEnd, lag),
        };
    }

    public static DateOnly EarliestStart(WorkLink link, WorkItem predecessor, WorkItem successor, WorkCalendar cal) =>
        EarliestStart(link, predecessor.PlanStart, predecessor.PlanEnd, WorkDays(successor, cal), cal);

    // FF 연결에서 후행이 끝날 수 있는 가장 이른 날 (선행 종료일 + 간격 근무일).
    // 휴일 종료는 FS 처럼 직전 근무일에 끝난 것으로 본다 — 토요일 종료로 적힌 선행은 금요일에 일을 마치므로, 간격 0 이면 후행도
    // 금요일(또는 같은 토요일)에 끝나도 된다. 다음 근무일로 올리면 간격 0 이 간격 1 처럼 한 근무일 더 민다
    public static DateOnly EarliestEnd(WorkLink link, DateOnly predecessorEnd, WorkCalendar cal) =>
        cal.AddWorkingDays(cal.EarliestStartAfter(predecessorEnd, link.LagDays), -1);

    // start 로 잡은 일정이 가장 이른 날보다 먼저 시작하는가. 휴일 시작은 다음 근무일에 시작하는 것으로 본다
    // (Place 와 같은 기준 — 토요일 시작으로 적힌 작업은 월요일부터 일하므로 금요일에 끝난 선행과 어긋나지 않는다)
    public static bool StartsBefore(DateOnly start, DateOnly earliest, WorkCalendar cal) => cal.NextWorkingDay(start) < earliest;

    // 후행 작업이 선행 작업이 허용하는 날보다 먼저 시작하는가
    public static bool Violates(WorkLink link, WorkItem predecessor, WorkItem successor, WorkCalendar cal) =>
        StartsBefore(successor.PlanStart, EarliestStart(link, predecessor, successor, cal), cal);

    public static bool Violates(WorkItem predecessor, WorkItem successor, int lagDays, WorkCalendar cal) =>
        Violates(new WorkLink { LagDays = lagDays }, predecessor, successor, cal);

    // 화면·이력에 쓰는 유형 이름
    public static string LinkLabel(LinkType type) => type switch
    {
        LinkType.SS => "같이 시작",
        LinkType.FF => "같이 끝",
        _ => "끝나면 시작",
    };

    // 연결 하나의 설명: "끝나면 시작" / "같이 시작 +2근무일"
    public static string LinkText(LinkType type, int lagDays) => LinkLabel(type) + (lagDays > 0 ? $" +{lagDays}근무일" : "");

    // 입력 문자열('fs'·'SS'·빈 값) → 유형. 빈 값은 FS
    public static LinkType ParseLinkType(string? value, string field = "predecessors") =>
        string.IsNullOrWhiteSpace(value) ? LinkType.FS
        : Enum.TryParse<LinkType>(value.Trim(), ignoreCase: true, out var t) && Enum.IsDefined(t) ? t
        : throw new RuleException("의존 유형을 다시 선택해 주세요.", field);

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
                    var gap = cal.Distance(EarliestStart(l, x.Work, next.Work, cal), cal.NextWorkingDay(next.Work.PlanStart));
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
        var g = DepGraph.Of(p);

        // 영향받는 부분 그래프: 시작 작업과 그 후행 전부 (원래 순서를 지켜 결과를 안정적으로)
        var nodes = new HashSet<WorkItem>();
        var order = new List<WorkItem>();
        var stack = new Stack<WorkItem>();
        foreach (var w in (from ?? g.Works).Where(g.StageOf.ContainsKey).Reverse()) stack.Push(w);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!nodes.Add(cur)) continue;
            order.Add(cur);
            if (g.Succs.TryGetValue(cur, out var next))
                for (var i = next.Count - 1; i >= 0; i--) stack.Push(next[i]);
        }

        var dates = new Dictionary<WorkItem, (DateOnly Start, DateOnly End)>();
        var moves = new List<ScheduleMove>();

        foreach (var w in g.TopoOrder(order, nodes))
        {
            if (!IsMovable(w, g.StageOf[w]) || !g.Preds.TryGetValue(w, out var incoming)) continue;
            DateOnly? earliest = null;
            WorkItem? cause = null;
            var days = WorkDays(w, cal);
            foreach (var (link, pred) in incoming)
            {
                var (ps, pe) = dates.TryGetValue(pred, out var moved) ? moved : (pred.PlanStart, pred.PlanEnd);
                var e = EarliestStart(link, ps, pe, days, cal);
                if (earliest == null || e > earliest.Value) { earliest = e; cause = pred; }
            }
            if (earliest != null && StartsBefore(w.PlanStart, earliest.Value, cal))
            {
                var (ns, ne) = Place(earliest.Value, days, cal);
                dates[w] = (ns, ne);
                moves.Add(new ScheduleMove(w, w.PlanStart, w.PlanEnd, ns, ne, cause!));
            }
        }
        return moves;
    }

    // 과제의 작업 의존 그래프. 밀기(Reschedule)와 전진 계산(ForwardPass)이 같은 그래프·같은 위상 정렬을 쓴다.
    // 연결은 탐색 속성(Predecessor/Successor)을 먼저 보고, 없으면 Id 로 찾는다 — 아직 저장 전(Id 0)인 작업·연결도 다룬다
    private sealed class DepGraph
    {
        public readonly Dictionary<WorkItem, Stage> StageOf = new();
        public readonly List<WorkItem> Works = new();
        public readonly Dictionary<WorkItem, List<(WorkLink Link, WorkItem Work)>> Preds = new();
        public readonly Dictionary<WorkItem, List<WorkItem>> Succs = new();

        public static DepGraph Of(Project p)
        {
            var g = new DepGraph();
            var byId = new Dictionary<int, WorkItem>();
            foreach (var s in p.Stages)
                foreach (var w in s.Works)
                {
                    if (g.StageOf.TryAdd(w, s)) g.Works.Add(w);
                    if (w.Id != 0) byId.TryAdd(w.Id, w);
                }

            WorkItem? Resolve(WorkItem? nav, int id) =>
                nav != null && g.StageOf.ContainsKey(nav) ? nav : id != 0 && byId.TryGetValue(id, out var w) ? w : null;

            foreach (var l in p.Links)
            {
                var a = Resolve(l.Predecessor, l.PredecessorId);
                var b = Resolve(l.Successor, l.SuccessorId);
                if (a == null || b == null || ReferenceEquals(a, b)) continue;
                if (!g.Preds.TryGetValue(b, out var pl)) g.Preds[b] = pl = new();
                pl.Add((l, a));
                if (!g.Succs.TryGetValue(a, out var sl)) g.Succs[a] = sl = new();
                sl.Add(b);
            }
            return g;
        }

        // nodes 안에서의 위상 정렬(Kahn). 선행이 여럿 모이는 작업도 모든 선행이 나온 뒤에 한 번만 나온다.
        // 순환에 걸린 작업(입력 단계에서 막지만)은 결과에 넣지 않는다
        public List<WorkItem> TopoOrder(IReadOnlyList<WorkItem> order, HashSet<WorkItem> nodes)
        {
            var indegree = order.ToDictionary(w => w, w => Preds.TryGetValue(w, out var pl) ? pl.Count(x => nodes.Contains(x.Work)) : 0);
            var queue = new Queue<WorkItem>(order.Where(w => indegree[w] == 0));
            var result = new List<WorkItem>();
            while (queue.Count > 0)
            {
                var w = queue.Dequeue();
                result.Add(w);
                if (Succs.TryGetValue(w, out var next))
                    foreach (var n in next)
                        if (nodes.Contains(n) && --indegree[n] == 0) queue.Enqueue(n);
            }
            return result;
        }
    }

    // ── 오늘 기준 예측 일정(전진 계산) ──────────────────────────────────

    // 작업 하나의 예상 일정. StartLate: 목표 시작일이 지났는데 아직 시작하지 않음. Cause: 예상을 미룬 선행 작업(오늘 때문이면 null)
    public record Expected(DateOnly Start, DateOnly End, bool StartLate, WorkItem? Cause);

    // 오늘과 실적을 반영해 의존 관계를 따라 앞으로 전파한 예상 일정. 저장값은 바꾸지 않는다(규칙 1·11).
    // 위상 정렬 한 번(O(V+E)), 작업마다:
    //  - 완료: 실적 그대로. 단계 완료로 끝난 작업은 단계 완료일에 끝난 것으로 본다(규칙 3)
    //  - 진행 중: 시작은 실적, 종료는 max(목표 종료, 오늘) — FF 선행이 있으면 그 선행의 예상 종료 + 간격 이후
    //  - 미착수: 시작은 max(목표 시작, 오늘, 선행들의 예상 일정이 허용하는 가장 이른 날(EarliestStart)), 근무일 기간은 그대로.
    //    목표 시작일이 그보다 늦거나 같으면 목표 일정 그대로 둔다(당기지 않는다)
    // 순환에 걸린 작업(입력 단계에서 막지만)은 선행 없이 자기 일정만으로 본다
    public static Dictionary<WorkItem, Expected> ForwardPass(Project p, WorkCalendar cal, DateOnly today)
    {
        var g = DepGraph.Of(p);
        var all = new HashSet<WorkItem>(g.Works);
        var order = g.TopoOrder(g.Works, all);
        var done = new HashSet<WorkItem>(order);
        order.AddRange(g.Works.Where(w => !done.Contains(w)));

        var result = new Dictionary<WorkItem, Expected>();
        foreach (var w in order)
        {
            var stage = g.StageOf[w];
            var incoming = g.Preds.TryGetValue(w, out var pl)
                ? pl.Where(x => result.ContainsKey(x.Work)).Select(x => (x.Link, x.Work, result[x.Work])).ToList()
                : new List<(WorkLink Link, WorkItem Work, Expected Pred)>();
            result[w] = ExpectedOf(w, stage, incoming, cal, today);
        }
        return result;
    }

    private static Expected ExpectedOf(WorkItem w, Stage stage, List<(WorkLink Link, WorkItem Work, Expected Pred)> incoming, WorkCalendar cal, DateOnly today)
    {
        var closedAt = w.ActualEnd ?? stage.CompletedDate;
        if (closedAt is DateOnly end)
        {
            var start = w.ActualStart ?? w.PlanStart;
            return new Expected(start > end ? end : start, end, false, null);
        }

        if (w.ActualStart is DateOnly started)
        {
            var finish = w.PlanEnd < today ? today : w.PlanEnd;
            if (finish < started) finish = started;
            WorkItem? cause = null;
            foreach (var (link, work, pred) in incoming)
            {
                if (link.Type != LinkType.FF) continue;
                var e = EarliestEnd(link, pred.End, cal);
                if (e > finish) { finish = e; cause = work; }
            }
            return new Expected(started, finish, false, cause);
        }

        var days = WorkDays(w, cal);
        var earliest = today;
        WorkItem? why = null;
        foreach (var (link, work, pred) in incoming)
        {
            var e = EarliestStart(link, pred.Start, pred.End, days, cal);
            if (e > earliest) { earliest = e; why = work; }
        }
        // 휴일 시작은 다음 근무일에 시작하는 것으로 본다(StartsBefore — 이동 판정과 같은 기준). 토요일 시작으로 적힌 작업이 월요일에 시작하면 늦지 않다
        var startLate = StartsBefore(w.PlanStart, today, cal);
        if (!StartsBefore(w.PlanStart, earliest, cal)) return new Expected(w.PlanStart, w.PlanEnd, startLate, null);
        var (ns, ne) = Place(earliest, days, cal);
        return new Expected(ns, ne, startLate, why);
    }

    // 재계획으로 옮길 작업: 밀기 대상(미착수·단계 미완료)인데 예상 일정이 목표 일정과 다른 것. 예상은 목표보다 앞서지 않는다
    public record ReplanMove(WorkItem Work, DateOnly OldStart, DateOnly OldEnd, DateOnly NewStart, DateOnly NewEnd, bool StartLate, WorkItem? Cause);

    public static List<ReplanMove> ReplanMoves(Project p, WorkCalendar cal, DateOnly today, IReadOnlyDictionary<WorkItem, Expected>? expected = null)
    {
        expected ??= ForwardPass(p, cal, today);
        var moves = new List<ReplanMove>();
        foreach (var s in Ordered(p))
            foreach (var w in s.Works.OrderBy(w => w.PlanStart).ThenBy(w => w.Id))
            {
                if (!IsMovable(w, s) || !expected.TryGetValue(w, out var e)) continue;
                if (e.Start == w.PlanStart && e.End == w.PlanEnd) continue;
                moves.Add(new ReplanMove(w, w.PlanStart, w.PlanEnd, e.Start, e.End, e.StartLate, e.Cause));
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

    // ── 화면·내보내기가 함께 쓰는 표시 규칙 ─────────────────────────────
    // 대시보드·목록·내 할 일·상세·Excel 이 같은 값을 보이도록 여기 한 곳에서 정한다(화면은 서버가 채운 값만 그린다)

    // '임박': 목표일까지 남은 날이 이 일수 이하(오늘 포함)
    public const int SoonDays = 7;

    public static int DaysLeft(DateOnly date, DateOnly today) => date.DayNumber - today.DayNumber;

    public static bool IsSoon(int daysLeft) => daysLeft >= 0 && daysLeft <= SoonDays;

    // 과제를 끝낸 날: 모든 단계를 완료했으면 가장 늦은 단계 완료일 (대시보드 '올해 완료'와 목록의 완료 연도 조건이 같은 판정을 쓴다)
    public static DateOnly? CompletedOn(Project p) =>
        AllStagesDone(p) ? p.Stages.Max(s => s.CompletedDate!.Value) : null;

    // 미완료 단계 중 목표일이 오늘부터 days 일 안(오늘 포함)인 것이 있는가 (대시보드 'n일 내 목표'·다가오는 일정과 목록의 조건)
    public static bool HasStageDueWithin(Project p, DateOnly today, int days) =>
        p.Stages.Any(s => s.CompletedDate == null && s.TargetDate != null && DaysLeft(s.TargetDate.Value, today) is var d && d >= 0 && d <= days);

    // 미완료 단계의 목표일이 지남
    public static bool IsLate(Stage s, DateOnly today) => s.CompletedDate == null && s.TargetDate != null && s.TargetDate.Value < today;

    // 단계를 목표일보다 늦게 완료함 (간트의 '목표 초과 완료' 막대·Excel 간트 색)
    public static bool IsDoneLate(Stage s) => s.CompletedDate != null && s.TargetDate != null && s.CompletedDate.Value > s.TargetDate.Value;

    // 작업 목표 종료의 기준선 대비 (밀리면 양수). 기준선이 없으면 null
    public static int? SlipDays(WorkItem w) => w.BaselineEnd == null ? null : w.PlanEnd.DayNumber - w.BaselineEnd.Value.DayNumber;

    // 지연 작업이 목표 종료를 넘긴 달력 일수 (지연이 아니면 0)
    public static int LateDays(WorkItem w, Stage? stage, DateOnly today)
    {
        var late = stage == null ? IsLate(w, today) : IsLate(w, stage, today);
        return late ? today.DayNumber - w.PlanEnd.DayNumber : 0;
    }

    // ── 접속자의 '오늘' ─────────────────────────────────────────────────
    // 지연 판정의 '오늘'은 한국 날짜(규칙 8)지만, 실적 날짜의 기본값('오늘 시작'·진행률만 올린 작업의 실적 시작)은
    // 접속한 PC 의 날짜를 쓴다 — 해외 법인에서 저녁에 누르면 한국은 이미 다음 날이라 하루 뒤 날짜가 기록되기 때문이다.
    // PC 시계가 틀린 경우를 막으려고 한국 날짜 ±ClientDayWindow 일 안일 때만 받는다(시간대 차이는 하루를 넘지 않는다)
    public const int ClientDayWindow = 1;

    public static DateOnly UserToday(DateOnly? clientToday, DateOnly today) =>
        clientToday is { } c && Math.Abs(c.DayNumber - today.DayNumber) <= ClientDayWindow ? c : today;

    // ── 삭제 권한 ───────────────────────────────────────────────────────
    // 서비스의 검사와 화면의 단추 표시가 같은 판정을 쓰도록 둔다 (권한 없는 사람에게 누르면 거절당할 단추를 보이지 않게)
    public static bool CanDeleteProject(Project p, int? userId, bool isAdmin) =>
        isAdmin || (userId != null && (p.OwnerId == userId || p.CreatedById == userId));

    public static bool CanDeleteAttachment(Attachment a, int? projectOwnerId, int? userId, bool isAdmin) =>
        isAdmin || (userId != null && (a.UploadedById == userId || projectOwnerId == userId));

    // 기준선 다시 확정: 기준선 대비 밀린 기록을 0 으로 되돌리므로 과제 담당자·관리자만 (처음 확정은 누구나)
    public static bool CanRebaseline(Project p, int? userId, bool isAdmin) => isAdmin || (userId != null && p.OwnerId == userId);

    // ── 단계 일정 계획: 목표일 초안·근무일 간격 ─────────────────────────
    // 기본 기간(StageTemplate.DefaultWorkDays)·초안·간격·리드타임 힌트가 모두 같은 거리를 쓴다:
    // "앞 단계 목표일(첫 단계는 착수일)부터 이 단계 목표일까지의 근무일 거리(WorkCalendar.Distance)".
    // 그래서 초안을 그대로 두면 단계 일정 표의 간격이 기본 기간과 같고, 과거 리드타임 평균을 기본 기간에 그대로 옮겨 쓸 수 있다

    public const int MaxDefaultWorkDays = 999;

    public static void ValidateDefaultWorkDays(int? days)
    {
        if (days is int d && (d < 0 || d > MaxDefaultWorkDays))
            throw new RuleException($"기본 기간은 0~{MaxDefaultWorkDays} 근무일로 입력해 주세요. 정하지 않으려면 비워 둡니다.", "defaultWorkDays");
    }

    // 단계 목표일 초안 (단계 순서대로). 앞 단계 목표일(첫 단계는 착수일)에서 근무일 기본 기간만큼 뒤로 잡는다.
    // 기본 기간이 없는 단계는 비우고, 그다음 단계는 앞에서 정해진 마지막 목표일(없으면 착수일)부터 센다 — 간트의 목표 막대와 같은 기준.
    // fixedAt(순서 위치 → 날짜)에 있는 단계는 사람이 정한 값(빈 값 포함)을 그대로 두고 그 뒤 단계만 거기서 이어 센다.
    // 초안은 제안일 뿐이다 — 저장은 사람이 확인하고 고친 뒤에 한다(규칙 1)
    public static List<DateOnly?> DraftTargets(DateOnly start, IReadOnlyList<int?> workDays, WorkCalendar cal,
                                               IReadOnlyDictionary<int, DateOnly?>? fixedAt = null)
    {
        var result = new List<DateOnly?>(workDays.Count);
        var anchor = start;
        for (var i = 0; i < workDays.Count; i++)
        {
            DateOnly? target = fixedAt != null && fixedAt.TryGetValue(i, out var kept) ? kept
                : workDays[i] is int days ? cal.AddWorkingDays(anchor, Math.Clamp(days, 0, MaxDefaultWorkDays))
                : null;
            result.Add(target);
            if (target != null) anchor = target.Value;
        }
        return result;
    }

    public static List<DateOnly?> DraftTargets(DateOnly start, IEnumerable<StageTemplate> templates, WorkCalendar cal) =>
        DraftTargets(start, templates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => t.DefaultWorkDays).ToList(), cal);

    // 단계 목표일 사이의 근무일 간격 (단계 순서대로). 앞에서 정해진 마지막 목표일(첫 단계는 착수일)부터 이 단계 목표일까지.
    // 목표일이 없으면 null, 앞 단계보다 이르면 음수(순서가 뒤바뀐 일정 — 화면이 경고한다)
    public static List<int?> TargetGaps(DateOnly start, IReadOnlyList<DateOnly?> targets, WorkCalendar cal)
    {
        var result = new List<int?>(targets.Count);
        var anchor = start;
        foreach (var t in targets)
        {
            result.Add(t == null ? null : cal.Distance(anchor, t.Value));
            if (t != null) anchor = t.Value;
        }
        return result;
    }

    // ── 과제 간 단계 의존 (StageLink) ─────────────────────────────────
    // 자동으로 밀지 않고 위반만 계산한다 — 다른 과제 담당자의 일정을 몰래 바꾸지 않기 위해서다(규칙 11)

    // 후행 단계 목표일이 지켜야 할 가장 이른 날: 선행 단계 완료일(없으면 목표일) 다음 근무일 + 간격. 선행 날짜가 없으면 null
    public static DateOnly? StageLinkEarliest(StageLink link, Stage predecessor, WorkCalendar cal)
    {
        var basis = predecessor.CompletedDate ?? predecessor.TargetDate;
        return basis == null ? null : cal.EarliestStartAfter(basis.Value, link.LagDays);
    }

    // 후행 단계(미완료)의 목표일이 선행 단계가 허용하는 날보다 이른가. 어느 쪽이든 날짜가 없으면 판정하지 않는다
    public static bool StageLinkViolated(StageLink link, Stage predecessor, Stage successor, WorkCalendar cal) =>
        successor.CompletedDate == null && successor.TargetDate != null
        && StageLinkEarliest(link, predecessor, cal) is { } earliest && successor.TargetDate.Value < earliest;

    // ── 보고 기준일(asOf) 판정 — 지난 주 보고 ─────────────────────────
    // 지난 주 보고를 다시 열면 그 주 마지막 날(asOf) 기준으로 지연을 센다. asOf 뒤에 기록된 실적(단계 완료일·작업 실적 종료)은
    // 그때 아직 없던 것으로 본다. 목표일은 지금 값이다 — 목표일 변경 이력을 거슬러 계산하지 않는다(근본 해결은 일별 스냅샷).
    // asOf 가 오늘 이후면 지금 판정(DelayDays·IsLate)과 같다

    // asOf 에 이미 끝나 있던 실적인가 (오늘 기준이면 기록된 실적은 모두 끝난 것)
    public static bool DoneBy(DateOnly? date, DateOnly asOf, DateOnly today) => date != null && (asOf >= today || date.Value <= asOf);

    // asOf 에 진행 중이던 과제: 지금 진행 중이거나, 지금은 완료지만 asOf 뒤에 마지막 단계를 끝낸 과제. 보류·중단은 언제 바뀌었는지 몰라 지금 상태로 본다
    public static bool ActiveAsOf(Project p, DateOnly asOf, DateOnly today) =>
        p.State == ProjectState.Active
        || (p.State == ProjectState.Done && p.Stages.Any(s => !DoneBy(s.CompletedDate, asOf, today)));

    // asOf 기준 단계 지연 일수 (지연이 아니면 0)
    public static int StageLateDaysAsOf(Stage s, DateOnly asOf, DateOnly today) =>
        !DoneBy(s.CompletedDate, asOf, today) && s.TargetDate != null && s.TargetDate.Value < asOf ? asOf.DayNumber - s.TargetDate.Value.DayNumber : 0;

    // asOf 기준 작업 지연 일수. 그때 단계가 이미 완료돼 있었으면 단계와 함께 끝난 것으로 본다(IsOpen 과 같은 기준)
    public static int WorkLateDaysAsOf(WorkItem w, Stage s, DateOnly asOf, DateOnly today) =>
        !DoneBy(w.ActualEnd, asOf, today) && !DoneBy(s.CompletedDate, asOf, today) && w.PlanEnd < asOf ? asOf.DayNumber - w.PlanEnd.DayNumber : 0;

    // asOf 기준 과제 지연 일수 (DelayDays 와 같은 규칙 — 가장 오래 밀린 미완료 단계)
    public static int DelayDaysAsOf(Project p, DateOnly asOf, DateOnly today)
    {
        if (!ActiveAsOf(p, asOf, today)) return 0;
        var max = 0;
        foreach (var s in p.Stages) max = Math.Max(max, StageLateDaysAsOf(s, asOf, today));
        return max;
    }

    // ── 공휴일 변경 영향 ────────────────────────────────────────────────
    // 공휴일을 더하거나 빼면 근무일 기간·지연 일수가 바뀌고, 선행 종료 다음 날을 공휴일로 넣으면 후행이 곧바로 선행 관계를 어긴다.
    // 관리 화면이 저장 응답으로 알리도록 과제 하나의 영향을 센다(자동으로 밀지는 않는다 — 규칙 11).
    // Touched: 바뀐 날이 미완료 단계 목표일이거나 열린 작업의 목표 기간 안에 있다. NewViolations: before 달력에서는 지키던 선행 관계를
    // after 달력에서 어기게 되는 밀기 대상 작업 수(작업마다 한 번)
    public static (bool Touched, int NewViolations) HolidayImpact(Project p, IReadOnlyCollection<DateOnly> changed, WorkCalendar before, WorkCalendar after)
    {
        var touched = p.Stages.Any(s => s.CompletedDate == null
            && ((s.TargetDate is { } t && changed.Contains(t))
                || s.Works.Any(w => IsOpen(w, s) && changed.Any(d => d >= w.PlanStart && d <= w.PlanEnd))));
        var byId = new Dictionary<int, (WorkItem Work, Stage Stage)>();
        foreach (var s in p.Stages)
            foreach (var w in s.Works) byId[w.Id] = (w, s);
        var violated = new HashSet<int>();
        foreach (var l in p.Links)
        {
            if (!byId.TryGetValue(l.PredecessorId, out var pred) || !byId.TryGetValue(l.SuccessorId, out var succ)) continue;
            if (!IsMovable(succ.Work, succ.Stage)) continue;
            if (Violates(l, pred.Work, succ.Work, after) && !Violates(l, pred.Work, succ.Work, before)) violated.Add(succ.Work.Id);
        }
        return (touched, violated.Count);
    }

    // ── 일정 변경 횟수 ──────────────────────────────────────────────────
    // 일정 변경은 '결정' 단위로 센다. 연쇄 이동·선행 연동은 한 번의 결정으로 여러 행(같은 BatchId)을 남기므로
    // 행 수로 세면 부풀려진다 — 묶음 없는 행은 하나씩, 묶음은 하나로 센다 (주간 보고·상세·Excel·PDF 공용)
    public static int DecisionCount(IEnumerable<Guid?> batchIds)
    {
        var count = 0;
        var seen = new HashSet<Guid>();
        foreach (var b in batchIds)
            if (b == null || seen.Add(b.Value)) count++;
        return count;
    }
}
