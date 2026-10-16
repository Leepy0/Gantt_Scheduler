using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GanttScheduler.Pages.Admin;

// 기준 정보 관리 — 탭별 데이터는 JSON 으로 주고받는다
public class IndexModel : PageModel
{
    private readonly MasterService _masters;
    private readonly CurrentUser _me;
    private readonly UserService _users;

    public IndexModel(MasterService masters, CurrentUser me, UserService users)
    {
        _masters = masters;
        _me = me;
        _users = users;
    }

    public bool IsAdmin => _me.IsAdmin;
    public string Ip => _me.Ip;
    // 관리자가 아무도 없다 — 서버에서 접속하거나 admin 명령을 써야 한다고 안내한다
    public bool NoAdmin { get; private set; }

    // 서버 PC 에서 접속할 주소. 첫 관리자는 루프백에서 온 등록만 되므로 서버 이름·IP 가 아니라 localhost 로 안내한다(포트는 지금 접속한 그대로)
    public string LocalUrl
    {
        get
        {
            var port = Request.Host.Port;
            var defaultPort = Request.IsHttps ? 443 : 80;
            return $"{Request.Scheme}://localhost{(port == null || port == defaultPort ? "" : ":" + port)}/";
        }
    }

    public async Task OnGetAsync() => NoAdmin = !await _users.HasAdminAsync();

    // 종류별·단계별 이름 길이 상한 (입력칸 maxlength) — 서버 검사·DB 열 길이와 같은 값(MasterService.NameLimit → Limits). 유형의 4 는 작업 템플릿
    public string LimitsJson => AppJson.Serialize(MasterService.Kinds.ToDictionary(k => k,
        k => Enumerable.Range(1, k switch { "location" => MasterService.LocationDepth, "category" => 4, "equipment" or "org" => 2, _ => 1 })
            .Select(level => MasterService.NameLimit(k, level)).ToArray()));

    // 이름 외 입력칸(국가·메일·IP)의 길이 상한 — 같은 이유로 Limits 에서 준다
    public string FieldLimitsJson => AppJson.Serialize(new { country = Limits.Country, email = Limits.Email, ip = Limits.Ip, reason = Limits.Reason });

    public Task<IActionResult> OnGetDataAsync(string kind) => Run(kind, () => Task.CompletedTask);

    public Task<IActionResult> OnPostSaveAsync([FromBody] MasterSave input) => Run(input.Kind, () => _masters.SaveAsync(input));

