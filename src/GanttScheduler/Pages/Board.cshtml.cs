using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages;

// TV 상황판 — 회의실·사무실 PC 에 켜 두는 읽기 전용 요약. 이름 등록 없이 열린다(UserGateMiddleware).
// 1분마다 ?handler=Stamp 로 데이터 버전만 묻고, 바뀌었을 때만 화면을 다시 받는다(board.js)
public class BoardModel : PageModel
{
    private readonly BoardService _board;
    private readonly UpdateService _updates;

    public BoardModel(BoardService board, UpdateService updates)
    {
        _board = board;
        _updates = updates;
    }

    public BoardView Data { get; private set; } = null!;
    public string BuildId => _updates.Build.Id;

    public async Task OnGetAsync() => Data = await _board.BuildAsync();

    // 데이터 버전 + 날짜 + 실행 중인 빌드. 빌드가 바뀌면(업데이트) 화면이 스타일·스크립트까지 새로 받도록 페이지 전체를 다시 읽는다
    public IActionResult OnGetStamp() => new JsonResult(new { ok = true, stamp = _board.Stamp(), build = BuildId });
}
