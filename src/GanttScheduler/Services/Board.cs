using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// 데이터 버전 — 과제 그래프(과제·단계·작업·체크·의존·과제 간 연결·첨부·활동)가 저장될 때마다 1 올리는 프로세스 안 카운터(싱글턴).
// 상황판이 1분마다 이 값만 물어 바뀌었을 때만 화면을 다시 받는다. 운영은 서버 한 대(Windows 서비스)라 프로세스 안 값으로 충분하다.
// 올리는 곳은 AppDb.SaveChangesAsync 한 곳이다 — 서비스마다 올리면 새 변경 경로에서 빠뜨리기 쉽다
public sealed class DataVersion
{
    // 시작 값을 시각으로 두어 서버를 다시 띄우면(업데이트 등) 값이 달라진다 — 새 버전의 화면을 한 번 다시 받게
    private long _value = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;

    public long Value => Interlocked.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);

    // 상황판에 보이는 값에 닿는 엔티티. 사람별 기록(마지막 접속·방문·관심·알림)과 기준 정보는 세지 않는다
    public static bool Watches(object entity) =>
        entity is Project or Stage or WorkItem or CheckItem or WorkLink or StageLink or Attachment or Activity;
}

// 상황판 한 줄: 지연(빨강) 또는 지연 위험(노랑) 과제와 이유 한 줄
public record BoardAlert(int Id, string Title, string Corporation, string Owner, bool Delayed, string Chip, string Reason);

// 이번 주 목표: 진행 과제의 미완료 단계 중 목표일이 오늘부터 Rules.SoonDays 일 안(대시보드 'n일 내 목표'와 같은 판정)
public record BoardDue(int ProjectId, string Project, string Stage, string Corporation, DateOnly Date, int DDay);

// 법인별 현황(대시보드 막대와 같은 범위 — 진행·보류 + 올해 완료). Risk: 그 법인의 지연 위험 과제 수
public record BoardCorp(int Id, string Name, int OnTrack, int Delayed, int Risk, int Done, int Paused, int Total);

// Stamp: 데이터 버전 + 한국 날짜. 날짜가 바뀌면 데이터가 그대로여도 지연 판정이 달라지므로 함께 본다
public record BoardView(DateOnly Today, string AsOf, string Stamp, Kpi Kpi, List<BoardAlert> Alerts, List<BoardDue> Week, List<BoardCorp> Corps);

// TV 상황판(/Board): 사람 없이 켜 두는 PC 가 보는 읽기 전용 요약. 계산은 대시보드(Dashboard.Build)를 그대로 쓴다 — 숫자가 대시보드와 같아야 한다
public class BoardService
{
    private readonly ProjectService _projects;
    private readonly IClock _clock;
    private readonly DataVersion _version;

    public BoardService(ProjectService projects, IClock clock, DataVersion version)
    {
        _projects = projects;
        _clock = clock;
        _version = version;
    }

    public string Stamp() => Stamp(_version.Value, _clock.Today);

    public static string Stamp(long version, DateOnly today) => $"{version}-{today:yyyyMMdd}";

    // 마지막으로 확인한 시각(한국 시간 HH:mm) — 데이터가 그대로인 날에도 화면이 살아 있음을 보이려고 Stamp 응답에 싣는다
    public string CheckedAt() => KoreaClock.ToKst(_clock.UtcNow).ToString("HH:mm");

    public async Task<BoardView> BuildAsync()
    {
        var today = _clock.Today;
        // 버전은 읽기 전에 잡는다 — 읽는 사이 저장이 끼어들면 다음 확인에서 다시 받게
        var stamp = Stamp();
        var all = await _projects.AllAsync(q => Dashboard.InScope(q, today), withLinks: true);
        var risk = new RiskContext(await _projects.CalendarAsync(), await _projects.AllCrossLinksAsync(), _clock.UtcNow);
        var dash = Dashboard.Build(all, await _projects.LocationsAsync(), today, new DashboardQuery(), risk);
        var riskViews = dash.Risk ?? new List<ProjectView>();
        var notes = await _projects.RecentNotesAsync(dash.Delayed.Take(Dashboard.NotesMax).Select(p => p.Id)
            .Concat(riskViews.Take(Dashboard.NotesMax).Select(p => p.Id)).ToList());

        // 지연(지연 큰 순) 다음에 위험(예상 지연 큰 순) — 대시보드 카드와 같은 순서
        var alerts = new List<BoardAlert>();
        foreach (var v in dash.Delayed)
        {
            var reason = notes.TryGetValue(v.Id, out var n) ? $"{n.Text} — {n.User}"
                : v.DelayedStage != null ? $"'{v.DelayedStage}' 단계 목표일이 {v.DelayDays}일 지났습니다" : "목표일이 지났습니다";
            alerts.Add(new BoardAlert(v.Id, v.Title, v.Corporation, v.Owner, true, $"D+{v.DelayDays}", reason));
        }
        foreach (var v in riskViews)
        {
            var why = string.Join(" · ", v.Risks.Select(r => r.Text));
            var reason = notes.TryGetValue(v.Id, out var n) ? $"{why} — {n.Text}" : why;
            alerts.Add(new BoardAlert(v.Id, v.Title, v.Corporation, v.Owner, false, v.RiskDays > 0 ? $"예상 +{v.RiskDays}일" : "위험", reason));
        }

        var locations = await _projects.LocationsAsync();
        var week = all.Where(p => p.State == ProjectState.Active)
            .SelectMany(p => p.Stages
                .Where(s => s.CompletedDate == null && s.TargetDate != null && Rules.IsSoon(Rules.DaysLeft(s.TargetDate.Value, today)))
                .Select(s => new BoardDue(p.Id, p.Title, s.Name, locations.Get(p.CorporationId)?.Name ?? "", s.TargetDate!.Value, Rules.DaysLeft(s.TargetDate.Value, today))))
            .OrderBy(d => d.Date).ThenBy(d => d.Project, StringComparer.Ordinal).ToList();

        var corps = dash.ByCorporation
            .Select(g => new BoardCorp(g.Id, g.Name, g.OnTrack, g.Delayed, riskViews.Count(v => v.CorporationId == g.Id), g.Done, g.Paused, g.Total))
            .ToList();

        return new BoardView(today, KoreaClock.ToKst(_clock.UtcNow).ToString("MM-dd HH:mm"), stamp, dash.Kpi, alerts, week, corps);
    }
}
