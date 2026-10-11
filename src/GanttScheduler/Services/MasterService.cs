using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 관리 화면 공통 노드 — 종류(kind)마다 쓰는 부가 필드만 채운다.
// DefaultWorkDays·Lead: 단계 템플릿의 기본 기간과 그 옆에 보이는 과거 리드타임 힌트(Reporting.StageLeadHints — 표본이 적으면 null)
public record MasterNode(
    int Id, int? ParentId, int Level, string Name, bool Active, int Sort,
    string? Country = null, int? OceanLeadDays = null, string? Kind = null,
    string? Org = null, int? OrgUnitId = null, bool? IsAdmin = null, DateOnly? Date = null, List<string>? Ips = null,
    DateOnly? LastSeen = null, List<UserIpView>? Addresses = null,
    int? DefaultWorkDays = null, StageLeadHint? Lead = null, string? Email = null);

// 사용자에 연결된 접속 IP. LastSeen 은 한국 날짜, Current 는 지금 이 화면을 연 기기
public record UserIpView(string Ip, DateOnly LastSeen, bool Current);

// 관리 > 사용자: IP 직접 연결·해제
public record MasterIp(int UserId, string? Ip);

public record MasterSave(
    string Kind, int Level, int? Id, int? ParentId, string? Name,
    string? Country, int? OceanLeadDays, string? SubKind, int? OrgUnitId, bool? IsAdmin, DateOnly? Date,
    int? DefaultWorkDays = null, string? Email = null);

// Active: 사용 중지/다시 사용에서 원하는 상태. 서버는 뒤집지 않고 이 값으로 맞춘다(같으면 아무 일도 하지 않는다) —
// 두 번 누르거나 오래 열어 둔 화면에서 눌러도 반대로 동작하지 않게(퇴사자 사용 중지가 풀리는 일 등)
public record MasterRef(string Kind, int Level, int Id, int Dir, bool? Active = null);

// 기준 정보(계층·유형·단계 템플릿·체크리스트·기기·용기·협력사·조직·사용자·공휴일) 관리
public class MasterService
{
    public const int LocationDepth = 4;
    public const int OrgDepth = 2;

    private readonly AppDb _db;
    private readonly IClock _clock;
    private readonly CurrentUser _me;

    public MasterService(AppDb db, IClock clock, CurrentUser me)
    {
        _db = db;
        _clock = clock;
        _me = me;
    }

    // 마지막 변경의 안내 문구(관리 화면이 저장 응답과 함께 보인다) — 공휴일 변경의 일정 영향 등
    public string? Notice { get; private set; }

