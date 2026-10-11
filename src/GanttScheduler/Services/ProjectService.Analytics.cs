using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 지연 사유 분류 고르기(사유 시트의 '분류')
public record ReasonOption(int Id, string Name);

// 분석을 위해 활동에 덧붙이는 구조화된 값: 일정 변경 사유의 분류(Activity.ReasonCode), 진행률 변경의 이전·새 값(OldValue/NewValue)
public partial class ProjectService
{
    // 이 요청에서 고른 사유 분류 — 일정 변경 활동(NewActivity)에 붙인다. 한 메서드 안에서만 유효하도록 using 으로 풀어 준다
    private int? _reasonCode;

    // 사유 시트의 분류 목록 (사용 중인 것만, 관리 화면 순서)
    public async Task<List<ReasonOption>> DelayReasonsAsync() =>
        (await _db.DelayReasons.AsNoTracking().Where(r => r.IsActive).ToListAsync())
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(r => new ReasonOption(r.Id, r.Name)).ToList();

    // 분류는 선택 사항이다(null 이면 분류 없음). 고르면 사용 중인 분류여야 한다 — 사용 중지된 분류는 새 기록에 쓰지 않는다
    private async Task<IDisposable> UseReasonCodeAsync(int? code)
    {
        if (code != null && !await _db.DelayReasons.AnyAsync(r => r.Id == code.Value && r.IsActive))
            throw new RuleException("사유 분류를 다시 골라 주세요. 사용 중지되었거나 없는 분류입니다.", "reasonCode");
        var previous = _reasonCode;
        _reasonCode = code;
        return new Restore(() => _reasonCode = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // 진행률이 바뀐 작업 변경 활동에 이전·새 진행률을 숫자로 남긴다(문구는 그대로) — 주간 보고의 과제별 '진척 +n%p'(Reporting.ProgressGain)가 읽는다
    private static void SetProgressValues(Activity a, int oldProgress, int newProgress)
    {
        if (oldProgress == newProgress) return;
        a.OldValue = oldProgress.ToString(System.Globalization.CultureInfo.InvariantCulture);
        a.NewValue = newProgress.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
