namespace GanttScheduler.Domain;

// 화면(JSON)으로 내보내는 읽기 전용 모델
public record WorkView
{
    public int Id { get; init; }
    public int StageId { get; init; }
    public int Version { get; init; }
    public string Title { get; init; } = "";
    public string? Owner { get; init; }
    public string? CoOwner { get; init; }
    public int Progress { get; init; }
    public DateOnly PlanStart { get; init; }
    public DateOnly PlanEnd { get; init; }
    public int PlanWorkDays { get; init; }              // 목표 기간의 근무일 수
    public DateOnly? ActualStart { get; init; }
    public DateOnly? ActualEnd { get; init; }
    public DateOnly? BaselineStart { get; init; }
    public DateOnly? BaselineEnd { get; init; }
    public string Status { get; init; } = "";
    public bool Late { get; init; }
    public bool DoneLate { get; init; }
    public string? Note { get; init; }
    public List<int> Predecessors { get; init; } = new();
    public int? Float { get; init; }                    // 여유 근무일 (제약이 없으면 null)
    public bool Critical { get; init; }                 // 미완료이고 여유가 없음
    public bool DepViolation { get; init; }             // 선행 작업보다 먼저 시작하게 잡혀 있음
}

public record CheckView(int Id, string Text, bool Done, string? DoneBy, DateTime? DoneAt);

public record StageView
{
    public int Id { get; init; }
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public int Sort { get; init; }
    public DateOnly? TargetDate { get; init; }
    public DateOnly? CompletedDate { get; init; }
    public DateOnly? BaselineDate { get; init; }
    public int? SlipDays { get; init; }                 // 기준선 대비 (밀리면 양수)
    public string Status { get; init; } = "";
    public int Progress { get; init; }
    public int LateDays { get; init; }
    public DateOnly? PlanFrom { get; init; }
    public DateOnly? ActualFrom { get; init; }
    public DateOnly? ActualTo { get; init; }
    public DateOnly? Forecast { get; init; }
    public int ForecastOver { get; init; }
    public DateOnly? InboundHint { get; init; }
    public List<CheckView> Checks { get; init; } = new();
    public int CheckDone { get; init; }
    public int CheckTotal { get; init; }
    public List<WorkView> Works { get; init; } = new();
    public int OpenWorks { get; init; }                 // 실적 종료가 없는 작업 수 (완료하면서 함께 닫을 수 있는 작업)
    public int LaterWorks { get; init; }                // 연쇄 이동 때 함께 옮길 수 있는 이후 단계의 미착수 작업 수
}

public record ActivityView(
    int Id, string Type, string Text, string? Reason, string User, DateTime At, int? StageId, int? WorkId);

public record ItemView(int TypeId, string Name, int Quantity, string? Note);

public record LinkView(int Id, int From, int To, int Lag);

public record AttachmentView(
    int Id, string FileName, string ContentType, long Size, string UploadedBy, DateTime UploadedAt, int? StageId, bool Previewable);

