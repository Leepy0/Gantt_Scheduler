using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// 단계 순서 한 칸: 화면이 본 단계 버전과 함께 보낸다 (끌어 놓기·정렬)
public record StageOrderItem(int Id, int? Version = null);

public partial class ProjectService
{
    // 단계 순서를 통째로 바꾼다(끌어 놓기). 순서는 현재 단계 판정과 연쇄 이동 범위를 정하므로, 자리가 바뀐 단계의 버전을 확인하고 올린다.
    // 그 사이 단계가 늘거나 줄었으면 모르는 순서로 덮어쓰지 않게 409 로 알린다
    public async Task<int> ReorderStagesAsync(int projectId, List<StageOrderItem> order)
    {
        var p = await RequireAsync(projectId);
        var current = Rules.Ordered(p).ToList();
        if (order.Count != current.Count || order.Select(o => o.Id).Distinct().Count() != order.Count
            || order.Any(o => current.All(s => s.Id != o.Id)))
            throw new ConflictException(p.Id);
        var wanted = order.Select(o => current.First(s => s.Id == o.Id)).ToList();
        ApplyStageOrder(p, current, wanted, order.ToDictionary(o => o.Id, o => o.Version), "단계 순서 변경");
        await _db.SaveChangesAsync();
        return p.Id;
    }

    // by: end 종료(목표일)순, start 시작(작업 시작, 작업이 없으면 목표일)순. 날짜가 없는 단계는 뒤로, 같으면 지금 순서를 지킨다
    public async Task<int> SortStagesAsync(int projectId, string by)
    {
        if (by is not ("end" or "start")) throw new RuleException("정렬 기준을 알 수 없습니다.");
        var p = await RequireAsync(projectId);
        var current = Rules.Ordered(p).ToList();
        var wanted = (by == "end"
                ? current.OrderBy(s => s.TargetDate == null).ThenBy(s => s.TargetDate)
                : current.OrderBy(s => (Rules.WorksStart(s) ?? s.TargetDate) == null).ThenBy(s => Rules.WorksStart(s) ?? s.TargetDate)
                    .ThenBy(s => s.TargetDate == null).ThenBy(s => s.TargetDate))
            .ToList();
        ApplyStageOrder(p, current, wanted, null, by == "end" ? "단계 정렬: 종료일순" : "단계 정렬: 시작일순");
        await _db.SaveChangesAsync();
        return p.Id;
    }

    private void ApplyStageOrder(Project p, List<Stage> current, List<Stage> wanted, Dictionary<int, int?>? versions, string text)
    {
        var moved = wanted.Where((s, k) => current[k] != s).ToList();
        if (moved.Count == 0) return;
        if (versions != null)
            foreach (var s in moved) CheckVersion(versions.GetValueOrDefault(s.Id), s.Version, p.Id);
        for (var k = 0; k < wanted.Count; k++) wanted[k].SortOrder = k + 1;
        foreach (var s in moved) s.Version++;
        p.Activities.Add(NewActivity(ActivityType.Edited, $"{text}: {string.Join(" → ", wanted.Select(s => s.Name))}", stageId: moved[0].Id));
        Touch(p);
    }
}
