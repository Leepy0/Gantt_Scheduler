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
        catch (RuleException ex)
        {
            Error = ex.Message;
            ErrorField = ex.Field;
            await LoadMetaAsync();
            return Page();
        }
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

        MetaJson = AppJson.Serialize(new
        {
            isNew = IsNew,
            categories = Categories.Select(c => new { id = c.Id, name = c.Name, kind = Views.Key(c.Kind) }),
            locations,
            equipment,
            containers,
            form = new { categoryId = Form.CategoryId, locationId = Form.LocationId, items = Form.Items },
            errorField = ErrorField,
        });
    }
}
