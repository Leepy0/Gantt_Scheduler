using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

// 도움말 — 처음 쓰는 사람이 화면의 숫자·색·막힘을 이해하도록 핵심 개념을 짧게 모았다.
// 숫자(임박 일수·정체 일수 등)는 규칙 상수에서 읽어 규칙이 바뀌어도 문구가 어긋나지 않게 한다
public class HelpModel : PageModel
{
    // 절 목록(목차·다른 화면의 '?' 링크가 쓰는 이름표). 화면의 '?' 링크는 /Help#{Id} 로 온다
    public static readonly (string Id, string Title)[] Sections =
    {
        ("concepts", "과제 · 단계 · 작업"),
        ("state", "상태는 어떻게 정해지나"),
        ("reason", "목표일을 바꿀 때 사유"),
        ("cascade", "연쇄 이동"),
        ("workdays", "근무일"),
        ("links", "의존 관계 — 유형과 간격"),
        ("float", "여유와 임계 작업"),
        ("baseline", "기준선과 차수"),
        ("risk", "지연 위험 · 정체"),
        ("notify", "알림 · 관심 과제"),
        ("quick", "내 할 일에서 바로 갱신"),
        ("conflict", "동시 수정 충돌"),
        ("user", "사용자 식별(IP)과 사용자 바꾸기"),
        ("trash", "휴지통"),
        ("board", "TV 상황판"),
    };

    public static bool Has(string id) => Sections.Any(s => s.Id == id);

    public HelpModel(TrashOptions trash, AccessOptions access)
    {
        TrashKeepDays = trash.KeepDays;
        ReconfirmDays = access.ReconfirmDays;
    }

    // 운영 설정에 따라 달라지는 값(휴지통 보관 일수, IP 재확인 일수) — 0 이면 그 기능이 꺼져 있다
    public int TrashKeepDays { get; }
    public int ReconfirmDays { get; }

    public int SoonDays => Rules.SoonDays;
    public int StaleDays => Rules.StaleDays;
    public int RiskSlipDays => Rules.RiskSlipDays;

    public void OnGet() { }
}
