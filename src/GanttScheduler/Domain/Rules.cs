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

    public static void ValidateProgress(int progress)
    {
        if (progress < 0 || progress > 100 || progress % ProgressStep != 0)
            throw new RuleException("진행률은 0~100 사이 5 단위로 입력합니다.", "progress");
    }
}
