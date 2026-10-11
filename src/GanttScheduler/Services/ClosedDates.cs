using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 과제 종료일(Project.ClosedOn) 채우기 — 종료일 열이 생기기 전에 끝난 과제(완료·중단)에 한 번 값을 넣는다.
// 서버 시작 단계(StartupWork → DbInitializer)에서 부른다. 값이 이미 있는 과제는 건드리지 않으므로 몇 번 불러도 같다(멱등)
public static class ClosedDates
{
    // 중단 상태 변경 활동의 문구 끝('상태 변경: 진행 → 중단') — 중단 처리일을 찾는 데 쓴다
    private static readonly string DroppedSuffix = "→ " + Views.Label(ProjectState.Dropped);

    public static async Task<int> FillAsync(AppDb db, DateOnly today)
    {
        // 휴지통 과제도 채운다(복원하면 목록에 그대로 돌아오므로)
        var pending = await db.Projects
            .Where(p => p.ClosedOn == null && (p.State == ProjectState.Done || p.State == ProjectState.Dropped))
            .Include(p => p.Stages).AsSplitQuery().ToListAsync();
        var filled = 0;
        foreach (var p in pending)
        {
            DateOnly? closed;
            if (p.State == ProjectState.Dropped)
            {
                // 중단 처리일: 마지막 '… → 중단' 상태 변경 활동의 한국 날짜. 기록이 없으면(옛 데이터) 마지막 변경일
                var changes = await db.Activities.AsNoTracking()
                    .Where(a => a.ProjectId == p.Id && a.Type == ActivityType.StateChanged)
                    .Select(a => new { a.Text, a.At, a.Id }).ToListAsync();
                var last = changes.Where(a => a.Text.TrimEnd().EndsWith(DroppedSuffix, StringComparison.Ordinal))
                    .OrderByDescending(a => a.At).ThenByDescending(a => a.Id).FirstOrDefault();
                closed = Rules.ClosedOn(p, KoreaClock.KstDate(last?.At ?? p.UpdatedAt));
            }
            else closed = Rules.ClosedOn(p, today);   // 완료: 마지막 단계 완료일(단계가 덜 끝난 어긋난 기록이면 비워 둔다)

            if (closed == null) continue;
            p.ClosedOn = closed;
            filled++;
        }
        // 동기 저장 — 시작 단계에는 요청 범위의 저장 훅(활동 알림)이 없고, 활동도 만들지 않는다(기준선 차수 이전과 같다)
        if (filled > 0) db.SaveChanges();
        return filled;
    }
}
