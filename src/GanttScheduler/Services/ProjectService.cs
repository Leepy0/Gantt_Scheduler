using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

public record ItemInput(int TypeId, int Quantity, string? Note);

public record ProjectInput(
    string? Title, int CategoryId, int LocationId, string? Owner, DateOnly? StartDate,
    string? Vendor, string? Description, List<ItemInput>? Equipments, List<ItemInput>? Containers, int? Version = null);

// CloseWorks: 완료하면서 남은 작업을 완료일로 닫는다. CascadeWorks: 목표일을 옮길 때 이후 단계의 미착수 작업도 같은 근무일만큼 옮긴다
public record StageInput(
    int ProjectId, int? Id, string? Name, DateOnly? TargetDate, DateOnly? CompletedDate, string? Reason, bool Cascade,
    int? Version = null, bool CloseWorks = false, bool CascadeWorks = false);

public record WorkInput(
    int? Id, int StageId, string? Title, string? Owner, string? CoOwner,
    DateOnly? PlanStart, DateOnly? PlanEnd, DateOnly? ActualStart, DateOnly? ActualEnd,
    int Progress, string? Note, string? Reason, int? Version = null, List<int>? Predecessors = null);

public record CommentInput(int ProjectId, int? StageId, int? WorkId, string? Text);

public record CheckInput(int StageId, int? Id, string? Text);

public record CloneInput(int ProjectId, string? Title, DateOnly? StartDate, bool IncludeWorks);

public record HolidayView(DateOnly Date, string Name);

// 페이지에 심는 JSON 직렬화 옵션 (camelCase)
public static class AppJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);
    public static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value, Web);
}

// 내가 화면을 연 뒤 다른 사람이 같은 항목을 먼저 고쳤을 때
public class ConflictException : RuleException
{
    public int ProjectId { get; }
    public ConflictException(int projectId)
        : base("다른 사용자가 먼저 수정했습니다. 최신 내용으로 바꿨으니 확인한 뒤 다시 시도해 주세요.") => ProjectId = projectId;
}

// 과제·단계·작업의 조회와 변경. 변경은 모두 활동 이력을 남긴다
public class ProjectService
{
    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;
    private readonly UserService _users;
    private WorkCalendar? _calendar;

    public ProjectService(AppDb db, IClock clock, CurrentUser me, UserService users)
    {
        _db = db;
        _clock = clock;
        _me = me;
        _users = users;
    }

    // ── 조회 ─────────────────────────────────────────────────────────────

    public async Task<LocationIndex> LocationsAsync() => new(await _db.Locations.AsNoTracking().ToListAsync());

    public async Task<List<HolidayView>> HolidaysAsync() =>
        (await _db.Holidays.AsNoTracking().ToListAsync()).OrderBy(h => h.Date).Select(h => new HolidayView(h.Date, h.Name)).ToList();

    // 근무일 달력 (요청 안에서는 한 번만 읽는다)
    public async Task<WorkCalendar> CalendarAsync() =>
        _calendar ??= new WorkCalendar((await _db.Holidays.AsNoTracking().ToListAsync()).Select(h => h.Date));

    // 담당자 자동완성용 이름 목록
    public async Task<List<string>> UserNamesAsync() =>
        (await _db.Users.AsNoTracking().ToListAsync()).Where(u => u.IsActive).Select(u => u.Name).OrderBy(n => n).ToList();

    private IQueryable<Project> Graph() => _db.Projects
        .Include(p => p.Category)
        .Include(p => p.Owner).ThenInclude(o => o!.OrgUnit).ThenInclude(u => u!.Parent)
        .Include(p => p.Stages).ThenInclude(s => s.Works).ThenInclude(w => w.Owner)
        .Include(p => p.Stages).ThenInclude(s => s.Works).ThenInclude(w => w.CoOwner);

    // 전체 과제(단계·작업 포함) — 대시보드/목록 집계용. 규모가 작아 메모리에서 계산한다
    public async Task<List<Project>> AllAsync() =>
        await Graph().AsNoTracking().AsSplitQuery().ToListAsync();

    public async Task<Project?> LoadAsync(int id) =>
        await Graph()
            .Include(p => p.CreatedBy)
            .Include(p => p.Stages).ThenInclude(s => s.Checks).ThenInclude(c => c.DoneBy)
            .Include(p => p.Links)
            .Include(p => p.Equipments).ThenInclude(e => e.EquipmentType).ThenInclude(t => t!.Parent)
            .Include(p => p.Containers).ThenInclude(c => c.ContainerType)
            .Include(p => p.Attachments).ThenInclude(a => a.UploadedBy)
            .Include(p => p.Activities).ThenInclude(a => a.User)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<ProjectView?> ViewAsync(int id)
    {
        var p = await LoadAsync(id);
        return p == null ? null : Views.Project(p, _clock.Today, await LocationsAsync(), detail: true, calendar: await CalendarAsync());
    }

    private async Task<Project> RequireAsync(int id) =>
        await LoadAsync(id) ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");

    // ── 과제 ─────────────────────────────────────────────────────────────

    public async Task<int> CreateAsync(ProjectInput input)
    {
        var title = Text(input.Title, 200, "과제명", "title", required: true)!;
        var category = await _db.Categories.Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems)
                           .FirstOrDefaultAsync(c => c.Id == input.CategoryId)
            ?? throw new RuleException("과제 유형을 선택해 주세요.", "categoryId");
        var (corp, loc) = await ResolveLocationAsync(input.LocationId);
        if (input.StartDate != null) Rules.ValidateStartDate(input.StartDate.Value, _clock.Today);
        var owner = await _users.ResolveAsync(input.Owner) ?? _me.User!;
        var now = _clock.UtcNow;

        var p = new Project
        {
            Title = title,
            CategoryId = category.Id,
            CorporationId = corp.Id,
            LocationId = loc.Id,
            OwnerId = owner.Id,
            State = ProjectState.Active,
            StartDate = input.StartDate ?? _clock.Today,
            Vendor = Text(input.Vendor, 100, "협력사", "vendor"),
            Description = Text(input.Description, 2000, "설명", "description"),
            CreatedAt = now,
            CreatedById = _me.Id,
            UpdatedAt = now,
        };
        // 아직 저장 전인 새 담당자(Id 0)는 내비게이션으로 연결해 과제와 함께 저장한다
        if (owner.Id == 0) p.Owner = owner;