    public Task<IActionResult> OnPostToggleAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.ToggleAsync(input));

    public Task<IActionResult> OnPostMoveAsync([FromBody] MasterRef input) => Run(input.Kind, () => _masters.MoveAsync(input));

    public Task<IActionResult> OnPostAddIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.AddAddressAsync(input));

    // 공휴일 여러 줄 붙여넣기 (한 줄에 '날짜 이름')
    public record HolidayPaste(string? Text);
    public Task<IActionResult> OnPostPasteHolidaysAsync([FromBody] HolidayPaste input) => Run("holiday", () => _masters.PasteHolidaysAsync(input.Text));

    public Task<IActionResult> OnPostRemoveIpAsync([FromBody] MasterIp input) => Run("user", () => _masters.RemoveAddressAsync(input));

    // CSV 붙여넣기: 미리보기(apply=false) 또는 적용. 적용했으면 그 종류의 최신 목록도 함께 준다
    public async Task<IActionResult> OnPostPasteAsync([FromBody] MasterPasteInput input)
    {
        try
        {
            var result = await _masters.PasteAsync(input);
            if (result.Applied && input.Kind is "holiday" or "location") HttpContext.RequestServices.GetService<OpsAlerts>()?.Invalidate();
            return new JsonResult(new { ok = true, result, nodes = result.Applied ? await _masters.ListAsync(input.Kind) : null, notice = _masters.Notice });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // 종류별 샘플 CSV (복사용 JSON 또는 파일)
    public IActionResult OnGetPasteSample(string kind, bool download = false)
    {
        try
        {
            var today = HttpContext.RequestServices.GetRequiredService<IClock>().Today;
            var text = MasterService.PasteSample(kind, today);
            if (!download) return new JsonResult(new { ok = true, text, columns = MasterService.PasteColumns(kind) });
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray();
            return File(bytes, "text/csv; charset=utf-8", $"관리_{kind}_샘플.csv");
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // ── 사용자 정리·관리 변경 이력·사용 현황 (UserAdminService·MasterLog) ──
    // 서비스는 요청에서 꺼낸다 — 이 페이지를 쓰는 다른 핸들러의 생성자를 늘리지 않으려고
    private UserAdminService People => HttpContext.RequestServices.GetRequiredService<UserAdminService>();
    private MasterLog Changes => HttpContext.RequestServices.GetRequiredService<MasterLog>();

    // 사용자 병합: from 의 기록을 into 로 옮기고 from 을 지운다. 응답은 사용자 목록 + 안내 문구
    public Task<IActionResult> OnPostMergeAsync([FromBody] MergeInput input) => RunPeople(async () => (await People.MergeAsync(input)).Text);

    // 담당 이관 후 사용 중지
    public Task<IActionResult> OnPostReassignAsync([FromBody] ReassignInput input) => RunPeople(async () => (await People.ReassignAsync(input)).Text);

    // 관리 변경 이력(관리자만): 종류·기간·사람 조건, 쪽 나눔. 처음 부를 때(page 1) 고르기 목록(종류·사람)을 함께 준다
    public async Task<IActionResult> OnGetChangesAsync(string? kind, DateOnly? from, DateOnly? to, int? user, int page = 1)
    {
        try
        {
            var result = await Changes.ListAsync(new MasterChangeFilter(string.IsNullOrEmpty(kind) ? null : kind, from, to, user, page));
            var kinds = MasterLog.Kinds.Select(k => new { key = k.Key, label = k.Value });
            return new JsonResult(new { ok = true, page = result, kinds, people = await Changes.PeopleAsync() });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // 사용 현황: 조직별 집계(누구나), 사람별 마지막 접속(관리자만)
    public async Task<IActionResult> OnGetUsageAsync() => new JsonResult(new { ok = true, usage = await People.UsageAsync() });

    private async Task<IActionResult> RunPeople(Func<Task<string>> action)
    {
        try
        {
            var notice = await action();
            return new JsonResult(new { ok = true, nodes = await _masters.ListAsync("user"), notice });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public record MailInput(string? Email);

    // 관리 > 사용자의 '시험 메일 보내기': 메일 서버 설정(Notify:Smtp)이 맞는지 그 주소로 한 통 보내 본다(관리자만)
    public async Task<IActionResult> OnPostTestMailAsync([FromBody] MailInput input)
    {
        try
        {
            if (!_me.IsAdmin) throw new RuleException("관리자만 시험 메일을 보낼 수 있습니다.");
            var digest = HttpContext.RequestServices.GetRequiredService<DigestService>();
            await digest.SendTestAsync(input.Email, _me.Name, HttpContext.RequestAborted);
            return new JsonResult(new { ok = true });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    // 변경 실행 → 해당 종류의 최신 목록
    private async Task<IActionResult> Run(string kind, Func<Task> action)
    {
        try
        {
            await action();
            // 법인·내년 공휴일을 등록하면 상단 칩('첫 설정 필요'·'내년 공휴일 없음')이 몇 분 기다리지 않고 바로 사라지게 한다
            if (kind is "holiday" or "location") HttpContext.RequestServices.GetService<OpsAlerts>()?.Invalidate();
            // notice: 변경에 따른 안내(공휴일이 걸린 진행 과제·선행 위반 등) — 화면이 알림으로 보인다
            return new JsonResult(new { ok = true, nodes = await _masters.ListAsync(kind), notice = _masters.Notice });
        }
        catch (RuleException ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message, field = ex.Field }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
