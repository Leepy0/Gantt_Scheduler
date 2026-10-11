using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Pages.Projects;

// 과제 등록·수정
public class EditModel : PageModel
{
    private readonly ProjectService _projects;
    private readonly AppDb _db;
    private readonly CurrentUser _me;
    private readonly IClock _clock;

    public EditModel(ProjectService projects, AppDb db, CurrentUser me, IClock clock)
    {
        _projects = projects;
        _db = db;
        _me = me;
        _clock = clock;
    }

    public class ItemRow
    {
        public int TypeId { get; set; }
        public int Quantity { get; set; }
        public string? Note { get; set; }
    }

    public class FormModel
    {
        public string? Title { get; set; }
        public int CategoryId { get; set; }
        public int LocationId { get; set; }
        public string? Owner { get; set; }
        public DateOnly? StartDate { get; set; }
        public string? Vendor { get; set; }
        public string? Description { get; set; }
        public int? Version { get; set; }       // 화면을 연 시점의 과제 버전 (동시 수정 감지)
        // 화면을 연 시점의 값(JSON). 다른 사람이 먼저 저장해 충돌했을 때 '그 사람이 바꾼 항목'만 골라 보여 주는 데만 쓴다
        public string? Base { get; set; }
        public List<ItemRow> Items { get; set; } = new();
    }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public FormModel Form { get; set; } = new();

    public bool IsNew => Id == null;
    public string? Error { get; private set; }
    public string? ErrorField { get; private set; }
    public string CategoryName { get; private set; } = "";
    public List<Category> Categories { get; private set; } = new();
    public List<string> UserNames { get; private set; } = new();
    public List<string> VendorNames { get; private set; } = new();
    public string MetaJson { get; private set; } = "{}";
    // 동시 수정 충돌 뒤: 다른 사용자가 바꾼 항목 → 그 값(표시용). 입력칸 옆에 보여 준다
    public Dictionary<string, string> Changed { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id == null)
        {
            Form.Owner = _me.Name;
            Form.StartDate = _clock.Today;
        }
        else
        {
            var p = await _projects.LoadAsync(Id.Value);
            if (p == null) return NotFound();
            Form = new FormModel
            {
                Title = p.Title,
                CategoryId = p.CategoryId,
                LocationId = p.LocationId,
                Owner = p.Owner?.Name,
                StartDate = p.StartDate,
                Vendor = p.Vendor,
                Description = p.Description,
                Version = p.Version,
                Base = AppJson.Serialize(Snapshot(p)),
                Items = (p.Category?.Kind == CategoryKind.Container
                    ? p.Containers.Select(c => new ItemRow { TypeId = c.ContainerTypeId, Quantity = c.Quantity, Note = c.Note })
                    : p.Equipments.Select(e => new ItemRow { TypeId = e.EquipmentTypeId, Quantity = e.Quantity, Note = e.Note })).ToList(),
            };
        }
        await LoadMetaAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // 모델 바인딩 오류(날짜 형식 등)는 규칙 검사 전에 알려 준다
        if (!ModelState.IsValid)
        {
            Error = "입력 값을 확인해 주세요.";
            await LoadMetaAsync();
            return Page();
        }