    public async Task<List<MasterNode>> ListAsync(string kind)
    {
        switch (kind)
        {
            case "location":
                return (await _db.Locations.AsNoTracking().ToListAsync())
                    .OrderBy(l => l.Level).ThenBy(l => l.SortOrder).ThenBy(l => l.Id)
                    .Select(l => new MasterNode(l.Id, l.ParentId, l.Level, l.Name, l.IsActive, l.SortOrder,
                        Country: l.Level == 1 ? l.Country : null, OceanLeadDays: l.Level == 1 ? l.OceanLeadDays : null))
                    .ToList();

            case "category":
            {
                var cats = await _db.Categories.AsNoTracking().Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems).AsSplitQuery().ToListAsync();
                var hints = await LeadHintsAsync();
                var nodes = new List<MasterNode>();
                foreach (var c in cats.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
                {
                    nodes.Add(new MasterNode(c.Id, null, 1, c.Name, c.IsActive, c.SortOrder, Kind: Views.Key(c.Kind)));
                    foreach (var t in c.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id))
                    {
                        nodes.Add(new MasterNode(t.Id, c.Id, 2, t.Name, true, t.SortOrder, Kind: Views.Key(t.Kind),
                            DefaultWorkDays: t.DefaultWorkDays, Lead: hints.GetValueOrDefault((c.Id, t.Name))));
                        foreach (var k in t.CheckItems.OrderBy(k => k.SortOrder).ThenBy(k => k.Id))
                            nodes.Add(new MasterNode(k.Id, t.Id, 3, k.Text, true, k.SortOrder));
                    }
                }
                return nodes;
            }

            case "equipment":
                return (await _db.EquipmentTypes.AsNoTracking().ToListAsync())
                    .OrderBy(e => e.ParentId == null ? 1 : 2).ThenBy(e => e.SortOrder).ThenBy(e => e.Id)
                    .Select(e => new MasterNode(e.Id, e.ParentId, e.ParentId == null ? 1 : 2, e.Name, e.IsActive, e.SortOrder))
                    .ToList();

            case "container":
                return (await _db.ContainerTypes.AsNoTracking().ToListAsync())
                    .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
                    .Select(e => new MasterNode(e.Id, null, 1, e.Name, e.IsActive, e.SortOrder)).ToList();

            case "vendor":
                return (await _db.Vendors.AsNoTracking().ToListAsync())
                    .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
                    .Select(e => new MasterNode(e.Id, null, 1, e.Name, e.IsActive, e.SortOrder)).ToList();

            case "reason":
                return (await _db.DelayReasons.AsNoTracking().ToListAsync())
                    .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
                    .Select(e => new MasterNode(e.Id, null, 1, e.Name, e.IsActive, e.SortOrder)).ToList();

            case "holiday":
                return (await _db.Holidays.AsNoTracking().ToListAsync())
                    .OrderBy(h => h.Date)
                    .Select(h => new MasterNode(h.Id, null, 1, h.Name, true, 0, Date: h.Date)).ToList();

            case "org":
                return (await _db.OrgUnits.AsNoTracking().ToListAsync())
                    .OrderBy(o => o.Level).ThenBy(o => o.SortOrder).ThenBy(o => o.Id)
                    .Select(o => new MasterNode(o.Id, o.ParentId, o.Level, o.Name, o.IsActive, o.SortOrder))
                    .ToList();

            case "user":
                return (await _db.Users.AsNoTracking().Include(u => u.Addresses).Include(u => u.OrgUnit).ThenInclude(o => o!.Parent).ToListAsync())
                    .OrderBy(u => u.Name)
                    .Select(u => new MasterNode(u.Id, null, 1, u.Name, u.IsActive, 0, Org: Views.OrgName(u.OrgUnit), OrgUnitId: u.OrgUnitId,
                        IsAdmin: u.IsAdmin, Ips: u.Addresses.Select(a => a.Ip).OrderBy(x => x).ToList(),
                        LastSeen: u.Addresses.Count == 0 ? null : KoreaClock.KstDate(u.Addresses.Max(a => a.LastSeenAt)),
                        Addresses: u.Addresses.OrderByDescending(a => a.LastSeenAt)
                            .Select(a => new UserIpView(a.Ip, KoreaClock.KstDate(a.LastSeenAt), a.Ip == _me.Ip)).ToList(),
                        // 메일 주소는 관리자에게만 보인다
                        Email: _me.IsAdmin ? u.Email : null))
                    .ToList();

            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }
    }

    // 종류·단계별 이름 길이 상한 — DB 열 길이(Limits)와 같다. 관리 화면 입력칸 maxlength 도 이 값을 쓴다(Admin IndexModel.LimitsJson)
    public static int NameLimit(string kind, int level) => kind switch
    {
        "location" => Limits.LocationName,
        "category" => level switch { 1 => Limits.CategoryName, 2 => Limits.StageName, _ => Limits.CheckText },
        "equipment" => Limits.EquipmentName,
        "container" => Limits.ContainerName,
        "vendor" => Limits.VendorName,
        "org" => Limits.OrgName,
        "user" => Limits.UserName,
        "holiday" => Limits.HolidayName,
        "reason" => Limits.DelayReasonName,
        _ => Limits.CategoryName,
    };

    public static readonly string[] Kinds = { "location", "category", "equipment", "container", "vendor", "org", "user", "holiday", "reason" };

    public async Task SaveAsync(MasterSave input)
    {
        RequireAdmin();
        Notice = null;
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new RuleException("이름을 입력해 주세요.", "name");
        // DB 열 길이로 검사한다 — 화면은 받는데 SQL Server 만 거절해 500 이 나던 문제(SQLite 는 길이를 검사하지 않는다)
        var max = NameLimit(input.Kind, input.Level);
        if (name.Length > max)
            throw new RuleException(input.Kind == "category" && input.Level == 3 ? $"체크 항목은 {max}자 이내로 입력해 주세요." : $"이름은 {max}자 이내로 입력해 주세요.", "name");

        switch (input.Kind)
        {
            case "location":
            {
                Location e;
                if (input.Id == null)
                {
                    var level = 1;
                    if (input.ParentId != null)
                    {
                        var parent = await _db.Locations.FirstOrDefaultAsync(l => l.Id == input.ParentId.Value)
                            ?? throw new RuleException("상위 항목을 찾을 수 없습니다.");
                        level = parent.Level + 1;
                    }
                    if (level > LocationDepth) throw new RuleException("층 아래에는 항목을 추가할 수 없습니다.");
                    var siblings = (await _db.Locations.Where(l => l.ParentId == input.ParentId).ToListAsync());
                    e = new Location { ParentId = input.ParentId, Level = level, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.Locations.Add(e);
                }
                else
                {
                    e = await _db.Locations.FirstOrDefaultAsync(l => l.Id == input.Id.Value) ?? throw NotFound();
                }
                e.Name = name;
                if (e.Level == 1)
                {
                    e.Country = string.IsNullOrWhiteSpace(input.Country) ? null : input.Country.Trim();
                    if (e.Country?.Length > Limits.Country) throw new RuleException($"국가는 {Limits.Country}자 이내로 입력해 주세요.", "country");
                    var lead = input.OceanLeadDays ?? 0;
                    if (lead < 0 || lead > 365) throw new RuleException("해상 운송 소요일은 0~365 사이로 입력해 주세요.", "oceanLeadDays");
                    e.OceanLeadDays = lead;
                }
                break;
            }

            case "category" when input.Level == 1:
            {
                Category e;
                if (input.Id == null)
                {
                    var all = await _db.Categories.ToListAsync();
                    e = new Category { SortOrder = NextSort(all.Select(s => s.SortOrder)) };
                    _db.Categories.Add(e);
                }
                else e = await _db.Categories.FirstOrDefaultAsync(c => c.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                // 성격(내역 입력 방식)은 그 유형을 쓰는 과제가 있으면 바꿀 수 없다 — 바꾸면 과제의 기기·용기 내역이 화면에서 사라지고
                // 다음 저장 때 지워진다. 값이 없으면(이름만 고침) 지금 값을 그대로 둔다
                var kind = input.SubKind == null ? e.Kind : ParseEnum(input.SubKind, e.Kind);
                if (e.Id != 0 && kind != e.Kind && await _db.Projects.AnyAsync(p => p.CategoryId == e.Id))
                    throw new RuleException("이 유형을 쓰는 과제가 있어 내역 입력 방식을 바꿀 수 없습니다. 새 유형을 만들어 주세요.", "subKind");
                e.Kind = kind;
                break;
            }

            case "category" when input.Level == 3:
            {
                TemplateCheckItem e;
                if (input.Id == null)
                {
                    if (input.ParentId == null) throw new RuleException("단계를 먼저 선택해 주세요.");
                    // 다른 화면에서 지운 단계 템플릿에 더하면 FK 오류(500)가 난다 — 먼저 있는지 본다
                    await RequireParentAsync(_db.StageTemplates.Where(t => t.Id == input.ParentId.Value), "단계");
                    var siblings = await _db.TemplateCheckItems.Where(t => t.StageTemplateId == input.ParentId.Value).ToListAsync();
                    e = new TemplateCheckItem { StageTemplateId = input.ParentId.Value, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.TemplateCheckItems.Add(e);
                }
                else e = await _db.TemplateCheckItems.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Text = name;
                break;
            }

            case "category":
            {
                StageTemplate e;
                if (input.Id == null)
                {
                    if (input.ParentId == null) throw new RuleException("유형을 먼저 선택해 주세요.");
                    await RequireParentAsync(_db.Categories.Where(c => c.Id == input.ParentId.Value), "유형");
                    var siblings = await _db.StageTemplates.Where(t => t.CategoryId == input.ParentId.Value).ToListAsync();
                    e = new StageTemplate { CategoryId = input.ParentId.Value, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.StageTemplates.Add(e);
                }
                else e = await _db.StageTemplates.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                Rules.ValidateDefaultWorkDays(input.DefaultWorkDays);
                e.Name = name;
                // 성격 값이 없으면(이름만 고침) 지금 값을 둔다 — 예전에는 일반으로 되돌려 검수·입고 단계의 추천일이 사라졌다
                e.Kind = input.SubKind == null ? e.Kind : ParseEnum(input.SubKind, e.Kind);
                e.DefaultWorkDays = input.DefaultWorkDays;
                break;
            }

            case "equipment":
            {
                EquipmentType e;
                if (input.Id == null)
                {
                    // 하위 타입은 대분류 아래에만 (지운·하위인 상위에 더하지 않게)
                    if (input.ParentId != null)
                        await RequireParentAsync(_db.EquipmentTypes.Where(t => t.Id == input.ParentId.Value && t.ParentId == null), "대분류");
                    var siblings = await _db.EquipmentTypes.Where(t => t.ParentId == input.ParentId).ToListAsync();
                    e = new EquipmentType { ParentId = input.ParentId, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.EquipmentTypes.Add(e);
                }
                else e = await _db.EquipmentTypes.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                break;
            }

            case "container":
            {
                ContainerType e;
                if (input.Id == null)
                {
                    var all = await _db.ContainerTypes.ToListAsync();
                    e = new ContainerType { SortOrder = NextSort(all.Select(s => s.SortOrder)) };
                    _db.ContainerTypes.Add(e);
                }
                else e = await _db.ContainerTypes.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                break;
            }

            case "vendor":
            {
                Vendor e;
                if (input.Id == null)
                {
                    var all = await _db.Vendors.ToListAsync();
                    e = new Vendor { SortOrder = NextSort(all.Select(s => s.SortOrder)) };
                    _db.Vendors.Add(e);
                }
                else e = await _db.Vendors.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                break;
            }

            case "reason":
            {
                DelayReason e;
                if (input.Id == null)
                {
                    var all = await _db.DelayReasons.ToListAsync();
                    e = new DelayReason { SortOrder = NextSort(all.Select(s => s.SortOrder)) };
                    _db.DelayReasons.Add(e);
                }
                else e = await _db.DelayReasons.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                break;
            }

            case "holiday":
            {
                if (input.Date == null) throw new RuleException("날짜를 입력해 주세요.", "date");
                var date = input.Date.Value;
                if (date < Rules.MinDate || date > Rules.MaxDate)
                    throw new RuleException($"날짜는 {Rules.MinDate:yyyy-MM-dd} ~ {Rules.MaxDate:yyyy-MM-dd} 사이로 입력해 주세요.", "date");
                var dup = await _db.Holidays.FirstOrDefaultAsync(h => h.Date == date);
                if (dup != null && dup.Id != input.Id) throw new RuleException("이미 등록된 날짜입니다.", "date");
                var before = await HolidayDatesAsync();
                Holiday e;
                if (input.Id == null)
                {
                    e = new Holiday();
                    _db.Holidays.Add(e);
                }
                else e = await _db.Holidays.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                var changed = new HashSet<DateOnly> { date };
                if (e.Id != 0 && e.Date != date) changed.Add(e.Date);
                e.Name = name;
                e.Date = date;
                await _db.SaveChangesAsync();
                // 날짜가 그대로면(이름만 고침) 일정에는 영향이 없다
                Notice = changed.Count == 1 && before.Contains(date) ? null
                    : "공휴일을 저장했습니다. " + await HolidayImpactAsync(before, (await HolidayDatesAsync()), changed);
                return;
            }

            case "org":
            {
                OrgUnit e;
                if (input.Id == null)
                {
                    var level = 1;
                    if (input.ParentId != null)
                    {
                        var parent = await _db.OrgUnits.FirstOrDefaultAsync(o => o.Id == input.ParentId.Value)
                            ?? throw new RuleException("상위 항목을 찾을 수 없습니다.");
                        level = parent.Level + 1;
                    }
                    if (level > OrgDepth) throw new RuleException("파트 아래에는 항목을 추가할 수 없습니다.");
                    var siblings = await _db.OrgUnits.Where(o => o.ParentId == input.ParentId).ToListAsync();
                    e = new OrgUnit { ParentId = input.ParentId, Level = level, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.OrgUnits.Add(e);
                }
                else e = await _db.OrgUnits.FirstOrDefaultAsync(o => o.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                break;
            }

            case "user":
            {
                var dup = await _db.Users.FirstOrDefaultAsync(u => u.Name == name);
                if (dup != null && dup.Id != input.Id) throw new RuleException("같은 이름의 사용자가 이미 있습니다.", "name");
                AppUser e;
                if (input.Id == null)
                {
                    e = new AppUser { CreatedAt = _clock.UtcNow };
                    _db.Users.Add(e);
                }
                else e = await _db.Users.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                if (input.OrgUnitId != null && !await _db.OrgUnits.AnyAsync(o => o.Id == input.OrgUnitId.Value))
                    throw new RuleException("소속을 다시 선택해 주세요.", "orgUnitId");
                e.OrgUnitId = input.OrgUnitId;
                var admin = input.IsAdmin ?? e.IsAdmin;
                if (e.Id == _me.Id && e.Id != 0 && !admin) throw new RuleException("자기 자신의 관리자 권한은 해제할 수 없습니다.", "isAdmin");
                e.IsAdmin = admin;
                // 메일 요약 받는 주소: 관리자만 정한다(IP 로 식별하므로 본인 입력은 남의 알림을 가로챌 수 있다). null 은 그대로, 빈 값은 지움
                if (input.Email != null)
                {
                    var email = input.Email.Trim();
                    e.Email = email.Length == 0 ? null
                        : NotifyOptions.NormalizeEmail(email) ?? throw new RuleException("메일 주소를 확인해 주세요. (예: name@company.com)", "email");
                }
                break;
            }

            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }

        await _db.SaveChangesAsync();
    }

    // 사용 중지/다시 사용: 요청에 실린 원하는 상태(Active)로 맞춘다. 단계 템플릿·체크 항목·공휴일은 과제가 참조하지 않으므로 바로 삭제한다
    public async Task ToggleAsync(MasterRef r)
    {
        RequireAdmin();
        Notice = null;
        // 원하는 상태가 없으면 뒤집던 예전 동작 대신 거절한다 — 무엇으로 바꿀지 모르는 요청은 반대로 동작할 수 있다
        bool Wanted() => r.Active ?? throw new RuleException("화면을 새로고침한 뒤 다시 시도해 주세요.");
        switch (r.Kind)
        {
            case "location":
            {
                var e = await _db.Locations.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "category" when r.Level == 1:
            {
                var e = await _db.Categories.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "category" when r.Level == 3:
            {
                var e = await _db.TemplateCheckItems.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                _db.TemplateCheckItems.Remove(e);
                break;
            }
            case "category":
            {
                var e = await _db.StageTemplates.Include(x => x.CheckItems).FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                _db.StageTemplates.Remove(e);
                break;
            }
            case "org":
            {
                var e = await _db.OrgUnits.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "equipment":
            {
                var e = await _db.EquipmentTypes.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "container":
            {
                var e = await _db.ContainerTypes.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "vendor":
            {
                var e = await _db.Vendors.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "reason":
            {
                var e = await _db.DelayReasons.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = Wanted();
                break;
            }
            case "holiday":
            {
                var e = await _db.Holidays.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var before = await HolidayDatesAsync();
                _db.Holidays.Remove(e);
                await _db.SaveChangesAsync();
                Notice = "공휴일을 삭제했습니다. " + await HolidayImpactAsync(before, before.Where(d => d != e.Date).ToList(), new[] { e.Date });
                return;
            }
            case "user":
            {
                var e = await _db.Users.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var wanted = Wanted();
                if (e.Id == _me.Id && !wanted) throw new RuleException("자기 자신은 사용 중지할 수 없습니다.");
                e.IsActive = wanted;
                break;
            }
            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }
        await _db.SaveChangesAsync();
    }

    // ── 공휴일: 여러 줄 붙여넣기, 변경 영향 안내 ──

    // 한 줄에 '날짜 이름' 하나 (날짜와 이름은 TAB·공백으로 나눈다 — Excel·정부 발표 표를 그대로 붙여 넣을 수 있게).
    // 날짜는 2028-01-01·2028.1.1·2028/01/01·2028년 1월 1일. 한 줄이라도 읽지 못하면 아무것도 넣지 않고 그 줄을 알린다.
    // 이미 있는 날짜는 건너뛴다(관리자가 고친 이름을 덮지 않게)
    public const int MaxPasteLines = 400;
    private static readonly System.Text.RegularExpressions.Regex PasteLine = new(
        @"^(?<y>\d{4})\s*(?:[-./]|년)\s*(?<m>\d{1,2})\s*(?:[-./]|월)\s*(?<d>\d{1,2})\s*(?:일|\.)?(?:\s*\([^)]*\))?\s+(?<name>\S.*)$");

    public async Task PasteHolidaysAsync(string? text)
    {
        RequireAdmin();
        var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) throw new RuleException("붙여 넣을 내용을 입력해 주세요. 한 줄에 '2028-01-01 신정'처럼 날짜와 이름 하나씩입니다.", "text");
        if (lines.Count > MaxPasteLines) throw new RuleException($"한 번에 {MaxPasteLines}줄까지 넣을 수 있습니다.", "text");

        var parsed = new List<(DateOnly Date, string Name)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = PasteLine.Match(lines[i]);
            var date = default(DateOnly);
            var ok = m.Success && DateOnly.TryParseExact($"{m.Groups["y"].Value}-{int.Parse(m.Groups["m"].Value):00}-{int.Parse(m.Groups["d"].Value):00}",
                "yyyy-MM-dd", out date) && date >= Rules.MinDate && date <= Rules.MaxDate;
            if (!ok)
                throw new RuleException($"{i + 1}번째 줄을 읽지 못했습니다: '{Short(lines[i])}'. '2028-01-01 신정'처럼 날짜와 이름을 TAB 이나 공백으로 나눠 주세요.", "text");
            var name = m.Groups["name"].Value.Trim();
            if (name.Length > Limits.HolidayName) throw new RuleException($"{i + 1}번째 줄: 이름은 {Limits.HolidayName}자 이내로 입력해 주세요.", "text");
            if (parsed.Any(x => x.Date == date)) throw new RuleException($"{i + 1}번째 줄: {date:yyyy-MM-dd} 이(가) 두 번 들어 있습니다.", "text");
            parsed.Add((date, name));
        }

        var before = await HolidayDatesAsync();
        var existing = before.ToHashSet();
        var added = parsed.Where(x => !existing.Contains(x.Date)).ToList();
        foreach (var (date, name) in added) _db.Holidays.Add(new Holiday { Date = date, Name = name });
        await _db.SaveChangesAsync();
        var skipped = parsed.Count - added.Count;
        Notice = $"공휴일 {added.Count}건을 추가했습니다" + (skipped > 0 ? $"(이미 있는 날짜 {skipped}건은 건너뜀)" : "") + ". "
            + (added.Count == 0 ? "" : await HolidayImpactAsync(before, before.Concat(added.Select(x => x.Date)).ToList(), added.Select(x => x.Date).ToList()));
    }

    private static string Short(string line) => line.Length > 40 ? line[..40] + "…" : line;

    private async Task<List<DateOnly>> HolidayDatesAsync() => await _db.Holidays.AsNoTracking().Select(h => h.Date).ToListAsync();

    // 공휴일을 바꾼 날이 진행 과제의 일정에 걸리는지, 선행 관계를 새로 어기게 되는 작업이 있는지 (Rules.HolidayImpact).
    // 자동으로 일정을 밀지 않으므로 무엇을 확인해야 하는지 알린다
    private async Task<string> HolidayImpactAsync(List<DateOnly> before, List<DateOnly> after, IReadOnlyCollection<DateOnly> changed)
    {
        var oldCal = new WorkCalendar(before);
        var newCal = new WorkCalendar(after);
        var active = await _db.LiveProjects.AsNoTracking().Include(p => p.Stages).ThenInclude(s => s.Works).Include(p => p.Links)
            .Where(p => p.State == ProjectState.Active).AsSplitQuery().ToListAsync();
        var titles = new List<string>();
        var violations = 0;
        foreach (var p in active.OrderBy(p => p.Title))
        {
            var (touched, broken) = Rules.HolidayImpact(p, changed, oldCal, newCal);
            if (!touched && broken == 0) continue;
            titles.Add(p.Title);
            violations += broken;
        }
        LastImpact = (titles.Count, violations);
        if (titles.Count == 0) return "일정에 걸리는 진행 과제가 없습니다.";
        var names = string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? $" 외 {titles.Count - 3}건" : "");
        return $"영향받는 진행 과제 {titles.Count}건({names})" +
            (violations > 0 ? $" · 선행 관계를 어기게 된 작업 {violations}건 — 과제 상세에서 일정을 확인해 주세요." : " — 근무일 기간과 지연 일수가 바뀔 수 있습니다.");
    }

    // 마지막 공휴일 변경의 영향 (진행 과제 수, 새 의존 위반 작업 수) — 시험용
    internal (int Projects, int Violations)? LastImpact { get; private set; }

    // 관리자가 사용자에 IP 를 직접 연결한다. 관리자 이름에는 등록 화면에서 새 PC 를 붙일 수 없으므로(이름 사칭 방지),
    // PC 를 바꾼 관리자는 다른 관리자가 여기서 연결해 준다
    public async Task AddAddressAsync(MasterIp input)
    {
        RequireAdmin();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId) ?? throw NotFound();
        if (!AccessOptions.TryParseIp(input.Ip, out var parsed))
            throw new RuleException("IP 주소 형식이 아닙니다. 예: 10.1.2.3", "ip");
        // 접속할 때와 같은 모양으로 저장해야 찾을 수 있다
        var ip = UserGateMiddleware.NormalizeIp(parsed);
        var addr = await _db.UserAddresses.Include(a => a.User).FirstOrDefaultAsync(a => a.Ip == ip);
        if (addr != null && addr.UserId != user.Id)
            throw new RuleException($"이 IP 는 '{addr.User?.Name}' 님에 연결돼 있습니다. 먼저 그 연결을 해제해 주세요.", "ip");
        if (addr == null) _db.UserAddresses.Add(new UserAddress { Ip = ip, UserId = user.Id, LastSeenAt = _clock.UtcNow });
        else addr.LastSeenAt = _clock.UtcNow;
        await _db.SaveChangesAsync();
    }

    // IP 연결 해제 — 그 기기에서 다시 접속하면 이름을 새로 등록한다
    public async Task RemoveAddressAsync(MasterIp input)
    {
        RequireAdmin();
        var addr = await _db.UserAddresses.FirstOrDefaultAsync(a => a.UserId == input.UserId && a.Ip == input.Ip) ?? throw NotFound();
        // 자기 기기의 연결을 끊으면 관리자 이름으로 다시 연결할 수 없어 스스로 잠긴다
        if (addr.Ip == _me.Ip) throw new RuleException("지금 쓰고 있는 기기의 IP 는 해제할 수 없습니다.", "ip");
        _db.UserAddresses.Remove(addr);
        await _db.SaveChangesAsync();
    }

    // 같은 상위 안에서 순서 이동 (dir: -1 위로, +1 아래로)
    public async Task MoveAsync(MasterRef r)
    {
        RequireAdmin();
        switch (r.Kind)
        {
            case "location":
            {
                var e = await _db.Locations.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var siblings = await _db.Locations.Where(x => x.ParentId == e.ParentId).ToListAsync();
                Swap(siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "category" when r.Level == 1:
            {
                var all = await _db.Categories.ToListAsync();
                var e = all.FirstOrDefault(x => x.Id == r.Id) ?? throw NotFound();
                Swap(all.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "category" when r.Level == 3:
            {
                var e = await _db.TemplateCheckItems.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var siblings = await _db.TemplateCheckItems.Where(x => x.StageTemplateId == e.StageTemplateId).ToListAsync();
                Swap(siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "category":
            {
                var e = await _db.StageTemplates.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var siblings = await _db.StageTemplates.Where(x => x.CategoryId == e.CategoryId).ToListAsync();
                Swap(siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "org":
            {
                var e = await _db.OrgUnits.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var siblings = await _db.OrgUnits.Where(x => x.ParentId == e.ParentId).ToListAsync();
                Swap(siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "equipment":
            {
                var e = await _db.EquipmentTypes.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                var siblings = await _db.EquipmentTypes.Where(x => x.ParentId == e.ParentId).ToListAsync();
                Swap(siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "container":
            {
                var all = await _db.ContainerTypes.ToListAsync();
                var e = all.FirstOrDefault(x => x.Id == r.Id) ?? throw NotFound();
                Swap(all.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "vendor":
            {
                var all = await _db.Vendors.ToListAsync();
                var e = all.FirstOrDefault(x => x.Id == r.Id) ?? throw NotFound();
                Swap(all.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            case "reason":
            {
                var all = await _db.DelayReasons.ToListAsync();
                var e = all.FirstOrDefault(x => x.Id == r.Id) ?? throw NotFound();
                Swap(all.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList(), e, r.Dir, (x, v) => x.SortOrder = v);
                break;
            }
            default:
                throw new RuleException("순서를 바꿀 수 없는 항목입니다.");
        }
        await _db.SaveChangesAsync();
    }

    private static void Swap<T>(List<T> ordered, T item, int dir, Action<T, int> setSort) where T : class
    {
        var i = ordered.FindIndex(x => ReferenceEquals(x, item));
        var j = i + (dir < 0 ? -1 : 1);
        if (i < 0 || j < 0 || j >= ordered.Count) return;
        (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        for (var k = 0; k < ordered.Count; k++) setSort(ordered[k], k + 1);
    }

    // 단계 템플릿 기본 기간 옆의 참고값 — 완료된 과제의 같은 유형·같은 이름 단계 리드타임(근무일). 완료 과제만 읽는다
    private async Task<Dictionary<(int CategoryId, string Stage), StageLeadHint>> LeadHintsAsync()
    {
        var done = await _db.LiveProjects.AsNoTracking().Include(p => p.Stages).Where(p => p.State == ProjectState.Done).ToListAsync();
        var cal = new WorkCalendar((await _db.Holidays.AsNoTracking().ToListAsync()).Select(h => h.Date));
        return Reporting.StageLeadHints(done, cal);
    }

    private static int NextSort(IEnumerable<int> existing) => existing.DefaultIfEmpty(0).Max() + 1;

    // 하위 항목을 더하기 전에 상위가 아직 있는지 본다 — 다른 화면에서 지운 상위에 더하면 FK 오류로 500 이 난다
    private static async Task RequireParentAsync<T>(IQueryable<T> parent, string what)
    {
        if (!await parent.AnyAsync())
            throw new RuleException($"{what}을(를) 찾을 수 없습니다. 다른 화면에서 지웠을 수 있으니 새로고침해 주세요.");
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private void RequireAdmin()
    {
        if (!_me.IsAdmin) throw new RuleException("관리자만 변경할 수 있습니다.");
    }

    private static RuleException NotFound() => new("항목을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
}
