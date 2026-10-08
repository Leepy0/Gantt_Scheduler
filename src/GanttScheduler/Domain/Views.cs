namespace GanttScheduler.Domain;

// 화면(JSON)으로 내보내는 읽기 전용 모델
public record WorkView(
    int Id, int StageId, string Title, string? Owner, string? CoOwner, int Progress,
    DateOnly PlanStart, DateOnly PlanEnd, DateOnly? ActualStart, DateOnly? ActualEnd,
    string Status, bool Late, bool DoneLate, string? Note);

public record StageView(
    int Id, string Name, string Kind, int Sort,
    DateOnly? TargetDate, DateOnly? CompletedDate,
    string Status, int Progress, int LateDays,
    DateOnly? PlanFrom, DateOnly? ActualFrom, DateOnly? ActualTo,
    DateOnly? Forecast, int ForecastOver, DateOnly? InboundHint,
    List<WorkView> Works);

public record ActivityView(
    int Id, string Type, string Text, string? Reason, string User, DateTime At, int? StageId, int? WorkId);

public record ItemView(int TypeId, string Name, int Quantity, string? Note);

public record ProjectView(
    int Id, string Title, string State,
    int CategoryId, string Category, string CategoryKind,
    int CorporationId, string Corporation, int LocationId, string LocationPath,
    int OwnerId, string Owner, DateOnly StartDate, DateOnly? EndTarget,
    int Progress, int DelayDays, string? DelayedStage,
    string? CurrentStage, DateOnly? CurrentTarget,
    int WorkCount, int OceanLeadDays,
    List<StageView> Stages,
    // 아래는 상세 화면에서만 채운다
    string? Vendor, string? Description,
    List<ItemView>? Equipments, List<ItemView>? Containers,
    List<ActivityView>? Activities,
    string? CreatedBy, DateTime? CreatedAt, DateTime? UpdatedAt);

// 위치 트리 조회용 색인
public class LocationIndex
{
    private readonly Dictionary<int, Location> _byId;
    public LocationIndex(IEnumerable<Location> all) => _byId = all.ToDictionary(l => l.Id);

    public Location? Get(int id) => _byId.TryGetValue(id, out var l) ? l : null;

    // 루트(법인)부터 해당 노드까지
    public List<Location> Chain(int id)
    {
        var chain = new List<Location>();
        var guard = 0;
        var cur = Get(id);
        while (cur != null && guard++ < 10)
        {
            chain.Insert(0, cur);
            cur = cur.ParentId == null ? null : Get(cur.ParentId.Value);
        }
        return chain;
    }

    // 법인을 뺀 하위 경로 표시 ("TV · 3라인 · 2F")
    public string PathBelowCorporation(int id) =>
        string.Join(" · ", Chain(id).Where(l => l.Level > 1).Select(l => l.Name));

    public Location? CorporationOf(int id) => Chain(id).FirstOrDefault();
}

public static class Views
{
    public static string Key(StageStatus s) => s.ToString().ToLowerInvariant();
    public static string Key(WorkStatus s) => s.ToString().ToLowerInvariant();
    public static string Key(ProjectState s) => s.ToString().ToLowerInvariant();
    public static string Key(StageKind s) => s.ToString().ToLowerInvariant();
    public static string Key(CategoryKind s) => s.ToString().ToLowerInvariant();
    public static string Key(ActivityType s) => char.ToLowerInvariant(s.ToString()[0]) + s.ToString()[1..];

    public static string Label(ProjectState s) => s switch
    {
        ProjectState.Active => "진행",
        ProjectState.Hold => "보류",
        ProjectState.Dropped => "중단",
        ProjectState.Done => "완료",
        _ => s.ToString(),
    };

    public static WorkView Work(WorkItem w, DateOnly today) => new(
        w.Id, w.StageId, w.Title, w.Owner?.Name, w.CoOwner?.Name, w.Progress,
        w.PlanStart, w.PlanEnd, w.ActualStart, w.ActualEnd,
        Key(Rules.StatusOf(w)), Rules.IsLate(w, today), Rules.IsDoneLate(w), w.Note);

