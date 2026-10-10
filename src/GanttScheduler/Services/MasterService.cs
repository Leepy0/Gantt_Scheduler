using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 관리 화면 공통 노드 — 종류(kind)마다 쓰는 부가 필드만 채운다
public record MasterNode(
    int Id, int? ParentId, int Level, string Name, bool Active, int Sort,
    string? Country = null, int? OceanLeadDays = null, string? Kind = null,
    string? Org = null, int? OrgUnitId = null, bool? IsAdmin = null, DateOnly? Date = null, List<string>? Ips = null,
    DateOnly? LastSeen = null, List<UserIpView>? Addresses = null);

// 사용자에 연결된 접속 IP. LastSeen 은 한국 날짜, Current 는 지금 이 화면을 연 기기
public record UserIpView(string Ip, DateOnly LastSeen, bool Current);

// 관리 > 사용자: IP 직접 연결·해제
public record MasterIp(int UserId, string? Ip);

public record MasterSave(
    string Kind, int Level, int? Id, int? ParentId, string? Name,
    string? Country, int? OceanLeadDays, string? SubKind, int? OrgUnitId, bool? IsAdmin, DateOnly? Date);

public record MasterRef(string Kind, int Level, int Id, int Dir);

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
                var cats = await _db.Categories.AsNoTracking().Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems).ToListAsync();
                var nodes = new List<MasterNode>();
                foreach (var c in cats.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
                {
                    nodes.Add(new MasterNode(c.Id, null, 1, c.Name, c.IsActive, c.SortOrder, Kind: Views.Key(c.Kind)));
                    foreach (var t in c.StageTemplates.OrderBy(t => t.SortOrder).ThenBy(t => t.Id))
                    {
                        nodes.Add(new MasterNode(t.Id, c.Id, 2, t.Name, true, t.SortOrder, Kind: Views.Key(t.Kind)));
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
                            .Select(a => new UserIpView(a.Ip, KoreaClock.KstDate(a.LastSeenAt), a.Ip == _me.Ip)).ToList()))
                    .ToList();

            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }
    }

    public async Task SaveAsync(MasterSave input)
    {
        RequireAdmin();
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new RuleException("이름을 입력해 주세요.", "name");
        if (name.Length > 100) throw new RuleException("이름은 100자 이내로 입력해 주세요.", "name");

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
                e.Kind = ParseEnum(input.SubKind, CategoryKind.General);
                break;
            }

            case "category" when input.Level == 3:
            {
                if (name.Length > 200) throw new RuleException("체크 항목은 200자 이내로 입력해 주세요.", "name");
                TemplateCheckItem e;
                if (input.Id == null)
                {
                    if (input.ParentId == null) throw new RuleException("단계를 먼저 선택해 주세요.");
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
                    var siblings = await _db.StageTemplates.Where(t => t.CategoryId == input.ParentId.Value).ToListAsync();
                    e = new StageTemplate { CategoryId = input.ParentId.Value, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.StageTemplates.Add(e);
                }
                else e = await _db.StageTemplates.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                e.Kind = ParseEnum(input.SubKind, StageKind.Normal);
                break;
            }

            case "equipment":
            {
                EquipmentType e;
                if (input.Id == null)
                {
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

            case "holiday":
            {
                if (input.Date == null) throw new RuleException("날짜를 입력해 주세요.", "date");
                var date = input.Date.Value;
                var dup = await _db.Holidays.FirstOrDefaultAsync(h => h.Date == date);
                if (dup != null && dup.Id != input.Id) throw new RuleException("이미 등록된 날짜입니다.", "date");
                Holiday e;
                if (input.Id == null)
                {
                    e = new Holiday();
                    _db.Holidays.Add(e);
                }
                else e = await _db.Holidays.FirstOrDefaultAsync(t => t.Id == input.Id.Value) ?? throw NotFound();
                e.Name = name;
                e.Date = date;
                break;
            }

            case "org":
            {
                if (name.Length > 50) throw new RuleException("이름은 50자 이내로 입력해 주세요.", "name");
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
                if (name.Length > 50) throw new RuleException("이름은 50자 이내로 입력해 주세요.", "name");
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
                break;
            }

            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }

        await _db.SaveChangesAsync();
    }

    // 사용 중지/복구. 단계 템플릿과 공휴일은 과제가 참조하지 않으므로 바로 삭제한다
    public async Task ToggleAsync(MasterRef r)
    {
        RequireAdmin();
        switch (r.Kind)
        {
            case "location":
            {
                var e = await _db.Locations.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = !e.IsActive;
                break;
            }
            case "category" when r.Level == 1:
            {
                var e = await _db.Categories.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = !e.IsActive;
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
                e.IsActive = !e.IsActive;
                break;
            }
            case "equipment":
            {
                var e = await _db.EquipmentTypes.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = !e.IsActive;
                break;
            }
            case "container":
            {
                var e = await _db.ContainerTypes.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = !e.IsActive;
                break;
            }
            case "vendor":
            {
                var e = await _db.Vendors.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                e.IsActive = !e.IsActive;
                break;
            }
            case "holiday":
            {
                var e = await _db.Holidays.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                _db.Holidays.Remove(e);
                break;
            }
            case "user":
            {
                var e = await _db.Users.FirstOrDefaultAsync(x => x.Id == r.Id) ?? throw NotFound();
                if (e.Id == _me.Id) throw new RuleException("자기 자신은 사용 중지할 수 없습니다.");
                e.IsActive = !e.IsActive;
                break;
            }
            default:
                throw new RuleException("알 수 없는 관리 항목입니다.");
        }
        await _db.SaveChangesAsync();
    }

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

    private static int NextSort(IEnumerable<int> existing) => existing.DefaultIfEmpty(0).Max() + 1;

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private void RequireAdmin()
    {
        if (!_me.IsAdmin) throw new RuleException("관리자만 변경할 수 있습니다.");
    }

    private static RuleException NotFound() => new("항목을 찾을 수 없습니다. 화면을 새로고침해 주세요.");
}
