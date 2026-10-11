using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 기준선 차수 이력 (규칙 12). 확정할 때마다 Baseline 한 차수를 더하고, Stage.BaselineDate 등 기존 열은 '최신 차수'의 사본으로 둔다.
// 상세 화면은 비교 기준(차수)을 골라 그 차수 대비 +n일을 볼 수 있다(서버가 기준값만 바꿔 같은 규칙으로 센다)
public partial class ProjectService
{
    // 과제의 기준선 차수 (최신 순)
    public async Task<List<BaselineView>> BaselinesAsync(int projectId) =>
        (await _db.Baselines.AsNoTracking().Where(b => b.ProjectId == projectId)
            .Select(b => new { b.No, b.At, User = b.User!.Name, b.Reason }).ToListAsync())
            .OrderByDescending(b => b.No)
            .Select(b => new BaselineView(b.No, b.At, b.User ?? "", b.Reason)).ToList();

    // 읽은(추적하지 않는) 그래프의 비교 기준을 차수 no 로 바꾼다. 최신 차수이거나 없는 차수면 그대로 두고 null 을 돌려준다
    private async Task<int?> CompareWithAsync(Project p, int? no, List<BaselineView> rounds)
    {
        if (no == null || rounds.Count == 0 || no == rounds[0].No) return null;
        var round = await _db.Baselines.AsNoTracking().Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.ProjectId == p.Id && b.No == no.Value);
        if (round == null) return null;
        Rules.UseBaseline(p, round.Items, round.At);
        return round.No;
    }

    // 확정 한 번 = 새 차수. 저장은 부른 쪽의 SaveChanges 에 맡긴다(기존 열 갱신·활동과 같은 저장)
    private async Task<Baseline> AddBaselineRoundAsync(Project p, string? reason)
    {
        var last = (await _db.Baselines.Where(b => b.ProjectId == p.Id).Select(b => b.No).ToListAsync()).DefaultIfEmpty(0).Max();
        var round = new Baseline { ProjectId = p.Id, No = last + 1, At = _clock.UtcNow, UserId = _me.Id, Reason = reason };
        round.Items.AddRange(Rules.BaselineItems(p));
        _db.Baselines.Add(round);
        return round;
    }
}

// 기존 데이터 이전: 차수 이력이 생기기 전에 확정한 과제(BaselineAt 은 있는데 차수가 없음)에 지금의 기준선 열로 1차를 만든다.
// 서버 시작 단계(StartupWork → DbInitializer)에서 돌고, 이미 차수가 있는 과제는 건너뛰므로 여러 번 돌아도 같다(멱등).
// 확정한 사람·사유는 마지막 '기준선 확정' 활동에서 가져오고, 없으면 과제 담당자로 둔다
public static class BaselineHistory
{
    public static async Task<int> EnsureFirstRoundsAsync(AppDb db)
    {
        var pending = await db.Projects.Where(p => p.BaselineAt != null && !db.Baselines.Any(b => b.ProjectId == p.Id))
            .Include(p => p.Stages).ThenInclude(s => s.Works)
            .AsSplitQuery().ToListAsync();
        foreach (var p in pending)
        {
            var last = await db.Activities.AsNoTracking()
                .Where(a => a.ProjectId == p.Id && a.Type == ActivityType.BaselineSet)
                .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
                .Select(a => new { a.UserId, a.Reason }).FirstOrDefaultAsync();
            var round = new Baseline { ProjectId = p.Id, No = 1, At = p.BaselineAt!.Value, UserId = last?.UserId ?? p.OwnerId, Reason = last?.Reason };
            foreach (var s in p.Stages)
            {
                round.Items.Add(new BaselineItem { StageId = s.Id, End = s.BaselineDate });
                foreach (var w in s.Works.Where(w => w.BaselineStart != null || w.BaselineEnd != null))
                    round.Items.Add(new BaselineItem { WorkItemId = w.Id, Start = w.BaselineStart, End = w.BaselineEnd });
            }
            db.Baselines.Add(round);
        }
        // 동기 저장 — 시작 단계에는 요청 범위의 저장 훅(활동 알림)이 없고, 활동도 만들지 않는다
        if (pending.Count > 0) db.SaveChanges();
        return pending.Count;
    }
}