        var items = Form.Items.Select(i => new ItemInput(i.TypeId, i.Quantity, i.Note)).ToList();
        var input = new ProjectInput(Form.Title, Form.CategoryId, Form.LocationId, Form.Owner, Form.StartDate,
            Form.Vendor, Form.Description, items, items, Form.Version);
        try
        {
            var id = Id;
            if (id == null) id = await _projects.CreateAsync(input);
            else await _projects.UpdateAsync(id.Value, input);
            return RedirectToPage("/Projects/Detail", new { id });
        }
        catch (ConflictException) when (Id != null)
        {
            await ShowConflictAsync(Id.Value);
            await LoadMetaAsync();
            return Page();
        }
        catch (DbUpdateConcurrencyException) when (Id != null)
        {
            // 읽고 저장하는 사이에 다른 저장이 끼어든 경우 — 버전 확인을 통과했어도 DB 가 막는다. 같은 방법으로 다시 저장하게 한다
            await ShowConflictAsync(Id.Value);
            await LoadMetaAsync();
            return Page();
        }
        catch (RuleException ex)
        {
            Error = ex.Message;
            ErrorField = ex.Field;
            await LoadMetaAsync();
            return Page();
        }
    }

    // 동시 수정 충돌: 입력한 값은 그대로 두고 최신 버전으로 바꿔, 확인한 뒤 같은 화면에서 다시 저장할 수 있게 한다.
    // (예전에는 같은 옛 버전으로 다시 그려 몇 번을 눌러도 거절되고, 메시지는 '최신 내용으로 바꿨다'고 잘못 말했다)
    private async Task ShowConflictAsync(int id)
    {
        // 실패한 저장이 컨텍스트에 남긴 값이 섞이지 않게 추적 없이 DB 에서 새로 읽는다
        var latest = await _db.Projects.AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.Owner)
            .Include(x => x.Equipments).ThenInclude(e => e.EquipmentType).ThenInclude(t => t!.Parent)
            .Include(x => x.Containers).ThenInclude(c => c.ContainerType)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (latest == null)
        {
            Error = "다른 사용자가 이 과제를 삭제했습니다. 저장할 수 없습니다.";
            return;
        }

        var before = ParseBase(Form.Base);
        var now = Snapshot(latest);
        var locations = await _projects.LocationsAsync();
        foreach (var (key, value) in now)
        {
            if (before != null && before.TryGetValue(key, out var old) && old == value) continue;
            Changed[key] = key switch
            {
                "locationId" => string.Join(" · ", locations.Chain(latest.LocationId).Select(l => l.Name)),
                "items" => ItemsText(latest),
                _ => value.Length == 0 ? "(비어 있음)" : value,
            };
        }

        // 다시 저장할 때는 지금 버전으로 보낸다. 모델 상태에 남은 옛 값이 숨은 입력에 다시 그려지지 않게 지운다
        Form.Version = latest.Version;
        Form.Base = AppJson.Serialize(now);
        ModelState.Remove("Form.Version");
        ModelState.Remove("Form.Base");
        Error = Changed.Count > 0
            ? "다른 사용자가 이 과제를 먼저 수정했습니다. 그 사람이 바꾼 값을 항목 옆에 표시했습니다. 입력한 내용은 그대로 있으니 확인한 뒤 다시 저장하면 지금 화면의 내용으로 저장됩니다."
            : "다른 사용자가 이 과제를 먼저 수정했습니다(이 화면에 없는 항목). 입력한 내용은 그대로 있으니 다시 저장하면 지금 화면의 내용으로 저장됩니다.";
    }

    // 충돌 비교용 값 (표시 문자열이 아니라 비교할 수 있는 원래 값)
    private static Dictionary<string, string> Snapshot(Project p) => new()
    {
        ["title"] = p.Title,
        ["locationId"] = p.LocationId.ToString(),
        ["owner"] = p.Owner?.Name ?? "",
        ["startDate"] = p.StartDate.ToString("yyyy-MM-dd"),
        ["vendor"] = p.Vendor ?? "",
        ["description"] = p.Description ?? "",
        ["items"] = string.Join("|", p.Equipments.OrderBy(e => e.Id).Select(e => $"E{e.EquipmentTypeId}:{e.Quantity}:{e.Note}"))
                    + "/" + string.Join("|", p.Containers.OrderBy(c => c.Id).Select(c => $"C{c.ContainerTypeId}:{c.Quantity}:{c.Note}")),
    };

    private static Dictionary<string, string>? ParseBase(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string ItemsText(Project p)
    {
        var items = p.Category?.Kind == CategoryKind.Container
            ? p.Containers.OrderBy(c => c.Id).Select(c => $"{c.ContainerType?.Name} {c.Quantity}")
            : p.Equipments.OrderBy(e => e.Id).Select(e => $"{Views.EquipmentName(e.EquipmentType)} {e.Quantity}");
        var text = string.Join(", ", items);
        return text.Length == 0 ? "(비어 있음)" : text;
    }

    private async Task LoadMetaAsync()
    {
        Categories = (await _db.Categories.AsNoTracking().ToListAsync())
            .Where(c => c.IsActive || c.Id == Form.CategoryId).OrderBy(c => c.SortOrder).ToList();
        CategoryName = Categories.FirstOrDefault(c => c.Id == Form.CategoryId)?.Name ?? "";
        UserNames = await _projects.UserNamesAsync();
        VendorNames = (await _db.Vendors.AsNoTracking().ToListAsync()).Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Name).ToList();

        var locations = (await _db.Locations.AsNoTracking().ToListAsync())
            .OrderBy(l => l.Level).ThenBy(l => l.SortOrder).ThenBy(l => l.Id)
            .Select(l => new { id = l.Id, parentId = l.ParentId, level = l.Level, name = l.Name, active = l.IsActive });
        var equipment = (await _db.EquipmentTypes.AsNoTracking().ToListAsync())
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
            .Select(e => new { id = e.Id, parentId = e.ParentId, name = e.Name, active = e.IsActive });
        var containers = (await _db.ContainerTypes.AsNoTracking().ToListAsync())
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
            .Select(e => new { id = e.Id, name = e.Name, active = e.IsActive });

        // 지금 담당자 이름(저장된 값). 화면은 이 이름을 '새 담당자' 안내에서 뺀다 — 입력칸 값으로 정하면 오류로 다시 그린 화면에서
        // 새로 입력한(오타일 수 있는) 이름까지 기존 담당으로 보고 안내를 숨긴다
        string? currentOwner = null;
        if (!IsNew)
        {
            var ownerId = await _db.Projects.AsNoTracking().Where(x => x.Id == Id).Select(x => x.OwnerId).FirstOrDefaultAsync();
            currentOwner = await _db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => u.Name).FirstOrDefaultAsync();
        }

        MetaJson = AppJson.Serialize(new
        {
            isNew = IsNew,
            currentOwner,
            categories = Categories.Select(c => new { id = c.Id, name = c.Name, kind = Views.Key(c.Kind) }),
            locations,
            equipment,
            containers,
            form = new { categoryId = Form.CategoryId, locationId = Form.LocationId, items = Form.Items },
            errorField = ErrorField,
        });
    }
}
