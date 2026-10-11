using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 관리 변경 이력 화면 한 줄
public record MasterChangeView(long Id, DateTime At, string? User, string Kind, string KindName, int? EntityId, string Text, string? OldValue, string? NewValue);

// 조건: 종류·기간(한국 날짜, 끝 포함)·사람(UserId, 0 이면 서버)·쪽(1부터)
public record MasterChangeFilter(string? Kind = null, DateOnly? From = null, DateOnly? To = null, int? UserId = null, int Page = 1);

public record MasterChangePage(List<MasterChangeView> Items, int Total, int Page, int Pages, int PageSize);

// 관리 변경 이력(MasterChange)을 남기고 읽는 한 곳. 기준 정보(MasterService)·사용자(UserService·UserAdminService)·
// 업데이트 설정·admin 명령·IP 보존 정리가 같은 길로 남긴다.
// 변경과 같은 SaveChanges 로 저장한다(저장 훅) — 변경은 됐는데 이력이 빠지는(또는 그 반대) 일이 없게.
// 새 항목은 저장 전에 Id 가 없으므로 Id 를 함수로 받아 두었다가 저장 직전에 채우고, 그때도 0 이면 저장 뒤 한 번 더 채운다
public sealed class MasterLog : ISaveHook
{
    public const int PageSize = 50;

    // 종류 열쇠 → 화면 이름 (변경 이력 탭의 종류 고르기). 새 종류는 여기에 더한다
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["location"] = "계층",
        ["category"] = "유형",
        ["stage"] = "단계 템플릿",
        ["check"] = "체크 항목",
        ["templateWork"] = "작업 템플릿",
        ["equipment"] = "기기",
        ["container"] = "용기",
        ["vendor"] = "협력사",
        ["org"] = "조직",
        ["user"] = "사용자",
        ["ip"] = "IP 연결",
        ["holiday"] = "공휴일",
        ["reason"] = "지연 사유",
        ["update"] = "업데이트",
        ["backup"] = "백업",
    };

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser? _me;
    private readonly List<(MasterChange Row, Func<int?>? Id)> _pending = new();

    public MasterLog(AppDb db, IClock clock, CurrentUser? me = null)
    {
        _db = db;
        _clock = clock;
        _me = me;
        db.AddSaveHook(this);
    }

    public static string KindName(string kind) => Kinds.TryGetValue(kind, out var n) ? n : kind;

    // 한 줄 더하기. 저장은 호출한 쪽의 SaveChanges 가 한다.
    // actor: 행위자를 직접 정할 때(이름 등록 — 저장 전 새 사용자일 수 있다). 없으면 지금 사용자, 그도 없으면 서버(null)
    public MasterChange Add(string kind, Func<int?>? entityId, string text, string? oldValue = null, string? newValue = null, AppUser? actor = null)
    {
        var who = actor ?? _me?.User;
        var row = new MasterChange
        {
            At = _clock.UtcNow,
            Kind = kind,
            Text = Cut(text, Limits.ActivityText)!,
            OldValue = Cut(oldValue, Limits.ActivityOldValue),
            NewValue = Cut(newValue, Limits.ActivityOldValue),
        };
        if (who != null)
        {
            if (who.Id != 0) row.UserId = who.Id;
            else row.User = who;     // 저장 전 새 사용자 — EF 가 같은 저장에서 Id 를 맞춘다
        }
        _db.MasterChanges.Add(row);
        _pending.Add((row, entityId));
        return row;
    }

    public MasterChange Add(string kind, int? entityId, string text, string? oldValue = null, string? newValue = null, AppUser? actor = null) =>
        Add(kind, () => entityId, text, oldValue, newValue, actor);

    // 변경 없이 이력만 남길 때(업데이트·백업 — DB 밖 변경)
    public Task SaveAsync() => _db.SaveChangesAsync();

    private static string? Cut(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;

    public Task BeforeSaveAsync(CancellationToken ct)
    {
        foreach (var (row, id) in _pending)
            if (id != null) row.EntityId = Positive(id());
        return Task.CompletedTask;
    }

    public Task<bool> AfterSaveAsync(CancellationToken ct)
    {
        var again = false;
        foreach (var (row, id) in _pending)
        {
            if (id == null || row.EntityId != null || row.Id == 0) continue;
            var now = Positive(id());
            if (now == null) continue;
            row.EntityId = now;
            again = true;
        }
        _pending.Clear();
        return Task.FromResult(again);
    }

    private static int? Positive(int? id) => id is > 0 ? id : null;

    // 변경 이력 조회(관리자만). 최근 순, 쪽마다 PageSize 줄
    public async Task<MasterChangePage> ListAsync(MasterChangeFilter f)
    {
        if (_me?.IsAdmin != true) throw new RuleException("관리 변경 이력은 관리자만 볼 수 있습니다.");
        if (f.From != null && f.To != null && f.From > f.To) throw new RuleException("기간의 시작이 끝보다 늦습니다.", "from");
        var q = _db.MasterChanges.AsNoTracking();
        if (!string.IsNullOrEmpty(f.Kind)) q = q.Where(c => c.Kind == f.Kind);
        if (f.From != null)
        {
            var from = KoreaClock.StartOfDayUtc(f.From.Value);
            q = q.Where(c => c.At >= from);
        }
        if (f.To != null && f.To.Value < DateOnly.MaxValue)
        {
            var to = KoreaClock.StartOfDayUtc(f.To.Value.AddDays(1));
            q = q.Where(c => c.At < to);
        }
        if (f.UserId == 0) q = q.Where(c => c.UserId == null);
        else if (f.UserId != null) q = q.Where(c => c.UserId == f.UserId);

        var total = await q.CountAsync();
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        var page = Math.Clamp(f.Page, 1, pages);
        var rows = await q.OrderByDescending(c => c.At).ThenByDescending(c => c.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(c => new { c.Id, c.At, User = c.User == null ? null : c.User.Name, c.Kind, c.EntityId, c.Text, c.OldValue, c.NewValue })
            .ToListAsync();
        var items = rows.Select(c => new MasterChangeView(c.Id, c.At, c.User, c.Kind, KindName(c.Kind), c.EntityId, c.Text, c.OldValue, c.NewValue)).ToList();
        return new MasterChangePage(items, total, page, pages, PageSize);
    }

    // 사람 고르기 목록: 이력을 남긴 적이 있는 사용자
    public async Task<List<PickOption>> PeopleAsync()
    {
        var ids = await _db.MasterChanges.AsNoTracking().Where(c => c.UserId != null).Select(c => c.UserId!.Value).Distinct().ToListAsync();
        var users = await _db.Users.AsNoTracking().Select(u => new { u.Id, u.Name }).ToListAsync();
        return users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.Name, StringComparer.Ordinal)
            .Select(u => new PickOption(u.Id, u.Name)).ToList();
    }
}
