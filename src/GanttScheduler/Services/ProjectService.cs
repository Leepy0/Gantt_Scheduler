using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

public record ItemInput(int TypeId, int Quantity, string? Note);

public record ProjectInput(
    string? Title, int CategoryId, int LocationId, string? Owner, DateOnly? StartDate,
    string? Vendor, string? Description, List<ItemInput>? Equipments, List<ItemInput>? Containers);

public record StageInput(
    int ProjectId, int? Id, string? Name, DateOnly? TargetDate, DateOnly? CompletedDate, string? Reason, bool Cascade);

public record WorkInput(
    int? Id, int StageId, string? Title, string? Owner, string? CoOwner,
    DateOnly? PlanStart, DateOnly? PlanEnd, DateOnly? ActualStart, DateOnly? ActualEnd,
    int Progress, string? Note, string? Reason);

public record CommentInput(int ProjectId, int? StageId, int? WorkId, string? Text);

public record HolidayView(DateOnly Date, string Name);

// 페이지에 심는 JSON 직렬화 옵션 (camelCase)
public static class AppJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);
    public static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value, Web);
}

// 과제·단계·작업의 조회와 변경. 변경은 모두 활동 이력을 남긴다
public class ProjectService
{
    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;
    private readonly UserService _users;

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

    // 담당자 자동완성용 이름 목록
    public async Task<List<string>> UserNamesAsync() =>
        (await _db.Users.AsNoTracking().ToListAsync()).Where(u => u.IsActive).Select(u => u.Name).OrderBy(n => n).ToList();

    private IQueryable<Project> Graph() => _db.Projects
        .Include(p => p.Category)
        .Include(p => p.Owner)
        .Include(p => p.Stages).ThenInclude(s => s.Works).ThenInclude(w => w.Owner)
        .Include(p => p.Stages).ThenInclude(s => s.Works).ThenInclude(w => w.CoOwner);

    // 전체 과제(단계·작업 포함) — 대시보드/목록 집계용. 규모가 작아 메모리에서 계산한다
    public async Task<List<Project>> AllAsync() =>
        await Graph().AsNoTracking().AsSplitQuery().ToListAsync();

    public async Task<Project?> LoadAsync(int id) =>
        await Graph()
            .Include(p => p.CreatedBy)
            .Include(p => p.Equipments).ThenInclude(e => e.EquipmentType).ThenInclude(t => t!.Parent)
            .Include(p => p.Containers).ThenInclude(c => c.ContainerType)
            .Include(p => p.Activities).ThenInclude(a => a.User)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<ProjectView?> ViewAsync(int id)
    {
        var p = await LoadAsync(id);
        return p == null ? null : Views.Project(p, _clock.Today, await LocationsAsync(), detail: true);
    }

    private async Task<Project> RequireAsync(int id) =>
        await LoadAsync(id) ?? throw new RuleException("과제를 찾을 수 없습니다. 삭제되었을 수 있습니다.");

    // ── 과제 ─────────────────────────────────────────────────────────────