public record ProjectView
{
    public int Id { get; init; }
    public int Version { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public int CategoryId { get; init; }
    public string Category { get; init; } = "";
    public string CategoryKind { get; init; } = "";
    public int CorporationId { get; init; }
    public string Corporation { get; init; } = "";
    public int LocationId { get; init; }
    public string LocationPath { get; init; } = "";
    public int OwnerId { get; init; }
    public string Owner { get; init; } = "";
    public string? OwnerOrg { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndTarget { get; init; }
    public int Progress { get; init; }
    public int DelayDays { get; init; }
    public string? DelayedStage { get; init; }
    public string? CurrentStage { get; init; }
    public DateOnly? CurrentTarget { get; init; }
    public int WorkCount { get; init; }
    public int OceanLeadDays { get; init; }
    public DateTime? BaselineAt { get; init; }
    public DateOnly? BaselineEnd { get; init; }
    public int? SlipDays { get; init; }                 // 목표 종료가 기준선보다 밀린 일수
    public int CriticalCount { get; init; }
    public List<StageView> Stages { get; init; } = new();
    public List<LinkView> Links { get; init; } = new();

    // 아래는 상세 화면에서만 채운다
    public string? Vendor { get; init; }
    public string? Description { get; init; }
    public List<ItemView>? Equipments { get; init; }
    public List<ItemView>? Containers { get; init; }
    public List<ActivityView>? Activities { get; init; }
    public List<AttachmentView>? Attachments { get; init; }
    public int TargetChanges { get; init; }             // 단계 목표일 변경 횟수
    public string? CreatedBy { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

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
    // 브라우저에서 바로 보여 줄 수 있는 첨부 형식
    public static readonly HashSet<string> PreviewTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "application/pdf",
    };

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

    // "그룹 · 파트" 표시
    public static string? OrgName(OrgUnit? unit)
    {
        if (unit == null) return null;
        return unit.Parent == null ? unit.Name : $"{unit.Parent.Name} · {unit.Name}";
    }

    public static WorkView Work(WorkItem w, DateOnly today, WorkCalendar cal,
        IReadOnlyDictionary<int, int>? floats = null, List<int>? predecessors = null, bool violation = false, Stage? stage = null)
    {
        int? slack = floats != null && floats.TryGetValue(w.Id, out var f) ? f : null;
        // 완료된 단계의 작업은 지연·임계로 보지 않는다 (Rules.IsOpen)
        var open = stage == null ? w.ActualEnd == null : Rules.IsOpen(w, stage);
        return new WorkView
        {
            Id = w.Id, StageId = w.StageId, Version = w.Version, Title = w.Title,
            Owner = w.Owner?.Name, CoOwner = w.CoOwner?.Name, Progress = w.Progress,
            PlanStart = w.PlanStart, PlanEnd = w.PlanEnd, PlanWorkDays = cal.WorkingDays(w.PlanStart, w.PlanEnd),
            ActualStart = w.ActualStart, ActualEnd = w.ActualEnd,
            BaselineStart = w.BaselineStart, BaselineEnd = w.BaselineEnd,
            Status = Key(Rules.StatusOf(w)), Late = open && Rules.IsLate(w, today), DoneLate = Rules.IsDoneLate(w), Note = w.Note,
            Predecessors = predecessors ?? new List<int>(),
            Float = slack,
            Critical = open && slack != null && slack.Value <= 0,
            DepViolation = violation,
        };
    }

    public static List<StageView> Stages(Project p, DateOnly today, int oceanLeadDays, bool withWorks, WorkCalendar? calendar = null)
    {
        var cal = calendar ?? WorkCalendar.WeekendsOnly;
        var list = new List<StageView>();
        DateOnly? prevTarget = p.StartDate;       // 목표 막대의 시작: 직전 단계 목표일(없으면 착수일)
        DateOnly? prevCompleted = p.StartDate;    // 실행 막대의 시작: 직전 단계 완료일(첫 단계는 착수일)
        var inbound = Rules.InboundRecommendation(p, oceanLeadDays);

        // 의존 관계에서 나오는 값은 작업을 내보낼 때만 계산한다
        IReadOnlyDictionary<int, int>? floats = null;
        var preds = new Dictionary<int, List<int>>();
        var violated = new HashSet<int>();
        if (withWorks)
        {
            floats = Rules.Floats(p, cal);
            var byId = new Dictionary<int, (WorkItem Work, Stage Stage)>();
            foreach (var st in p.Stages)
                foreach (var w in st.Works) byId[w.Id] = (w, st);
            foreach (var l in p.Links)
            {
                if (!byId.TryGetValue(l.PredecessorId, out var from) || !byId.TryGetValue(l.SuccessorId, out var to)) continue;
                if (!preds.TryGetValue(to.Work.Id, out var ids)) preds[to.Work.Id] = ids = new List<int>();
                ids.Add(from.Work.Id);
                // 밀기 대상(미착수·단계 미완료)만 위반으로 표시한다 — 나머지는 자동으로 옮기지 않으므로 경고할 일이 아니다
                if (Rules.IsMovable(to.Work, to.Stage) && Rules.Violates(l, from.Work, to.Work, cal)) violated.Add(to.Work.Id);
            }
        }

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
                ? s.Works.OrderBy(w => w.PlanStart).ThenBy(w => w.Id)
                    .Select(w => Work(w, today, cal, floats, preds.TryGetValue(w.Id, out var ids) ? ids : null, violated.Contains(w.Id), s))
                    .ToList()
                : new List<WorkView>();

            var checks = s.Checks.OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new CheckView(c.Id, c.Text, c.IsDone, c.DoneBy?.Name, c.DoneAt)).ToList();

            list.Add(new StageView
            {
                Id = s.Id, Version = s.Version, Name = s.Name, Kind = Key(s.Kind), Sort = s.SortOrder,
                TargetDate = s.TargetDate, CompletedDate = s.CompletedDate,
                BaselineDate = s.BaselineDate, SlipDays = Rules.SlipDays(s),
                Status = Key(status), Progress = Rules.StageProgress(s), LateDays = lateDays,
                PlanFrom = planFrom, ActualFrom = actualFrom, ActualTo = actualTo,
                Forecast = Rules.Forecast(s, today), ForecastOver = Rules.ForecastOverDays(s, today),
                InboundHint = s.Kind == StageKind.Inbound && s.CompletedDate == null ? inbound : null,
                Checks = checks, CheckDone = checks.Count(c => c.Done), CheckTotal = checks.Count,
                Works = works,
                OpenWorks = withWorks && s.CompletedDate == null ? s.Works.Count(w => w.ActualEnd == null) : 0,
                LaterWorks = withWorks ? Rules.CascadeWorks(p, s).Count() : 0,
            });

            if (s.TargetDate != null) prevTarget = s.TargetDate;
            prevCompleted = s.CompletedDate;   // 미완료면 null → 다음 단계 실행 막대 없음
        }
        return list;
    }