        // 단계 템플릿(체크리스트 포함)을 그 시점 그대로 복사 — 이후 템플릿이 바뀌어도 기존 과제는 영향 없음
        foreach (var t in category.StageTemplates.OrderBy(t => t.SortOrder))
        {
            var stage = new Stage { Name = t.Name, Kind = t.Kind, SortOrder = t.SortOrder };
            foreach (var c in t.CheckItems.OrderBy(c => c.SortOrder))
                stage.Checks.Add(new CheckItem { Text = c.Text, SortOrder = c.SortOrder });
            p.Stages.Add(stage);
        }

        await ApplyItemsAsync(p, category.Kind, input);
        p.Activities.Add(NewActivity(ActivityType.Created, "과제 등록"));

        _db.Projects.Add(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task UpdateAsync(int id, ProjectInput input)
    {
        var title = Text(input.Title, 200, "과제명", "title", required: true)!;
        var (corp, loc) = await ResolveLocationAsync(input.LocationId);
        var p = await RequireAsync(id);
        CheckVersion(input.Version, p.Version, p.Id);
        if (input.StartDate != null && p.StartDate != input.StartDate.Value)
        {
            // 착수일이 일정 범위의 기준이므로, 옮긴 착수일 기준으로 기존 일정이 범위 밖이 되면 막는다(연도 오타를 여기서 잡는다)
            Rules.ValidateStartDate(input.StartDate.Value, _clock.Today);
            if (Rules.FirstOutOfRange(p, input.StartDate.Value) is { } outside)
            {
                var (min, max) = Rules.DateRange(input.StartDate.Value);
                throw new RuleException($"착수일을 {Fmt(input.StartDate)}(으)로 바꾸면 기존 일정 {Fmt(outside)} 이(가) 허용 범위({Fmt(min)} ~ {Fmt(max)})를 벗어납니다.", "startDate");
            }
        }
        var owner = await _users.ResolveAsync(input.Owner, keepId: p.OwnerId);

        var changed = new List<string>();
        if (p.Title != title) { changed.Add("과제명"); p.Title = title; }
        if (p.LocationId != loc.Id) { changed.Add("위치"); p.CorporationId = corp.Id; p.LocationId = loc.Id; }
        if (owner != null && p.OwnerId != owner.Id) { changed.Add($"담당자 → {owner.Name}"); p.Owner = owner; if (owner.Id != 0) p.OwnerId = owner.Id; }
        if (input.StartDate != null && p.StartDate != input.StartDate.Value) { changed.Add($"착수일 → {Fmt(input.StartDate)}"); p.StartDate = input.StartDate.Value; }
        var vendor = Text(input.Vendor, 100, "협력사", "vendor");
        if (p.Vendor != vendor) { changed.Add("협력사"); p.Vendor = vendor; }
        var desc = Text(input.Description, 2000, "설명", "description");
        if (p.Description != desc) { changed.Add("설명"); p.Description = desc; }

        var before = ItemsSignature(p);
        var kind = p.Category?.Kind ?? CategoryKind.General;
        _db.ProjectEquipments.RemoveRange(p.Equipments);
        _db.ProjectContainers.RemoveRange(p.Containers);
        p.Equipments.Clear();
        p.Containers.Clear();
        await ApplyItemsAsync(p, kind, input);
        if (before != ItemsSignature(p)) changed.Add(kind == CategoryKind.Container ? "용기 내역" : "기기 내역");

        if (changed.Count > 0)
            p.Activities.Add(NewActivity(ActivityType.Edited, "과제 정보 수정: " + string.Join(", ", changed)));
        p.Version++;
        Touch(p);
        await _db.SaveChangesAsync();
    }

    private static string ItemsSignature(Project p) =>
        string.Join("|", p.Equipments.Select(e => $"E{e.EquipmentTypeId}:{e.Quantity}:{e.Note}"))
        + "/" + string.Join("|", p.Containers.Select(e => $"C{e.ContainerTypeId}:{e.Quantity}:{e.Note}"));

    private async Task ApplyItemsAsync(Project p, CategoryKind kind, ProjectInput input)
    {
        if (kind == CategoryKind.Automation)
        {
            var valid = (await _db.EquipmentTypes.AsNoTracking().ToListAsync()).Select(t => t.Id).ToHashSet();
            foreach (var item in input.Equipments ?? new())
            {
                if (item.TypeId == 0 && item.Quantity == 0) continue;   // 빈 행은 건너뜀
                if (!valid.Contains(item.TypeId)) throw new RuleException("기기 종류를 선택해 주세요.", "equipments");
                if (item.Quantity < 1) throw new RuleException("기기 수량은 1 이상으로 입력해 주세요.", "equipments");
                p.Equipments.Add(new ProjectEquipment { EquipmentTypeId = item.TypeId, Quantity = item.Quantity, Note = Text(item.Note, 200, "비고", "equipments") });
            }
        }
        else if (kind == CategoryKind.Container)
        {
            var valid = (await _db.ContainerTypes.AsNoTracking().ToListAsync()).Select(t => t.Id).ToHashSet();
            foreach (var item in input.Containers ?? new())
            {
                if (item.TypeId == 0 && item.Quantity == 0) continue;
                if (!valid.Contains(item.TypeId)) throw new RuleException("용기 종류를 선택해 주세요.", "containers");
                if (item.Quantity < 1) throw new RuleException("용기 수량은 1 이상으로 입력해 주세요.", "containers");
                p.Containers.Add(new ProjectContainer { ContainerTypeId = item.TypeId, Quantity = item.Quantity, Note = Text(item.Note, 200, "비고", "containers") });
            }
        }
    }

    private async Task<(Location Corp, Location Loc)> ResolveLocationAsync(int locationId)
    {
        var index = await LocationsAsync();
        var loc = index.Get(locationId) ?? throw new RuleException("법인을 선택해 주세요.", "locationId");
        var corp = index.CorporationOf(locationId) ?? loc;
        return (corp, loc);
    }

    public async Task SetStateAsync(int id, ProjectState state, string? reason, int? version = null)
    {
        var p = await RequireAsync(id);
        CheckVersion(version, p.Version, p.Id);
        if (state == ProjectState.Done)
            throw new RuleException("완료 상태는 모든 단계를 완료하면 자동으로 바뀝니다.");
        if (p.State == state) return;

        reason = Text(reason, 500, "사유", "reason");
        if ((state == ProjectState.Hold || state == ProjectState.Dropped) && reason == null)
            throw new RuleException("보류·중단은 사유를 입력해 주세요.", "reason");

        var old = p.State;
        p.State = state;
        p.Activities.Add(NewActivity(ActivityType.StateChanged, $"상태 변경: {Views.Label(old)} → {Views.Label(state)}", reason));
        SyncState(p);
        p.Version++;
        Touch(p);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var p = await RequireAsync(id);
        if (!_me.IsAdmin && p.OwnerId != _me.Id && p.CreatedById != _me.Id)
            throw new RuleException("과제 삭제는 담당자·등록자 또는 관리자만 할 수 있습니다.");
        // 의존 관계는 작업보다 먼저 지워야 한다 (작업 쪽 FK 가 Restrict)
        _db.WorkLinks.RemoveRange(p.Links);
        p.Links.Clear();
        _db.Projects.Remove(p);
        await _db.SaveChangesAsync();
    }

    // 지금의 목표 일정을 기준선으로 고정한다. 이후의 변경은 기준선 대비 며칠 밀렸는지로 보여 준다
    public async Task SetBaselineAsync(int id)
    {
        var p = await RequireAsync(id);
        var again = p.BaselineAt != null;
        foreach (var s in p.Stages)
        {
            s.BaselineDate = s.TargetDate;
            foreach (var w in s.Works)
            {
                w.BaselineStart = w.PlanStart;
                w.BaselineEnd = w.PlanEnd;
            }
        }
        p.BaselineAt = _clock.UtcNow;
        p.Activities.Add(NewActivity(ActivityType.BaselineSet, again ? "기준선 다시 확정" : "기준선 확정"));
        Touch(p);
        await _db.SaveChangesAsync();
    }

    // 과제 복제: 단계·체크리스트·작업·의존 관계·내역을 복사하고, 날짜는 원본 착수일로부터의 근무일 거리 그대로 새 착수일에 맞춰 옮긴다.
    // 달력 일수로 옮기면 요일이 바뀌어 주말에 놓이거나 근무일 기간이 줄고 의존 위반이 생긴다.
    // 실적·진행률·활동·첨부·기준선은 복사하지 않는다
    public async Task<int> CloneAsync(CloneInput input)
    {
        var src = await RequireAsync(input.ProjectId);
        var title = Text(input.Title, 200, "과제명", "title", required: true)!;
        var start = input.StartDate ?? _clock.Today;
        Rules.ValidateStartDate(start, _clock.Today);
        var cal = await CalendarAsync();
        var now = _clock.UtcNow;
        DateOnly Shift(DateOnly d) => cal.Shift(d, cal.NextWorkingDay(src.StartDate), cal.NextWorkingDay(start));

        var p = new Project
        {
            Title = title, CategoryId = src.CategoryId, CorporationId = src.CorporationId, LocationId = src.LocationId,
            OwnerId = _me.Id, State = ProjectState.Active, StartDate = start,
            Vendor = src.Vendor, Description = src.Description,
            CreatedAt = now, CreatedById = _me.Id, UpdatedAt = now,
        };

        var map = new Dictionary<int, WorkItem>();
        foreach (var s in Rules.Ordered(src))
        {
            var stage = new Stage { Name = s.Name, Kind = s.Kind, SortOrder = s.SortOrder, TargetDate = s.TargetDate == null ? null : Shift(s.TargetDate.Value) };
            foreach (var c in s.Checks.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
                stage.Checks.Add(new CheckItem { Text = c.Text, SortOrder = c.SortOrder });
            if (input.IncludeWorks)
            {
                foreach (var w in s.Works.OrderBy(w => w.Id))
                {
                    // 시작은 휴일이면 다음 근무일 자리로 보고 옮긴 뒤 근무일 기간을 그대로 놓는다
                    var (planStart, planEnd) = Rules.Place(Shift(cal.NextWorkingDay(w.PlanStart)), Rules.WorkDays(w, cal), cal);
                    var copy = new WorkItem
                    {
                        Title = w.Title, OwnerId = w.OwnerId, CoOwnerId = w.CoOwnerId, Note = w.Note,
                        PlanStart = planStart, PlanEnd = planEnd, CreatedAt = now,
                    };
                    map[w.Id] = copy;
                    stage.Works.Add(copy);
                }
            }
            p.Stages.Add(stage);
        }
        foreach (var l in src.Links)
        {
            if (map.TryGetValue(l.PredecessorId, out var from) && map.TryGetValue(l.SuccessorId, out var to))
                p.Links.Add(new WorkLink { Predecessor = from, Successor = to, LagDays = l.LagDays });
        }
        foreach (var e in src.Equipments)
            p.Equipments.Add(new ProjectEquipment { EquipmentTypeId = e.EquipmentTypeId, Quantity = e.Quantity, Note = e.Note });
        foreach (var c in src.Containers)
            p.Containers.Add(new ProjectContainer { ContainerTypeId = c.ContainerTypeId, Quantity = c.Quantity, Note = c.Note });

        // 원본에 남아 있던 의존 위반(공휴일 추가 등으로 생긴)은 복제본에 넘기지 않는다
        var fixes = Rules.Reschedule(p, cal);
        foreach (var m in fixes)
        {
            m.Work.PlanStart = m.NewStart;
            m.Work.PlanEnd = m.NewEnd;
        }
        if (Rules.FirstOutOfRange(p, start) is { } outside)
            throw new RuleException($"원본 일정을 옮기면 {Fmt(outside)} 이(가) 허용 범위를 벗어납니다. 착수일을 다시 확인해 주세요.", "startDate");

        p.Activities.Add(NewActivity(ActivityType.Cloned, $"과제 등록 (복제 원본: {src.Title})" + (fixes.Count > 0 ? $" — 의존 관계에 맞춰 작업 {fixes.Count}건 일정 조정" : "")));
        _db.Projects.Add(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // ── 단계 ─────────────────────────────────────────────────────────────

    public async Task SaveStageAsync(StageInput input)
    {
        var name = Text(input.Name, 50, "단계명", "name", required: true)!;
        var reason = Text(input.Reason, 500, "사유", "reason");
        var p = await RequireAsync(input.ProjectId);

        if (input.Id == null)
        {
            Rules.ValidateDate(input.TargetDate, p.StartDate, "목표일", "targetDate");
            Rules.ValidateDate(input.CompletedDate, p.StartDate, "완료일", "completedDate");
            var order = p.Stages.Count == 0 ? 1 : p.Stages.Max(s => s.SortOrder) + 1;
            p.Stages.Add(new Stage { Name = name, SortOrder = order, TargetDate = input.TargetDate, CompletedDate = input.CompletedDate });
            p.Activities.Add(NewActivity(ActivityType.StageAdded, $"단계 추가: {name}"));
        }
        else
        {
            var s = p.Stages.FirstOrDefault(x => x.Id == input.Id.Value)
                ?? throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.");
            CheckVersion(input.Version, s.Version, p.Id);
            // 바뀐 날짜만 범위를 본다 — 범위가 생기기 전에 들어간 값이 남아 있어도 다른 수정은 막지 않는다
            if (s.TargetDate != input.TargetDate) Rules.ValidateDate(input.TargetDate, p.StartDate, "목표일", "targetDate");
            if (s.CompletedDate != input.CompletedDate) Rules.ValidateDate(input.CompletedDate, p.StartDate, "완료일", "completedDate");
            var completing = s.CompletedDate == null && input.CompletedDate != null;
            if (completing)
            {
                // 체크리스트를 모두 끝내야 완료할 수 있다
                var open = s.Checks.Count(c => !c.IsDone);
                if (open > 0)
                    throw new RuleException($"체크리스트 {open}개 항목이 남아 있어 완료할 수 없습니다.", "completedDate");
            }
            var changed = false;

            if (s.Name != name)
            {
                p.Activities.Add(NewActivity(ActivityType.Edited, $"단계명 변경: {s.Name} → {name}", stageId: s.Id));
                s.Name = name;
                changed = true;
            }

            if (s.TargetDate != input.TargetDate)
            {
                changed = true;
                if (s.TargetDate == null)
                {
                    // 처음 정하는 목표일은 사유 없이 기록만 남긴다
                    p.Activities.Add(NewActivity(ActivityType.Edited, $"[{s.Name}] 목표일 설정 {Fmt(input.TargetDate)}", stageId: s.Id,
                        newValue: Fmt(input.TargetDate)));
                    s.TargetDate = input.TargetDate;
                }
                else
                {
                    if (reason == null) throw new RuleException("목표일을 바꿀 때는 변경 사유를 입력해 주세요.", "reason");
                    var batch = Guid.NewGuid();
                    var old = s.TargetDate.Value;
                    p.Activities.Add(NewActivity(ActivityType.TargetChanged, $"[{s.Name}] 목표일 {Fmt(old)} → {Fmt(input.TargetDate)}", reason,
                        s.Id, oldValue: Fmt(old), newValue: Fmt(input.TargetDate), batch: batch));
                    s.TargetDate = input.TargetDate;

                    var cal = await CalendarAsync();
                    var delta = input.TargetDate == null ? 0 : cal.Distance(old, input.TargetDate.Value);

                    // 연쇄 이동: 이후 미완료 단계의 목표일을 같은 근무일 수만큼 옮긴다 (주말·공휴일은 건너뜀)
                    if (input.Cascade && delta != 0)
                    {
                        foreach (var later in p.Stages.Where(x => x.SortOrder > s.SortOrder && x.CompletedDate == null && x.TargetDate != null))
                        {
                            var from = later.TargetDate!.Value;
                            var to = cal.AddWorkingDays(from, delta);
                            if (to == from) continue;
                            Rules.ValidateDate(to, p.StartDate, $"연쇄 이동할 '{later.Name}' 목표일", "targetDate");
                            later.TargetDate = to;
                            later.Version++;
                            p.Activities.Add(NewActivity(ActivityType.TargetChanged, $"[{later.Name}] 목표일 {Fmt(from)} → {Fmt(to)} (연쇄 이동)", reason,
                                later.Id, oldValue: Fmt(from), newValue: Fmt(to), batch: batch));
                        }
                    }

                    // 이후 단계의 미착수 작업도 같은 근무일만큼 옮긴다(선택). 당기면 앞 단계 선행과 어긋날 수 있어,
                    // 옮긴 작업에서 시작해 의존 관계를 다시 맞춘다. 작업마다 처음 → 마지막 일정으로 활동 1건
                    if (input.CascadeWorks && delta != 0)
                    {
                        var plan = new PlanBatch();
                        foreach (var w in Rules.CascadeWorks(p, s).ToList())
                        {
                            var (ns, ne) = Rules.ShiftWork(w, delta, cal);
                            plan.Move(w, ns, ne, "연쇄 이동", reason);
                        }
                        foreach (var m in Rules.Reschedule(p, cal, plan.Works.ToList()))
                            plan.Move(m.Work, m.NewStart, m.NewEnd, "선행 작업 연동", reason);
                        CommitPlan(p, plan, batch, "targetDate");
                    }
                }
            }

            if (s.CompletedDate != input.CompletedDate)
            {
                changed = true;
                if (completing)
                {
                    var batch = Guid.NewGuid();
                    p.Activities.Add(NewActivity(ActivityType.StageCompleted, $"[{s.Name}] 완료 ({Fmt(input.CompletedDate)})", stageId: s.Id,
                        newValue: Fmt(input.CompletedDate), batch: batch));
                    if (input.CloseWorks) CloseOpenWorks(p, s, input.CompletedDate!.Value, batch);
                }
                else if (input.CompletedDate == null)
                    p.Activities.Add(NewActivity(ActivityType.StageReopened, $"[{s.Name}] 완료 취소", stageId: s.Id, oldValue: Fmt(s.CompletedDate)));
                else
                    p.Activities.Add(NewActivity(ActivityType.Edited, $"[{s.Name}] 완료일 {Fmt(s.CompletedDate)} → {Fmt(input.CompletedDate)}", stageId: s.Id));
                s.CompletedDate = input.CompletedDate;
            }

            if (changed) s.Version++;
        }

        SyncState(p);
        Touch(p);
        await _db.SaveChangesAsync();
    }

    // 단계를 완료하면서 실적 종료가 없는 작업을 완료일로 닫는다. 실적 시작이 없으면 목표 시작일(완료일보다 늦으면 완료일)로 둔다
    private void CloseOpenWorks(Project p, Stage s, DateOnly done, Guid batch)
    {
        foreach (var w in s.Works.Where(w => w.ActualEnd == null).OrderBy(w => w.PlanStart).ThenBy(w => w.Id).ToList())
        {
            var changes = new List<string>();
            var start = w.ActualStart ?? (w.PlanStart < done ? w.PlanStart : done);
            if (w.ActualStart == null) changes.Add($"실적 시작 {Fmt(start)}");
            var end = start > done ? start : done;    // 완료일보다 늦게 시작한 작업은 시작일에 닫는다(종료가 시작보다 앞설 수 없다)
            changes.Add($"실적 종료 {Fmt(end)}");
            if (w.Progress != 100) changes.Add($"진행률 {w.Progress}% → 100%");
            w.ActualStart = start;
            w.ActualEnd = end;
            w.Progress = 100;
            w.Version++;
            p.Activities.Add(NewActivity(ActivityType.WorkEdited, $"[{w.Title}] 단계 완료로 닫음: " + string.Join(", ", changes),
                stageId: s.Id, workId: w.Id, batch: batch));
        }
    }

    // dir: -1 위로, +1 아래로
    public async Task<int> MoveStageAsync(int stageId, int dir)
    {
        var p = await RequireByStageAsync(stageId);
        var ordered = Rules.Ordered(p).ToList();
        var i = ordered.FindIndex(s => s.Id == stageId);
        var j = i + (dir < 0 ? -1 : 1);
        if (i < 0 || j < 0 || j >= ordered.Count) return p.Id;

        (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        for (var k = 0; k < ordered.Count; k++) ordered[k].SortOrder = k + 1;
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task<int> DeleteStageAsync(int stageId)
    {
        var p = await RequireByStageAsync(stageId);
        var s = p.Stages.First(x => x.Id == stageId);
        var works = s.Works.Count;
        RemoveLinksOf(p, s.Works.Select(w => w.Id).ToHashSet());
        p.Stages.Remove(s);
        _db.Stages.Remove(s);
        p.Activities.Add(NewActivity(ActivityType.StageRemoved, works > 0 ? $"단계 삭제: {s.Name} (작업 {works}건 포함)" : $"단계 삭제: {s.Name}"));
        SyncState(p);
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<Project> RequireByStageAsync(int stageId)
    {
        var projectId = await _db.Stages.Where(s => s.Id == stageId).Select(s => s.ProjectId).FirstOrDefaultAsync();
        if (projectId == 0) throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        return await RequireAsync(projectId);
    }

    // ── 체크리스트 ───────────────────────────────────────────────────────

    public async Task<int> SaveCheckAsync(CheckInput input)
    {
        var text = Text(input.Text, 200, "체크 항목", "check", required: true)!;
        var p = await RequireByStageAsync(input.StageId);
        var s = p.Stages.First(x => x.Id == input.StageId);

        if (input.Id == null)
        {
            if (s.CompletedDate != null)
                throw new RuleException("완료한 단계에는 체크 항목을 추가할 수 없습니다. 먼저 완료를 취소해 주세요.", "check");
            var order = s.Checks.Count == 0 ? 1 : s.Checks.Max(c => c.SortOrder) + 1;
            s.Checks.Add(new CheckItem { Text = text, SortOrder = order });
            p.Activities.Add(NewActivity(ActivityType.CheckChanged, $"[{s.Name}] 체크 항목 추가: {text}", stageId: s.Id));
        }
        else
        {
            var c = s.Checks.FirstOrDefault(x => x.Id == input.Id.Value) ?? throw new RuleException("체크 항목을 찾을 수 없습니다.");
            c.Text = text;
        }
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task<int> ToggleCheckAsync(int checkId, bool done)
    {
        var (p, s, c) = await RequireCheckAsync(checkId);
        if (c.IsDone == done) return p.Id;
        if (!done && s.CompletedDate != null)
            throw new RuleException("완료한 단계의 체크는 해제할 수 없습니다. 먼저 완료를 취소해 주세요.", "check");

        c.IsDone = done;
        c.DoneById = done ? _me.Id : null;
        c.DoneBy = done ? _me.User : null;
        c.DoneAt = done ? _clock.UtcNow : null;
        p.Activities.Add(NewActivity(ActivityType.CheckChanged, $"[{s.Name}] {(done ? "체크" : "체크 해제")}: {c.Text}", stageId: s.Id));
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task<int> DeleteCheckAsync(int checkId)
    {
        var (p, s, c) = await RequireCheckAsync(checkId);
        s.Checks.Remove(c);
        _db.CheckItems.Remove(c);
        p.Activities.Add(NewActivity(ActivityType.CheckChanged, $"[{s.Name}] 체크 항목 삭제: {c.Text}", stageId: s.Id));
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<(Project, Stage, CheckItem)> RequireCheckAsync(int checkId)
    {
        var stageId = await _db.CheckItems.Where(c => c.Id == checkId).Select(c => c.StageId).FirstOrDefaultAsync();
        if (stageId == 0) throw new RuleException("체크 항목을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        var p = await RequireByStageAsync(stageId);
        var s = p.Stages.First(x => x.Id == stageId);
        return (p, s, s.Checks.First(c => c.Id == checkId));
    }

    // ── 작업 ─────────────────────────────────────────────────────────────

    public async Task<int> SaveWorkAsync(WorkInput input)
    {
        var title = Text(input.Title, 200, "작업명", "title", required: true)!;
        var note = Text(input.Note, 1000, "메모", "note");
        var reason = Text(input.Reason, 500, "사유", "reason");
        if (input.PlanStart == null || input.PlanEnd == null)
            throw new RuleException("목표 시작일과 종료일을 입력해 주세요.", "planStart");
        if (input.PlanStart.Value > input.PlanEnd.Value)
            throw new RuleException("목표 시작일이 종료일보다 늦습니다.", "planStart");
        if (input.ActualEnd != null && input.ActualStart == null)
            throw new RuleException("실적 종료일을 넣으려면 실적 시작일도 입력해 주세요.", "actualStart");
        if (input.ActualStart != null && input.ActualEnd != null && input.ActualStart.Value > input.ActualEnd.Value)
            throw new RuleException("실적 시작일이 종료일보다 늦습니다.", "actualStart");
        Rules.ValidateProgress(input.Progress);

        var cal = await CalendarAsync();
        var p = await RequireByStageAsync(input.StageId);
        var stage = p.Stages.First(s => s.Id == input.StageId);
        var all = Rules.AllWorks(p).ToDictionary(w => w.Id);

        // 담당자 확인. 새 이름은 저장하지 않고 Add 만 해 두므로 아래 검증에 실패하면 남지 않는다(마지막 SaveChanges 에서 함께 저장)
        var current = input.Id != null ? all.GetValueOrDefault(input.Id.Value) : null;
        var owner = await _users.ResolveAsync(input.Owner, "owner", current?.OwnerId);
        var coOwner = await _users.ResolveAsync(input.CoOwner, "coOwner", current?.CoOwnerId);
        if (ReferenceEquals(owner, coOwner)) coOwner = null;

        var progress = input.Progress;
        var actualStart = input.ActualStart;
        if (input.ActualEnd != null) progress = 100;                          // 종료일이 있으면 100%
        else if (progress > 0 && actualStart == null) actualStart = _clock.Today; // 진행률만 올렸으면 오늘 시작한 것으로

        WorkItem w;
        var batch = Guid.NewGuid();
        var planChanged = false;
        string? linkChange = null;

        if (input.Id == null)
        {
            ValidateWorkDates(p, null, input.PlanStart.Value, input.PlanEnd.Value, actualStart, input.ActualEnd);
            var wanted = ValidatePredecessors(p, null, all, input.Predecessors, input.PlanStart.Value, actualStart != null, planChanged: true, cal);
            w = new WorkItem
            {
                Title = title, Owner = owner, CoOwner = coOwner, Progress = progress,
                PlanStart = input.PlanStart.Value, PlanEnd = input.PlanEnd.Value,
                ActualStart = actualStart, ActualEnd = input.ActualEnd, Note = note, CreatedAt = _clock.UtcNow,
            };
            ApplyLinks(p, w, all, wanted);
            stage.Works.Add(w);
            p.Activities.Add(NewActivity(ActivityType.WorkAdded, $"[{stage.Name}] 작업 추가: {title}", stageId: stage.Id));
        }
        else
        {
            if (!all.TryGetValue(input.Id.Value, out var found))
                throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
            w = found;
            CheckVersion(input.Version, w.Version, p.Id);

            planChanged = w.PlanStart != input.PlanStart.Value || w.PlanEnd != input.PlanEnd.Value;
            if (planChanged && reason == null)
                throw new RuleException("목표 일정을 바꿀 때는 변경 사유를 입력해 주세요.", "reason");
            // 검사를 모두 통과한 뒤에만 값을 바꾼다
            ValidateWorkDates(p, w, input.PlanStart.Value, input.PlanEnd.Value, actualStart, input.ActualEnd);
            var wanted = ValidatePredecessors(p, w, all, input.Predecessors, input.PlanStart.Value, actualStart != null, planChanged, cal);

            if (planChanged)
            {
                p.Activities.Add(NewActivity(ActivityType.WorkPlanChanged,
                    $"[{title}] 목표 일정 {Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)} → {Fmt(input.PlanStart)}~{Fmt(input.PlanEnd)}", reason,
                    stage.Id, w.Id, $"{Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)}", $"{Fmt(input.PlanStart)}~{Fmt(input.PlanEnd)}", batch));
                w.PlanStart = input.PlanStart.Value;
                w.PlanEnd = input.PlanEnd.Value;
            }

            var changed = new List<string>();
            if (w.Title != title) { changed.Add($"작업명 → {title}"); w.Title = title; }
            if (w.StageId != stage.Id)
            {
                // FK 만 바꾸면 EF 가 저장 시 양쪽 컬렉션을 맞춰 준다 (같은 과제 안에서만 이동)
                changed.Add($"단계 → {stage.Name}");
                w.StageId = stage.Id;
            }
            // 새 담당자(Id 0)는 FK 를 건드리지 않고 내비게이션으로만 연결한다 — 저장할 때 EF 가 새 Id 로 맞춘다
            if (w.OwnerId != owner?.Id) { changed.Add($"담당 → {owner?.Name ?? "없음"}"); if (owner?.Id != 0) w.OwnerId = owner?.Id; w.Owner = owner; }
            if (w.CoOwnerId != coOwner?.Id) { changed.Add($"부담당 → {coOwner?.Name ?? "없음"}"); if (coOwner?.Id != 0) w.CoOwnerId = coOwner?.Id; w.CoOwner = coOwner; }
            if (w.Progress != progress) { changed.Add($"진행률 {w.Progress}% → {progress}%"); w.Progress = progress; }
            if (w.ActualStart != actualStart) { changed.Add($"실적 시작 {Fmt(actualStart)}"); w.ActualStart = actualStart; }
            if (w.ActualEnd != input.ActualEnd) { changed.Add(input.ActualEnd == null ? "실적 종료 취소" : $"실적 종료 {Fmt(input.ActualEnd)}"); w.ActualEnd = input.ActualEnd; }
            if (w.Note != note) { changed.Add("메모"); w.Note = note; }
            linkChange = ApplyLinks(p, w, all, wanted);
            if (linkChange != null) changed.Add(linkChange);

            if (changed.Count > 0)
                p.Activities.Add(NewActivity(ActivityType.WorkEdited, $"[{title}] " + string.Join(", ", changed), stageId: stage.Id, workId: w.Id));
            w.Version++;
        }

        // 이 작업의 일정이나 선행이 바뀌었으면 아직 시작하지 않은 후행 작업을 선행 일정에 맞춰 민다.
        // 다른 것만 고친 저장에서는 관계없는 작업을 옮기지 않는다
        if (planChanged || linkChange != null)
        {
            var plan = new PlanBatch();
            foreach (var m in Rules.Reschedule(p, cal, new[] { w }))
                plan.Move(m.Work, m.NewStart, m.NewEnd, "선행 작업 연동", planChanged ? reason! : $"선행 작업 '{m.Cause.Title}' 일정에 맞춤");
            CommitPlan(p, plan, batch, "planEnd");
        }

        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // 작업 날짜 범위 검사. 기존 작업은 바뀐 날짜만 본다 — 범위가 생기기 전에 들어간 값이 남아 있어도 다른 수정은 막지 않는다
    private static void ValidateWorkDates(Project p, WorkItem? w, DateOnly planStart, DateOnly planEnd, DateOnly? actualStart, DateOnly? actualEnd)
    {
        if (w == null || w.PlanStart != planStart) Rules.ValidateDate(planStart, p.StartDate, "목표 시작일", "planStart");
        if (w == null || w.PlanEnd != planEnd) Rules.ValidateDate(planEnd, p.StartDate, "목표 종료일", "planEnd");
        if (w == null || w.ActualStart != actualStart) Rules.ValidateDate(actualStart, p.StartDate, "실적 시작일", "actualStart");
        if (w == null || w.ActualEnd != actualEnd) Rules.ValidateDate(actualEnd, p.StartDate, "실적 종료일", "actualEnd");
    }

    // 선행 작업 지정이 유효한지 검사하고, 적용할 선행 작업 id 목록을 돌려준다 (predecessors 가 null 이면 지금 그대로).
    // w 가 null 이면 새 작업. 존재·순환은 늘 검사한다. "선행이 끝난 뒤 시작" 검사는 새 작업이거나 목표 일정·선행 집합이
    // 바뀔 때만 한다 — 공휴일 추가 등으로 이미 생긴 위반이 제목·담당·메모·진행률만 고치는 저장까지 막지 않게(위반은 화면에 표시된다)
    private static List<int> ValidatePredecessors(Project p, WorkItem? w, Dictionary<int, WorkItem> all, List<int>? predecessors,
                                                  DateOnly planStart, bool started, bool planChanged, WorkCalendar cal)
    {
        var current = w == null ? new List<WorkLink>() : p.Links.Where(l => l.SuccessorId == w.Id).ToList();
        var wanted = predecessors == null ? current.Select(l => l.PredecessorId).ToList() : predecessors.Distinct().ToList();
        var predsChanged = !current.Select(l => l.PredecessorId).ToHashSet().SetEquals(wanted);
        var checkDates = !started && (w == null || planChanged || predsChanged);

        // 순환 검사는 이 작업으로 들어오는 기존 관계를 빼고, 지정한 것을 하나씩 더해 가며 한다
        var edges = p.Links.Where(l => w == null || l.SuccessorId != w.Id).Select(l => (l.PredecessorId, l.SuccessorId)).ToList();
        foreach (var id in wanted)
        {
            if (!all.TryGetValue(id, out var pred) || (w != null && id == w.Id))
                throw new RuleException("선행 작업을 다시 선택해 주세요.", "predecessors");
            if (w != null && Rules.Reaches(edges, w.Id, id))
                throw new RuleException($"'{pred.Title}' 을(를) 선행으로 두면 의존 관계가 순환합니다.", "predecessors");
            if (w != null) edges.Add((id, w.Id));

            // 아직 시작하지 않은 작업은 선행 작업이 끝난 다음 근무일 이후로만 잡을 수 있다
            if (!checkDates) continue;
            var link = current.FirstOrDefault(l => l.PredecessorId == id) ?? new WorkLink { PredecessorId = id };
            var earliest = Rules.EarliestStart(link, pred, cal);
            if (planStart < earliest)
                throw new RuleException($"선행 작업 '{pred.Title}' 이(가) {Fmt(pred.PlanEnd)} 에 끝나므로 {Fmt(earliest)} 이후에 시작해야 합니다.", "planStart");
        }
        return wanted;
    }

    // 선행 작업 목록을 wanted 로 맞춘다. 바뀐 내용 요약을 돌려준다(변화 없으면 null)
    private string? ApplyLinks(Project p, WorkItem w, Dictionary<int, WorkItem> all, List<int> wanted)
    {
        var current = w.Id == 0 ? new List<WorkLink>() : p.Links.Where(l => l.SuccessorId == w.Id).ToList();
        var removed = current.Where(l => !wanted.Contains(l.PredecessorId)).ToList();
        var added = wanted.Where(id => current.All(l => l.PredecessorId != id)).ToList();
        foreach (var l in removed)
        {
            p.Links.Remove(l);
            _db.WorkLinks.Remove(l);
        }
        foreach (var id in added)
            p.Links.Add(new WorkLink { Predecessor = all[id], PredecessorId = id, Successor = w, SuccessorId = w.Id });

        if (removed.Count == 0 && added.Count == 0) return null;
        var names = wanted.Select(id => all[id].Title).ToList();
        return names.Count == 0 ? "선행 작업 없음" : "선행 작업 → " + string.Join(", ", names);
    }

    // 한 묶음(BatchId) 안에서 움직인 작업 일정. 같은 작업이 여러 번 움직여도(연쇄 이동 뒤 선행 맞춤 등)
    // 활동은 처음 → 마지막 일정으로 작업당 1건만 남도록 처음 값과 이유를 기억한다
    private sealed class PlanBatch
    {
        private readonly List<(WorkItem Work, DateOnly Start, DateOnly End, string Note, string Reason)> _first = new();

        public IEnumerable<WorkItem> Works => _first.Select(x => x.Work);
        public IReadOnlyList<(WorkItem Work, DateOnly Start, DateOnly End, string Note, string Reason)> Entries => _first;

        public void Move(WorkItem w, DateOnly start, DateOnly end, string note, string reason)
        {
            if (!_first.Any(x => ReferenceEquals(x.Work, w))) _first.Add((w, w.PlanStart, w.PlanEnd, note, reason));
            w.PlanStart = start;
            w.PlanEnd = end;
        }
    }

    // 움직인 작업마다 Version 을 올리고 WorkPlanChanged 활동을 1건씩 남긴다 (결국 제자리면 남기지 않는다).
    // 함께 옮겨진 일정도 날짜 범위를 지켜야 한다 — 어기면 저장 전에 거절해 아무것도 바꾸지 않는다(field 는 화면에서 표시할 입력)
    private void CommitPlan(Project p, PlanBatch plan, Guid batch, string field)
    {
        foreach (var (w, oldStart, oldEnd, _, _) in plan.Entries)
        {
            if (w.PlanStart == oldStart && w.PlanEnd == oldEnd) continue;
            Rules.ValidateDate(w.PlanStart, p.StartDate, $"함께 옮겨지는 작업 '{w.Title}' 의 목표 시작일", field);
            Rules.ValidateDate(w.PlanEnd, p.StartDate, $"함께 옮겨지는 작업 '{w.Title}' 의 목표 종료일", field);
        }
        foreach (var (w, oldStart, oldEnd, note, reason) in plan.Entries)
        {
            if (w.PlanStart == oldStart && w.PlanEnd == oldEnd) continue;
            w.Version++;
            p.Activities.Add(NewActivity(ActivityType.WorkPlanChanged,
                $"[{w.Title}] 목표 일정 {Fmt(oldStart)}~{Fmt(oldEnd)} → {Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)} ({note})",
                reason, w.StageId, w.Id, $"{Fmt(oldStart)}~{Fmt(oldEnd)}", $"{Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)}", batch));
        }
    }

    private void RemoveLinksOf(Project p, HashSet<int> workIds)
    {
        var links = p.Links.Where(l => workIds.Contains(l.PredecessorId) || workIds.Contains(l.SuccessorId)).ToList();
        foreach (var l in links) p.Links.Remove(l);
        _db.WorkLinks.RemoveRange(links);
    }

    public async Task<int> DeleteWorkAsync(int workId)
    {
        var stageId = await _db.WorkItems.Where(w => w.Id == workId).Select(w => w.StageId).FirstOrDefaultAsync();
        if (stageId == 0) throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        var p = await RequireByStageAsync(stageId);
        var stage = p.Stages.First(s => s.Id == stageId);
        var w = stage.Works.First(x => x.Id == workId);
        RemoveLinksOf(p, new HashSet<int> { w.Id });
        stage.Works.Remove(w);
        _db.WorkItems.Remove(w);
        p.Activities.Add(NewActivity(ActivityType.WorkRemoved, $"[{stage.Name}] 작업 삭제: {w.Title}", stageId: stage.Id));
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // ── 댓글 ─────────────────────────────────────────────────────────────

    public async Task CommentAsync(CommentInput input)
    {
        var text = Text(input.Text, 1000, "내용", "text", required: true)!;
        var p = await RequireAsync(input.ProjectId);
        p.Activities.Add(NewActivity(ActivityType.Comment, text, stageId: input.StageId, workId: input.WorkId));
        Touch(p);
        await _db.SaveChangesAsync();
    }

    // ── 공통 ─────────────────────────────────────────────────────────────

    // 화면이 보낸 버전이 지금과 다르면, 그 사이 다른 사람이 같은 항목을 고친 것이다
    private static void CheckVersion(int? sent, int current, int projectId)
    {
        if (sent != null && sent.Value != current) throw new ConflictException(projectId);
    }

    private void Touch(Project p) => p.UpdatedAt = _clock.UtcNow;

    // 모든 단계가 끝나면 완료로, 완료 과제에 미완료 단계가 생기면 다시 진행으로
    private void SyncState(Project p)
    {
        var allDone = Rules.AllStagesDone(p);
        if (p.State == ProjectState.Active && allDone)
        {
            p.State = ProjectState.Done;
            p.Activities.Add(NewActivity(ActivityType.StateChanged, "상태 변경: 진행 → 완료 (모든 단계 완료)"));
        }
        else if (p.State == ProjectState.Done && !allDone)
        {
            p.State = ProjectState.Active;
            p.Activities.Add(NewActivity(ActivityType.StateChanged, "상태 변경: 완료 → 진행 (미완료 단계 있음)"));
        }
    }

    public Activity NewActivity(ActivityType type, string text, string? reason = null, int? stageId = null, int? workId = null,
                                string? oldValue = null, string? newValue = null, Guid? batch = null) => new()
    {
        Type = type,
        Text = text.Length > 1000 ? text[..1000] : text,
        Reason = reason,
        StageId = stageId,
        WorkItemId = workId,
        OldValue = oldValue,
        NewValue = newValue,
        BatchId = batch,
        UserId = _me.Id,
        At = _clock.UtcNow,
    };

    public static string Fmt(DateOnly? d) => d == null ? "미정" : d.Value.ToString("yyyy-MM-dd");

    // 입력 문자열 정리: 공백 제거, 빈 값은 null, 길이 검사
    private static string? Text(string? value, int max, string label, string field, bool required = false)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (value == null)
        {
            if (required) throw new RuleException($"{label}을(를) 입력해 주세요.", field);
            return null;
        }
        if (value.Length > max) throw new RuleException($"{label}은(는) {max}자 이내로 입력해 주세요.", field);
        return value;
    }
}