    public static List<StageView> Stages(Project p, DateOnly today, int oceanLeadDays, bool withWorks)
    {
        var list = new List<StageView>();
        DateOnly? prevTarget = p.StartDate;       // 목표 막대의 시작: 직전 단계 목표일(없으면 착수일)
        DateOnly? prevCompleted = p.StartDate;    // 실행 막대의 시작: 직전 단계 완료일(첫 단계는 착수일)
        var inbound = Rules.InboundRecommendation(p, oceanLeadDays);

        foreach (var s in Rules.Ordered(p))
        {
            var status = Rules.StatusOf(s, p, today);

            DateOnly? planFrom = s.TargetDate == null ? null : prevTarget;
            if (planFrom != null && planFrom.Value > s.TargetDate!.Value) planFrom = s.TargetDate;

            // 실행 막대: 직전 단계가 끝났으면 그 완료일부터, 본 단계 완료일(진행 중이면 오늘)까지
            DateOnly? actualFrom = prevCompleted;
            DateOnly? actualTo = s.CompletedDate ?? (actualFrom != null && p.State == ProjectState.Active ? today : null);
            if (s.CompletedDate != null && actualFrom == null) actualFrom = planFrom ?? s.CompletedDate;
            if (actualTo == null) actualFrom = null;
            if (actualFrom != null && actualTo != null && actualFrom.Value > actualTo.Value) actualFrom = actualTo;

            var lateDays = status == StageStatus.Late ? today.DayNumber - s.TargetDate!.Value.DayNumber : 0;

            var works = withWorks
                ? s.Works.OrderBy(w => w.PlanStart).ThenBy(w => w.Id).Select(w => Work(w, today)).ToList()
                : new List<WorkView>();

            list.Add(new StageView(
                s.Id, s.Name, Key(s.Kind), s.SortOrder,
                s.TargetDate, s.CompletedDate,
                Key(status), Rules.StageProgress(s), lateDays,
                planFrom, actualFrom, actualTo,
                Rules.Forecast(s, today), Rules.ForecastOverDays(s, today),
                s.Kind == StageKind.Inbound && s.CompletedDate == null ? inbound : null,
                works));

            if (s.TargetDate != null) prevTarget = s.TargetDate;
            prevCompleted = s.CompletedDate;   // 미완료면 null → 다음 단계 실행 막대 없음
        }
        return list;
    }

    public static ProjectView Project(Project p, DateOnly today, LocationIndex locations, bool detail, bool withWorks = true)
    {
        var corp = locations.Get(p.CorporationId);
        var lead = corp?.OceanLeadDays ?? 0;
        var current = Rules.CurrentStage(p);
        var delayed = Rules.MostDelayedStage(p, today);
        var delayDays = Rules.DelayDays(p, today);

        return new ProjectView(
            p.Id, p.Title, Key(p.State),
            p.CategoryId, p.Category?.Name ?? "", Key(p.Category?.Kind ?? CategoryKind.General),
            p.CorporationId, corp?.Name ?? "", p.LocationId, locations.PathBelowCorporation(p.LocationId),
            p.OwnerId, p.Owner?.Name ?? "", p.StartDate, Rules.EndTarget(p),
            Rules.ProjectProgress(p), delayDays, delayDays > 0 ? delayed?.Name : null,
            current?.Name, current?.TargetDate,
            p.Stages.Sum(s => s.Works.Count), lead,
            Stages(p, today, lead, withWorks),
            detail ? p.Vendor : null,
            detail ? p.Description : null,
            detail ? p.Equipments.OrderBy(e => e.Id).Select(e => new ItemView(e.EquipmentTypeId, EquipmentName(e.EquipmentType), e.Quantity, e.Note)).ToList() : null,
            detail ? p.Containers.OrderBy(e => e.Id).Select(e => new ItemView(e.ContainerTypeId, e.ContainerType?.Name ?? "", e.Quantity, e.Note)).ToList() : null,
            detail ? p.Activities.OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
                        .Select(a => new ActivityView(a.Id, Key(a.Type), a.Text, a.Reason, a.User?.Name ?? "", a.At, a.StageId, a.WorkItemId)).ToList() : null,
            detail ? p.CreatedBy?.Name : null,
            detail ? p.CreatedAt : null,
            detail ? p.UpdatedAt : null);
    }

    // "AGV · Belt" 처럼 대분류와 함께 표시
    public static string EquipmentName(EquipmentType? t)
    {
        if (t == null) return "";
        return t.Parent == null ? t.Name : $"{t.Parent.Name} · {t.Name}";
    }
}