    public async Task<int> CreateAsync(ProjectInput input)
    {
        var title = Text(input.Title, 200, "과제명", "title", required: true)!;
        var category = await _db.Categories.Include(c => c.StageTemplates).FirstOrDefaultAsync(c => c.Id == input.CategoryId)
            ?? throw new RuleException("과제 유형을 선택해 주세요.", "categoryId");
        var (corp, loc) = await ResolveLocationAsync(input.LocationId);
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

        // 단계 템플릿을 그 시점 그대로 복사 — 이후 템플릿이 바뀌어도 기존 과제는 영향 없음
        foreach (var t in category.StageTemplates.OrderBy(t => t.SortOrder))
            p.Stages.Add(new Stage { Name = t.Name, Kind = t.Kind, SortOrder = t.SortOrder });

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
        var owner = await _users.ResolveAsync(input.Owner);
        var p = await RequireAsync(id);

        var changed = new List<string>();
        if (p.Title != title) { changed.Add("과제명"); p.Title = title; }
        if (p.LocationId != loc.Id) { changed.Add("위치"); p.CorporationId = corp.Id; p.LocationId = loc.Id; }
        if (owner != null && p.OwnerId != owner.Id) { changed.Add($"담당자 → {owner.Name}"); p.OwnerId = owner.Id; }
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
        p.UpdatedAt = _clock.UtcNow;
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

    public async Task SetStateAsync(int id, ProjectState state, string? reason)
    {
        var p = await RequireAsync(id);
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
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var p = await RequireAsync(id);
        if (!_me.IsAdmin && p.OwnerId != _me.Id && p.CreatedById != _me.Id)
            throw new RuleException("과제 삭제는 담당자·등록자 또는 관리자만 할 수 있습니다.");
        _db.Projects.Remove(p);
        await _db.SaveChangesAsync();
    }

    // ── 단계 ─────────────────────────────────────────────────────────────

    public async Task SaveStageAsync(StageInput input)
    {
        var name = Text(input.Name, 50, "단계명", "name", required: true)!;
        var reason = Text(input.Reason, 500, "사유", "reason");
        var p = await RequireAsync(input.ProjectId);

        if (input.Id == null)
        {
            var order = p.Stages.Count == 0 ? 1 : p.Stages.Max(s => s.SortOrder) + 1;
            p.Stages.Add(new Stage { Name = name, SortOrder = order, TargetDate = input.TargetDate, CompletedDate = input.CompletedDate });
            p.Activities.Add(NewActivity(ActivityType.StageAdded, $"단계 추가: {name}"));
        }
        else
        {
            var s = p.Stages.FirstOrDefault(x => x.Id == input.Id.Value)
                ?? throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.");

            if (s.Name != name)
            {
                p.Activities.Add(NewActivity(ActivityType.Edited, $"단계명 변경: {s.Name} → {name}", stageId: s.Id));
                s.Name = name;
            }

            if (s.TargetDate != input.TargetDate)
            {
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

                    // 연쇄 이동: 이후 미완료 단계의 목표일을 같은 일수만큼 옮긴다
                    if (input.Cascade && input.TargetDate != null)
                    {
                        var delta = input.TargetDate.Value.DayNumber - old.DayNumber;
                        foreach (var later in p.Stages.Where(x => x.SortOrder > s.SortOrder && x.CompletedDate == null && x.TargetDate != null))
                        {
                            var from = later.TargetDate!.Value;
                            later.TargetDate = from.AddDays(delta);
                            p.Activities.Add(NewActivity(ActivityType.TargetChanged, $"[{later.Name}] 목표일 {Fmt(from)} → {Fmt(later.TargetDate)} (연쇄 이동)", reason,
                                later.Id, oldValue: Fmt(from), newValue: Fmt(later.TargetDate), batch: batch));
                        }
                    }
                }
            }

            if (s.CompletedDate != input.CompletedDate)
            {
                if (s.CompletedDate == null)
                    p.Activities.Add(NewActivity(ActivityType.StageCompleted, $"[{s.Name}] 완료 ({Fmt(input.CompletedDate)})", stageId: s.Id, newValue: Fmt(input.CompletedDate)));
                else if (input.CompletedDate == null)
                    p.Activities.Add(NewActivity(ActivityType.StageReopened, $"[{s.Name}] 완료 취소", stageId: s.Id, oldValue: Fmt(s.CompletedDate)));
                else
                    p.Activities.Add(NewActivity(ActivityType.Edited, $"[{s.Name}] 완료일 {Fmt(s.CompletedDate)} → {Fmt(input.CompletedDate)}", stageId: s.Id));
                s.CompletedDate = input.CompletedDate;
            }
        }

        SyncState(p);
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
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
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task<int> DeleteStageAsync(int stageId)
    {
        var p = await RequireByStageAsync(stageId);
        var s = p.Stages.First(x => x.Id == stageId);
        var works = s.Works.Count;
        p.Stages.Remove(s);
        _db.Stages.Remove(s);
        p.Activities.Add(NewActivity(ActivityType.StageRemoved, works > 0 ? $"단계 삭제: {s.Name} (작업 {works}건 포함)" : $"단계 삭제: {s.Name}"));
        SyncState(p);
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<Project> RequireByStageAsync(int stageId)
    {
        var projectId = await _db.Stages.Where(s => s.Id == stageId).Select(s => s.ProjectId).FirstOrDefaultAsync();
        if (projectId == 0) throw new RuleException("단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        return await RequireAsync(projectId);
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

        // 사용자 확인(필요 시 생성)은 엔티티를 건드리기 전에 끝낸다
        var owner = await _users.ResolveAsync(input.Owner);
        var coOwner = await _users.ResolveAsync(input.CoOwner);
        if (owner != null && coOwner != null && owner.Id == coOwner.Id) coOwner = null;

        var p = await RequireByStageAsync(input.StageId);
        var stage = p.Stages.First(s => s.Id == input.StageId);

        var progress = input.Progress;
        var actualStart = input.ActualStart;
        if (input.ActualEnd != null) progress = 100;                          // 종료일이 있으면 100%
        else if (progress > 0 && actualStart == null) actualStart = _clock.Today; // 진행률만 올렸으면 오늘 시작한 것으로

        if (input.Id == null)
        {
            stage.Works.Add(new WorkItem
            {
                Title = title, OwnerId = owner?.Id, CoOwnerId = coOwner?.Id, Progress = progress,
                PlanStart = input.PlanStart.Value, PlanEnd = input.PlanEnd.Value,
                ActualStart = actualStart, ActualEnd = input.ActualEnd, Note = note, CreatedAt = _clock.UtcNow,
            });
            p.Activities.Add(NewActivity(ActivityType.WorkAdded, $"[{stage.Name}] 작업 추가: {title}", stageId: stage.Id));
        }
        else
        {
            var w = p.Stages.SelectMany(s => s.Works).FirstOrDefault(x => x.Id == input.Id.Value)
                ?? throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");

            if (w.PlanStart != input.PlanStart.Value || w.PlanEnd != input.PlanEnd.Value)
            {
                if (reason == null) throw new RuleException("목표 일정을 바꿀 때는 변경 사유를 입력해 주세요.", "reason");
                p.Activities.Add(NewActivity(ActivityType.WorkPlanChanged,
                    $"[{title}] 목표 일정 {Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)} → {Fmt(input.PlanStart)}~{Fmt(input.PlanEnd)}", reason,
                    stage.Id, w.Id, $"{Fmt(w.PlanStart)}~{Fmt(w.PlanEnd)}", $"{Fmt(input.PlanStart)}~{Fmt(input.PlanEnd)}"));
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
            if (w.OwnerId != owner?.Id) { changed.Add($"담당 → {owner?.Name ?? "없음"}"); w.OwnerId = owner?.Id; w.Owner = owner; }
            if (w.CoOwnerId != coOwner?.Id) { changed.Add($"부담당 → {coOwner?.Name ?? "없음"}"); w.CoOwnerId = coOwner?.Id; w.CoOwner = coOwner; }
            if (w.Progress != progress) { changed.Add($"진행률 {w.Progress}% → {progress}%"); w.Progress = progress; }
            if (w.ActualStart != actualStart) { changed.Add($"실적 시작 {Fmt(actualStart)}"); w.ActualStart = actualStart; }
            if (w.ActualEnd != input.ActualEnd) { changed.Add(input.ActualEnd == null ? "실적 종료 취소" : $"실적 종료 {Fmt(input.ActualEnd)}"); w.ActualEnd = input.ActualEnd; }
            if (w.Note != note) { changed.Add("메모"); w.Note = note; }

            if (changed.Count > 0)
                p.Activities.Add(NewActivity(ActivityType.WorkEdited, $"[{title}] " + string.Join(", ", changed), stageId: stage.Id, workId: w.Id));
        }

        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return p.Id;
    }

    public async Task<int> DeleteWorkAsync(int workId)
    {
        var stageId = await _db.WorkItems.Where(w => w.Id == workId).Select(w => w.StageId).FirstOrDefaultAsync();
        if (stageId == 0) throw new RuleException("작업을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
        var p = await RequireByStageAsync(stageId);
        var stage = p.Stages.First(s => s.Id == stageId);
        var w = stage.Works.First(x => x.Id == workId);
        stage.Works.Remove(w);
        _db.WorkItems.Remove(w);
        p.Activities.Add(NewActivity(ActivityType.WorkRemoved, $"[{stage.Name}] 작업 삭제: {w.Title}", stageId: stage.Id));
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // ── 댓글 ─────────────────────────────────────────────────────────────

    public async Task CommentAsync(CommentInput input)
    {
        var text = Text(input.Text, 1000, "내용", "text", required: true)!;
        var p = await RequireAsync(input.ProjectId);
        p.Activities.Add(NewActivity(ActivityType.Comment, text, stageId: input.StageId, workId: input.WorkId));
        p.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── 공통 ─────────────────────────────────────────────────────────────

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

    private Activity NewActivity(ActivityType type, string text, string? reason = null, int? stageId = null, int? workId = null,
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
