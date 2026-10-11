using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 빠른 갱신(내 할 일·작업 시트 맨 위): 진행률·실적·진행 내용만 바꾼다.
// ActualStart·ActualEnd 가 null 이면 지금 값을 그대로 둔다 — 빠른 갱신에는 실적을 지우는 단추가 없고, 지우면 밀기 대상이 되어
// 선행 검사가 필요해지므로 작업 시트에서 한다. Note: 진행 내용 한 줄(작업 댓글로 남긴다). ClientToday: 화면(PC)의 오늘(Rules.UserToday)
public record WorkProgressInput(int WorkId, int? Version, int Progress, DateOnly? ActualStart = null, DateOnly? ActualEnd = null,
    string? Note = null, DateOnly? ClientToday = null);

public partial class ProjectService
{
    // 작업 진행률·실적·진행 내용 갱신. 진행률·실적 규칙은 작업 저장(SaveWorkAsync)과 같은 Rules.WorkActuals·ValidateActuals 를 쓴다.
    // 화면이 본 작업 버전을 확인하고(어긋나면 409) 바뀐 것이 있으면 버전을 올린다. 과제 Id 를 돌려준다
    public Task<int> UpdateWorkProgressAsync(int workId, int? version, int progress, DateOnly? actualStart = null, DateOnly? actualEnd = null,
                                             string? note = null, DateOnly? clientToday = null) =>
        UpdateWorkProgressAsync(new WorkProgressInput(workId, version, progress, actualStart, actualEnd, note, clientToday));

    public async Task<int> UpdateWorkProgressAsync(WorkProgressInput input)
    {
        var text = Text(input.Note, Limits.ActivityText, "진행 내용", "note");
        var stageId = await _db.WorkItems.Where(w => w.Id == input.WorkId).Select(w => (int?)w.StageId).FirstOrDefaultAsync()
            ?? throw new RuleException("작업을 찾을 수 없습니다. 삭제되었을 수 있습니다. 화면을 새로고침해 주세요.");
        var p = await RequireByStageAsync(stageId);
        var stage = p.Stages.First(s => s.Id == stageId);
        var w = stage.Works.First(x => x.Id == input.WorkId);
        CheckVersion(input.Version, w.Version, p.Id);

        var wantStart = input.ActualStart ?? w.ActualStart;
        var wantEnd = input.ActualEnd ?? w.ActualEnd;
        Rules.ValidateActuals(wantStart, wantEnd, input.Progress);
        var (progress, start) = Rules.WorkActuals(input.Progress, wantStart, wantEnd, input.ClientToday, _clock.Today);
        if (w.ActualStart != start) Rules.ValidateDate(start, p.StartDate, "실적 시작일", "actualStart");
        if (w.ActualEnd != wantEnd) Rules.ValidateDate(wantEnd, p.StartDate, "실적 종료일", "actualEnd");

        // 작업 저장과 같은 문구로 남긴다 — 변경 이력·주간 보고가 같은 활동으로 본다
        var changed = new List<string>();
        if (w.Progress != progress) { changed.Add($"진행률 {w.Progress}% → {progress}%"); w.Progress = progress; }
        if (w.ActualStart != start) { changed.Add($"실적 시작 {Fmt(start)}"); w.ActualStart = start; }
        if (w.ActualEnd != wantEnd) { changed.Add($"실적 종료 {Fmt(wantEnd)}"); w.ActualEnd = wantEnd; }
        if (changed.Count == 0 && text == null) return p.Id;      // 바뀐 것이 없으면 저장하지 않는다(버전도 그대로)

        if (changed.Count > 0)
        {
            var edited = NewActivity(ActivityType.WorkEdited, $"[{w.Title}] " + string.Join(", ", changed), stageId: stage.Id, workId: w.Id);
            p.Activities.Add(edited);
            InvolveOwners(edited, w.OwnerId, w.CoOwnerId);
            w.Version++;
        }
        if (text != null)
            p.Activities.Add(NewActivity(ActivityType.Comment, text, stageId: stage.Id, workId: w.Id));
        Touch(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }
}
