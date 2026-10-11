using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 조기 경보(대시보드·목록)가 쓰는 조회
public partial class ProjectService
{
    // 최근 사유를 찾는 기간 — 오래된 사유는 지금 상황을 설명하지 못하고, 큰 과제의 활동을 모두 읽지 않게 한다
    public const int RecentNoteDays = 60;

    // 모든 과제 간 단계 연결(양쪽 단계와 과제 제목까지). 대시보드·목록은 과제 수백 건의 단계 Id 를 조건으로 넘기지 않고
    // 연결 표 전체를 읽는다 — 연결은 과제 수보다 훨씬 적고, 긴 IN 목록은 SQL Server 매개 변수 한도에 걸린다
    public async Task<List<StageLink>> AllCrossLinksAsync()
    {
        var links = await _db.StageLinks.AsNoTracking().ToListAsync();
        if (links.Count == 0) return links;
        var ids = links.SelectMany(l => new[] { l.PredecessorStageId, l.SuccessorStageId }).Distinct().ToList();
        var stages = (await _db.Stages.AsNoTracking().Include(s => s.Project).Where(s => ids.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id);
        foreach (var l in links)
        {
            l.PredecessorStage = stages.GetValueOrDefault(l.PredecessorStageId);
            l.SuccessorStage = stages.GetValueOrDefault(l.SuccessorStageId);
        }
        // 휴지통에 간 과제의 연결은 지우지 않고 남겨 두므로(복원하면 돌아온다) 화면·위반 계산에서만 뺀다 — CrossLinksAsync 와 같은 규칙
        return links.Where(l => l.PredecessorStage?.Project is { DeletedAt: null } && l.SuccessorStage?.Project is { DeletedAt: null }).ToList();
    }

    // 과제마다 최근 사유 한 줄: 최근 RecentNoteDays 일 안에 사유가 붙은 변경 또는 (지우지 않은) 댓글 중 가장 최근 것.
    // 지연·위험 카드의 과제 Id 목록으로 한 번에 읽고 과제별 고르기는 메모리에서 한다(그룹 집계는 DB 로 옮기지 않는다 — DESIGN 6장)
    public async Task<Dictionary<int, RecentNote>> RecentNotesAsync(IReadOnlyCollection<int> projectIds)
    {
        var result = new Dictionary<int, RecentNote>();
        if (projectIds.Count == 0) return result;
        var ids = projectIds.Distinct().ToList();
        var since = _clock.UtcNow.AddDays(-RecentNoteDays);
        var rows = await _db.LiveActivities.AsNoTracking()
            .Where(a => ids.Contains(a.ProjectId) && a.At >= since
                        && ((a.Reason != null && a.Reason != "") || (a.Type == ActivityType.Comment && a.DeletedAt == null)))
            .Select(a => new { a.ProjectId, a.Type, a.Text, a.Reason, a.At, a.Id, User = a.User!.Name })
            .ToListAsync();
        foreach (var g in rows.GroupBy(a => a.ProjectId))
        {
            var a = g.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).First();
            var text = a.Type == ActivityType.Comment || string.IsNullOrEmpty(a.Reason) ? a.Text : a.Reason;
            result[g.Key] = new RecentNote(text, a.User ?? "", a.At);
        }
        return result;
    }
}
