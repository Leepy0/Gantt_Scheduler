using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 과제 간 단계 연결 추가. Role: 'pred' = 상대 단계(OtherStageId)가 이 단계(StageId)의 선행, 'succ' = 상대 단계가 후행
public record StageLinkInput(int StageId, int OtherStageId, string? Role, int LagDays);

public record StageLinkRemove(int Id, int ProjectId);

// 연결할 상대를 고르는 검색 결과 (과제 → 단계)
public record LinkTargetStage(int Id, string Name, DateOnly? TargetDate, DateOnly? CompletedDate);

public record LinkTargetProject(int Id, string Title, string State, List<LinkTargetStage> Stages);

// 과제 간 단계 의존 (StageLink). 자동으로 밀지 않고 위반만 계산한다(규칙 11) — 연결·해제는 두 과제 모두에 활동을 남긴다
public partial class ProjectService
{
    public const int LinkTargetLimit = 20;

    // 연결할 상대 과제 검색: 제목에 q 가 든 다른 과제(진행·보류 먼저)와 그 단계들. 과제 수가 적어 제목 비교는 메모리에서 한다
    // (SQLite 와 SQL Server 의 대소문자·정렬 차이를 피한다)
    public async Task<List<LinkTargetProject>> LinkTargetsAsync(int projectId, string? q)
    {
        var text = q?.Trim() ?? "";
        var rows = (await _db.LiveProjects.AsNoTracking().Where(p => p.Id != projectId)
                .Select(p => new { p.Id, p.Title, p.State, p.UpdatedAt }).ToListAsync())
            .Where(p => text.Length == 0 || p.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.State is ProjectState.Active or ProjectState.Hold ? 0 : 1).ThenByDescending(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Take(LinkTargetLimit).ToList();
        var ids = rows.Select(p => p.Id).ToList();
        var stages = await _db.Stages.AsNoTracking().Where(s => ids.Contains(s.ProjectId)).ToListAsync();
        return rows.Select(p => new LinkTargetProject(p.Id, p.Title, Views.Key(p.State),
            stages.Where(s => s.ProjectId == p.Id).OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(s => new LinkTargetStage(s.Id, s.Name, s.TargetDate, s.CompletedDate)).ToList())).ToList();
    }

    // 이 단계들에 걸린 과제 간 연결 (양쪽 단계와 그 과제 제목까지 읽어 둔다 — Views 가 상대 과제 기준으로 보인다)
    public async Task<List<StageLink>> CrossLinksAsync(IReadOnlyCollection<int> stageIds)
    {
        if (stageIds.Count == 0) return new();
        var links = await _db.StageLinks.AsNoTracking()
            .Where(l => stageIds.Contains(l.PredecessorStageId) || stageIds.Contains(l.SuccessorStageId)).ToListAsync();
        if (links.Count == 0) return links;
        var ids = links.SelectMany(l => new[] { l.PredecessorStageId, l.SuccessorStageId }).Distinct().ToList();
        var stages = (await _db.Stages.AsNoTracking().Include(s => s.Project).Where(s => ids.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id);
        foreach (var l in links)
        {
            l.PredecessorStage = stages.GetValueOrDefault(l.PredecessorStageId);
            l.SuccessorStage = stages.GetValueOrDefault(l.SuccessorStageId);
        }
        // 휴지통에 있는 과제와의 연결은 지우지 않고(복원하면 돌아온다) 보이지도 위반으로 세지도 않는다
        return links.Where(l => l.PredecessorStage?.Project?.DeletedAt == null && l.SuccessorStage?.Project?.DeletedAt == null).ToList();
    }

    // 다른 과제의 단계와 잇는다. 같은 과제끼리·같은 쌍·순환(과제 간 연결을 따라 되돌아옴)은 거절한다.
    // 두 과제 모두에 LinkChanged 활동을 남기고 변경 시각을 갱신한다(열려 있는 상대 과제 화면에 '바뀌었습니다' 띠가 뜨게). 이 단계의 과제 Id 를 돌려준다
    public async Task<int> AddStageLinkAsync(StageLinkInput input)
    {
        Rules.ValidateLag(input.LagDays, "lagDays");
        var role = input.Role?.Trim().ToLowerInvariant();
        if (role is not ("pred" or "succ")) throw new RuleException("연결 방향(선행·후행)을 골라 주세요.", "role");
        if (input.StageId == input.OtherStageId) throw new RuleException("같은 단계끼리는 연결할 수 없습니다.", "otherStageId");
        var (predId, succId) = role == "pred" ? (input.OtherStageId, input.StageId) : (input.StageId, input.OtherStageId);

        var stages = await _db.Stages.Include(s => s.Project).Where(s => s.Id == predId || s.Id == succId).ToListAsync();
        var pred = stages.FirstOrDefault(s => s.Id == predId);
        var succ = stages.FirstOrDefault(s => s.Id == succId);
        if (pred?.Project == null || succ?.Project == null || pred.Project.DeletedAt != null || succ.Project.DeletedAt != null)
            throw new RuleException("연결할 단계를 찾을 수 없습니다. 화면을 새로고침해 주세요.", "otherStageId");
        if (pred.ProjectId == succ.ProjectId)
            throw new RuleException("같은 과제의 단계는 연결하지 않습니다. 과제 안의 순서는 단계 목표일로 정합니다.", "otherStageId");

        var edges = await _db.StageLinks.AsNoTracking().Select(l => new { l.PredecessorStageId, l.SuccessorStageId }).ToListAsync();
        if (edges.Any(e => e.PredecessorStageId == predId && e.SuccessorStageId == succId))
            throw new RuleException("이미 연결된 단계입니다.", "otherStageId");
        if (Rules.Reaches(edges.Select(e => (e.PredecessorStageId, e.SuccessorStageId)), succId, predId))
            throw new RuleException($"'{succ.Project.Title}' 의 '{succ.Name}' 단계가 이미 '{pred.Project.Title}' 의 '{pred.Name}' 단계 앞에 이어져 있어 연결하면 순환합니다.", "otherStageId");

        _db.StageLinks.Add(new StageLink { PredecessorStageId = predId, SuccessorStageId = succId, LagDays = input.LagDays, CreatedAt = _clock.UtcNow });
        var lag = input.LagDays > 0 ? $" (간격 {input.LagDays}근무일)" : "";
        AddActivity(pred.Project, pred.Id, $"[{pred.Name}] 과제 간 후행 연결: {succ.Project.Title} · {succ.Name}{lag}");
        AddActivity(succ.Project, succ.Id, $"[{succ.Name}] 과제 간 선행 연결: {pred.Project.Title} · {pred.Name}{lag}");
        await _db.SaveChangesAsync();
        return input.StageId == predId ? pred.ProjectId : succ.ProjectId;
    }

    // 과제 간 연결을 끊는다. 두 과제 모두에 기록한다. 보던 과제(ProjectId)의 Id 를 돌려준다
    public async Task<int> RemoveStageLinkAsync(StageLinkRemove input)
    {
        var link = await _db.StageLinks.FirstOrDefaultAsync(l => l.Id == input.Id)
            ?? throw new RuleException("연결을 찾을 수 없습니다. 다른 사용자가 이미 끊었을 수 있습니다. 화면을 새로고침해 주세요.");
        var stages = await _db.Stages.Include(s => s.Project)
            .Where(s => s.Id == link.PredecessorStageId || s.Id == link.SuccessorStageId).ToListAsync();
        var pred = stages.First(s => s.Id == link.PredecessorStageId);
        var succ = stages.First(s => s.Id == link.SuccessorStageId);
        _db.StageLinks.Remove(link);
        AddActivity(pred.Project!, pred.Id, $"[{pred.Name}] 과제 간 후행 연결 해제: {succ.Project!.Title} · {succ.Name}");
        AddActivity(succ.Project!, succ.Id, $"[{succ.Name}] 과제 간 선행 연결 해제: {pred.Project!.Title} · {pred.Name}");
        await _db.SaveChangesAsync();
        return input.ProjectId;
    }

    // 단계·과제를 지우기 전에 그 단계들에 걸린 과제 간 연결을 지운다(FK 가 Restrict). 상대 과제에는 왜 끊겼는지 남긴다.
    // removedProject: 과제째 지우는 중이면 그 과제 — 지워질 과제에는 기록하지 않는다
    private async Task RemoveStageLinksAsync(Project p, IReadOnlyCollection<int> stageIds, bool removedProject)
    {
        if (stageIds.Count == 0) return;
        var links = await _db.StageLinks.Where(l => stageIds.Contains(l.PredecessorStageId) || stageIds.Contains(l.SuccessorStageId)).ToListAsync();
        if (links.Count == 0) return;
        var otherIds = links.SelectMany(l => new[] { l.PredecessorStageId, l.SuccessorStageId }).Where(id => !stageIds.Contains(id)).Distinct().ToList();
        var others = (await _db.Stages.Include(s => s.Project).Where(s => otherIds.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id);
        if (removedProject)
        {
            // 과제째 지울 때(DeleteAsync)는 단계를 읽지 않는다. 연결된 이 과제의 단계만 작업과 함께 추적해 읽어 둔다 — 이름을 쓰고,
            // EF 가 연결 → 작업 → 단계 → 과제 순서로 지우게 하기 위해서다. EF 는 추적하는 행 사이의 FK 로만 순서를 정하므로
            // 단계를 모르면 과제 행을 먼저 지우고, DB cascade 가 단계를 지우려다 연결의 Restrict 에 걸린다
            var ownIds = links.SelectMany(l => new[] { l.PredecessorStageId, l.SuccessorStageId }).Where(stageIds.Contains).Distinct().ToList();
            await _db.Stages.Include(s => s.Works).Where(s => ownIds.Contains(s.Id)).ToListAsync();
        }
        var mine = p.Stages.ToDictionary(s => s.Id);
        foreach (var l in links)
        {
            _db.StageLinks.Remove(l);
            var thisId = stageIds.Contains(l.PredecessorStageId) ? l.PredecessorStageId : l.SuccessorStageId;
            var otherId = thisId == l.PredecessorStageId ? l.SuccessorStageId : l.PredecessorStageId;
            var name = mine.TryGetValue(thisId, out var s) ? s.Name : "";
            if (others.TryGetValue(otherId, out var other) && other.Project != null)
                AddActivity(other.Project, other.Id,
                    $"[{other.Name}] 과제 간 연결 해제: {p.Title} · {name} " + (removedProject ? "(과제 삭제)" : "(단계 삭제)"));
            if (!removedProject && s != null)
                p.Activities.Add(NewActivity(ActivityType.LinkChanged, $"[{name}] 과제 간 연결 해제 (단계 삭제)", stageId: thisId));
        }
    }

    // 다른 과제(읽어 둔 엔티티)에 활동을 남기고 변경 시각을 갱신한다
    private void AddActivity(Project project, int stageId, string text)
    {
        var a = NewActivity(ActivityType.LinkChanged, text, stageId: stageId);
        a.ProjectId = project.Id;
        _db.Activities.Add(a);
        Touch(project);
    }
}