    public static ProjectView Project(Project p, DateOnly today, LocationIndex locations, bool detail,
        bool withWorks = true, WorkCalendar? calendar = null)
    {
        var corp = locations.Get(p.CorporationId);
        var lead = corp?.OceanLeadDays ?? 0;
        var current = Rules.CurrentStage(p);
        var delayed = Rules.MostDelayedStage(p, today);
        var delayDays = Rules.DelayDays(p, today);
        var stages = Stages(p, today, lead, withWorks, calendar);
        var endTarget = Rules.EndTarget(p);
        var baselineEnd = Rules.BaselineEnd(p);

        return new ProjectView
        {
            Id = p.Id, Version = p.Version, Title = p.Title, State = Key(p.State),
            CategoryId = p.CategoryId, Category = p.Category?.Name ?? "", CategoryKind = Key(p.Category?.Kind ?? Domain.CategoryKind.General),
            CorporationId = p.CorporationId, Corporation = corp?.Name ?? "",
            LocationId = p.LocationId, LocationPath = locations.PathBelowCorporation(p.LocationId),
            OwnerId = p.OwnerId, Owner = p.Owner?.Name ?? "", OwnerOrg = OrgName(p.Owner?.OrgUnit),
            StartDate = p.StartDate, EndTarget = endTarget,
            Progress = Rules.ProjectProgress(p), DelayDays = delayDays, DelayedStage = delayDays > 0 ? delayed?.Name : null,
            CurrentStage = current?.Name, CurrentTarget = current?.TargetDate,
            WorkCount = p.Stages.Sum(s => s.Works.Count), OceanLeadDays = lead,
            BaselineAt = p.BaselineAt, BaselineEnd = baselineEnd,
            SlipDays = baselineEnd == null || endTarget == null ? null : endTarget.Value.DayNumber - baselineEnd.Value.DayNumber,
            CriticalCount = stages.Sum(s => s.Works.Count(w => w.Critical)),
            Stages = stages,
            Links = withWorks ? p.Links.OrderBy(l => l.Id).Select(l => new LinkView(l.Id, l.PredecessorId, l.SuccessorId, l.LagDays)).ToList() : new List<LinkView>(),

            Vendor = detail ? p.Vendor : null,
            Description = detail ? p.Description : null,
            Equipments = detail ? p.Equipments.OrderBy(e => e.Id).Select(e => new ItemView(e.EquipmentTypeId, EquipmentName(e.EquipmentType), e.Quantity, e.Note)).ToList() : null,
            Containers = detail ? p.Containers.OrderBy(e => e.Id).Select(e => new ItemView(e.ContainerTypeId, e.ContainerType?.Name ?? "", e.Quantity, e.Note)).ToList() : null,
            Activities = detail ? p.Activities.OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
                .Select(a => new ActivityView(a.Id, Key(a.Type), a.Text, a.Reason, a.User?.Name ?? "", a.At, a.StageId, a.WorkItemId)).ToList() : null,
            Attachments = detail ? p.Attachments.OrderByDescending(a => a.UploadedAt).ThenByDescending(a => a.Id)
                .Select(a => new AttachmentView(a.Id, a.FileName, a.ContentType, a.Size, a.UploadedBy?.Name ?? "", a.UploadedAt, a.StageId,
                    PreviewTypes.Contains(a.ContentType))).ToList() : null,
            TargetChanges = detail ? p.Activities.Count(a => a.Type == ActivityType.TargetChanged) : 0,
            CreatedBy = detail ? p.CreatedBy?.Name : null,
            CreatedAt = detail ? p.CreatedAt : null,
            UpdatedAt = detail ? p.UpdatedAt : null,
        };
    }

    // "AGV · Belt" 처럼 대분류와 함께 표시
    public static string EquipmentName(EquipmentType? t)
    {
        if (t == null) return "";
        return t.Parent == null ? t.Name : $"{t.Parent.Name} · {t.Name}";
    }
}
