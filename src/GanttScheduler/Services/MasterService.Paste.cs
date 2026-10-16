using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// 관리 화면의 CSV 붙여넣기. Apply=false 면 미리보기(저장 없음), true 면 문제가 없을 때만 한 번에 추가한다
public record MasterPasteInput(string Kind, string? Text, bool Apply = false);

// 한 줄의 결과. Action: add(새로 추가) · exists(이미 있어 건너뜀) · error(문제 — 하나라도 있으면 아무것도 추가하지 않는다)
public record MasterPasteRow(int Line, string Text, string Action, string? Message = null);

public record MasterPasteResult(string Kind, List<string> Columns, List<MasterPasteRow> Rows, int Added, int Skipped, int Errors, bool Applied);

// 기준 정보 CSV 일괄 입력: 종류마다 칸 순서가 정해져 있고(PasteColumns), 머리글 줄은 건너뛴다. 칸은 쉼표나 TAB.
// 이미 있는 이름은 건너뛰고 고치지 않는다(관리자가 고친 값을 덮지 않게). 상위가 없으면 함께 만든다(계층·조직·유형의 단계).
// 한 번의 붙여넣기는 변경 이력에 한 줄로 남긴다(줄마다 남기면 수백 줄이 된다)
public partial class MasterService
{
    public const int MaxCsvLines = 500;

    // 종류별 칸 순서 (머리글·샘플·화면 안내가 같은 이름을 쓴다)
    public static IReadOnlyList<string> PasteColumns(string kind) => kind switch
    {
        "location" => new[] { "법인", "제품군", "라인", "층", "국가", "해상 운송일" },
        "category" => new[] { "유형", "단계", "종류", "기본 근무일", "동시", "체크 항목", "작업명", "시작 근무일", "기간 근무일", "선행 작업명" },
        "equipment" => new[] { "대분류", "하위 타입" },
        "container" => new[] { "용기 종류" },
        "vendor" => new[] { "협력사" },
        "org" => new[] { "그룹", "파트" },
        "user" => new[] { "이름", "그룹", "파트", "메일" },
        "holiday" => new[] { "날짜", "이름" },
        "reason" => new[] { "지연 사유" },
        _ => throw new RuleException("CSV 로 넣을 수 없는 항목입니다."),
    };

    // 샘플 CSV (머리글 + 예시 줄). 사내 고유 정보가 아닌 일반적인 예만 적는다
    public static string PasteSample(string kind, DateOnly today)
    {
        var y = today.Year + 1;
        var rows = kind switch
        {
            "location" => new[] { "A법인,,,,한국,0", "A법인,C제품,1라인,1층,,", "A법인,C제품,1라인,2층,,", "B법인,D제품,2라인,,베트남,25" },
            "category" => new[]
            {
                "설비 도입,,일반,,,,,,,", "설비 도입,검토,일반,10,,현장 실사 완료;요구 사항 확정,,,,", "설비 도입,검토,,,,,현장 실사,0,3,",
                "설비 도입,검토,,,,,사양 검토,3,5,현장 실사", "설비 도입,발주,일반,5,,,,,,", "설비 도입,검수,검수,5,,FAT 성적서 확인,,,,",
                "설비 도입,교육,일반,5,동시,,,,,", "설비 도입,입고,입고,,,,,,,",
            },
            "equipment" => new[] { "AGV,ANT", "AGV,Belt", "컨베이어," },
            "container" => new[] { "파렛트", "대차" },
            "vendor" => new[] { "A로보틱스", "B오토메이션" },
            "org" => new[] { "물류기술그룹,자동화파트", "물류기술그룹,설비파트", "생산기술그룹," },
            "user" => new[] { "김물류,물류기술그룹,자동화파트,kim@company.com", "이설비,물류기술그룹,설비파트," },
            "holiday" => new[] { $"{y}-01-01,신정", $"{y}-03-01,삼일절", $"{y}-05-05,어린이날" },
            "reason" => new[] { "자재·납품 지연", "설계·사양 변경" },
            _ => throw new RuleException("CSV 로 넣을 수 없는 항목입니다."),
        };
        return string.Join(",", PasteColumns(kind)) + "\n" + string.Join("\n", rows) + "\n";
    }

    private sealed record CsvLine(int Line, List<string?> Cells, string Raw)
    {
        public string? At(int i) => i < Cells.Count ? Cells[i] : null;
    }

    private static List<CsvLine> ReadCsv(string? text, IReadOnlyList<string> columns)
    {
        var lines = new List<CsvLine>();
        foreach (var (line, raw) in InputText.Lines(text))
        {
            var cells = InputText.Cells(raw).Select(c => c.Length == 0 ? null : c).ToList();
            // 머리글: 첫 칸이 첫 열 이름과 같다(공백 무시)
            if (lines.Count == 0 && cells.Count > 0 && Key(cells[0] ?? "") == Key(columns[0])) continue;
            lines.Add(new CsvLine(line, cells, raw));
        }
        return lines;
    }

    private static string Key(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    private static bool Same(string a, string b) => Key(a) == Key(b);

    public async Task<MasterPasteResult> PasteAsync(MasterPasteInput input)
    {
        RequireAdmin();
        Notice = null;
        var columns = PasteColumns(input.Kind);
        var lines = ReadCsv(input.Text, columns);
        if (lines.Count == 0) throw new RuleException("붙여 넣을 내용을 입력해 주세요. 첫 줄은 머리글(" + string.Join(", ", columns) + ")이어도 됩니다.", "text");
        if (lines.Count > MaxCsvLines) throw new RuleException($"한 번에 {MaxCsvLines}줄까지 넣을 수 있습니다(지금 {lines.Count}줄).", "text");

        var rows = new List<MasterPasteRow>();
        var added = new List<string>();      // 이력에 남길 새 항목 이름
        var apply = input.Apply;
        // 미리보기와 적용은 같은 코드를 돈다. 미리보기도 엔티티를 만들어 DbSet 에 넣지만 저장하지 않는다(요청 범위의 컨텍스트라 버려진다)
        switch (input.Kind)
        {
            case "location": await PasteLocationsAsync(lines, rows, added); break;
            case "category": await PasteCategoriesAsync(lines, rows, added); break;
            case "equipment": await PasteEquipmentAsync(lines, rows, added); break;
            case "container": await PasteNamesAsync(lines, rows, added, _db.ContainerTypes, c => c.Name, c => c.SortOrder, (n, o) => new ContainerType { Name = n, SortOrder = o }, Limits.ContainerName); break;
            case "vendor": await PasteNamesAsync(lines, rows, added, _db.Vendors, c => c.Name, c => c.SortOrder, (n, o) => new Vendor { Name = n, SortOrder = o }, Limits.VendorName); break;
            case "reason": await PasteNamesAsync(lines, rows, added, _db.DelayReasons, c => c.Name, c => c.SortOrder, (n, o) => new DelayReason { Name = n, SortOrder = o }, Limits.DelayReasonName); break;
            case "org": await PasteOrgsAsync(lines, rows, added); break;
            case "user": await PasteUsersAsync(lines, rows, added); break;
            case "holiday": await PasteHolidayRowsAsync(lines, rows, added); break;
            default: throw new RuleException("CSV 로 넣을 수 없는 항목입니다.");
        }

        var errors = rows.Count(r => r.Action == "error");
        var skipped = rows.Count(r => r.Action == "exists");
        if (!apply || errors > 0)
        {
            // 미리보기(또는 문제가 있어 적용하지 않음): 만들어 둔 엔티티는 저장하지 않는다
            _db.ChangeTracker.Clear();
            if (apply) throw new RuleException($"문제가 {errors}건 있어 아무것도 추가하지 않았습니다. 미리보기에서 고칠 줄을 확인해 주세요.", "text");
            return new MasterPasteResult(input.Kind, columns.ToList(), rows, added.Count, skipped, errors, false);
        }
        if (added.Count > 0)
        {
            var what = MasterLog.Kinds.TryGetValue(input.Kind, out var label) ? label : input.Kind;
            var names = string.Join(", ", added);
            _changes.Add(input.Kind, (int?)null, $"{what} CSV 붙여넣기: {added.Count}건 추가", null, names.Length > 900 ? names[..900] + "…" : names);
        }
        await _db.SaveChangesAsync();
        Notice = $"{added.Count}건을 추가했습니다" + (skipped > 0 ? $" (이미 있는 {skipped}건은 건너뜀)" : "") + ".";
        if (input.Kind == "holiday" && added.Count > 0)
        {
            var dates = await HolidayDatesAsync();
            var newDates = _lastHolidayDates;
            Notice += " " + await HolidayImpactAsync(dates.Where(d => !newDates.Contains(d)).ToList(), dates, newDates);
        }
        return new MasterPasteResult(input.Kind, columns.ToList(), rows, added.Count, skipped, 0, true);
    }

    // x 가 parent 의 자식인지. DB 에서 읽은 항목은 ParentId 로, 이 요청에서 새로 만든(아직 Id 가 0 인) 상위의 자식은 내비게이션으로 견준다
    private static bool IsChildOf<T>(int? parentId, T? parentNav, T? parent) where T : class =>
        parent == null ? parentId == null && parentNav == null
        : ReferenceEquals(parentNav, parent) || (ParentKey(parent) is int id && id != 0 && parentId == id);

    private static int? ParentKey(object parent) => parent switch
    {
        Location l => l.Id, OrgUnit o => o.Id, EquipmentType e => e.Id, _ => null,
    };

    private static string RowText(CsvLine l, int take) => string.Join(" › ", l.Cells.Take(take).Where(c => c != null));

    private static void Error(List<MasterPasteRow> rows, CsvLine l, string message, int take = 4) =>
        rows.Add(new MasterPasteRow(l.Line, RowText(l, take).Length == 0 ? l.Raw.Trim() : RowText(l, take), "error", message));

    private static string? CheckName(string? name, int max, string what)
    {
        if (name == null) return $"{what}을(를) 입력해 주세요.";
        if (name.Length > max) return $"{what}은(는) {max}자 이내로 입력해 주세요.";
        return null;
    }

    // 이름 하나짜리(용기·협력사·지연 사유)
    private async Task PasteNamesAsync<T>(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added, DbSet<T> set,
        Func<T, string> nameOf, Func<T, int> sortOf, Func<string, int, T> make, int max) where T : class
    {
        var all = await set.ToListAsync();
        var next = NextSort(all.Select(sortOf));
        foreach (var l in lines)
        {
            var name = l.At(0);
            if (CheckName(name, max, "이름") is { } bad) { Error(rows, l, bad, 1); continue; }
            if (all.Any(x => Same(nameOf(x), name!))) { rows.Add(new MasterPasteRow(l.Line, name!, "exists")); continue; }
            var e = make(name!, next++);
            set.Add(e);
            all.Add(e);
            added.Add(name!);
            rows.Add(new MasterPasteRow(l.Line, name!, "add"));
        }
    }

    // 계층: 법인 › 제품군 › 라인 › 층 경로. 없는 항목은 그 자리에 만든다. 국가·해상 운송일은 법인을 새로 만들 때만 넣는다
    private async Task PasteLocationsAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var all = await _db.Locations.ToListAsync();
        foreach (var l in lines)
        {
            var names = Enumerable.Range(0, LocationDepth).Select(l.At).ToList();
            if (names[0] == null) { Error(rows, l, "법인을 입력해 주세요."); continue; }
            var gap = false;
            for (var i = 1; i < LocationDepth; i++) if (names[i] != null && names[i - 1] == null) gap = true;
            if (gap) { Error(rows, l, "상위 칸을 비운 채 하위를 적었습니다. 법인 › 제품군 › 라인 › 층 차례로 채워 주세요."); continue; }
            if (names.Where(n => n != null).Any(n => n!.Length > Limits.LocationName)) { Error(rows, l, $"이름은 {Limits.LocationName}자 이내로 입력해 주세요."); continue; }
            var country = l.At(4);
            if (country?.Length > Limits.Country) { Error(rows, l, $"국가는 {Limits.Country}자 이내로 입력해 주세요."); continue; }
            var lead = 0;
            if (l.At(5) != null && (!int.TryParse(l.At(5), out lead) || lead < 0 || lead > 365)) { Error(rows, l, "해상 운송 소요일은 0~365 사이의 숫자로 적어 주세요."); continue; }

            Location? parent = null;
            var created = new List<string>();
            for (var level = 1; level <= LocationDepth && names[level - 1] != null; level++)
            {
                var name = names[level - 1]!;
                var siblings = all.Where(x => x.Level == level && IsChildOf(x.ParentId, x.Parent, parent)).ToList();
                var found = siblings.Where(x => Same(x.Name, name)).OrderBy(x => x.IsActive ? 0 : 1).FirstOrDefault();
                if (found == null)
                {
                    found = new Location { Level = level, Name = name, Parent = parent, ParentId = parent?.Id == 0 ? null : parent?.Id, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    if (level == 1) { found.Country = country; found.OceanLeadDays = lead; }
                    _db.Locations.Add(found);
                    all.Add(found);
                    created.Add(name);
                }
                parent = found;
            }
            if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, RowText(l, LocationDepth), "exists"));
            else
            {
                added.Add(RowText(l, LocationDepth));
                rows.Add(new MasterPasteRow(l.Line, RowText(l, LocationDepth), "add", created.Count < names.Count(n => n != null) ? $"새로 만드는 항목: {string.Join(" › ", created)}" : null));
            }
        }
    }

    // 조직: 그룹 › 파트
    private async Task PasteOrgsAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var all = await _db.OrgUnits.ToListAsync();
        foreach (var l in lines)
        {
            var group = l.At(0);
            var part = l.At(1);
            if (CheckName(group, Limits.OrgName, "그룹") is { } bad) { Error(rows, l, bad, 2); continue; }
            if (part?.Length > Limits.OrgName) { Error(rows, l, $"파트 이름은 {Limits.OrgName}자 이내로 입력해 주세요.", 2); continue; }
            var (g, p, created) = EnsureOrg(all, group!, part);
            var text = RowText(l, 2);
            if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, text, "exists"));
            else { added.Add(text); rows.Add(new MasterPasteRow(l.Line, text, "add")); }
        }
    }

    // 그룹(·파트)을 찾거나 만든다. 사용자 붙여넣기도 같은 도우미로 소속을 만든다
    private (OrgUnit Group, OrgUnit? Part, List<string> Created) EnsureOrg(List<OrgUnit> all, string group, string? part)
    {
        var created = new List<string>();
        var g = all.Where(o => o.Level == 1 && Same(o.Name, group)).OrderBy(o => o.IsActive ? 0 : 1).FirstOrDefault();
        if (g == null)
        {
            g = new OrgUnit { Level = 1, Name = group, SortOrder = NextSort(all.Where(o => o.Level == 1).Select(o => o.SortOrder)) };
            _db.OrgUnits.Add(g);
            all.Add(g);
            created.Add(group);
        }
        OrgUnit? p = null;
        if (part != null)
        {
            p = all.Where(o => o.Level == 2 && ((o.ParentId == g.Id && g.Id != 0) || ReferenceEquals(o.Parent, g)) && Same(o.Name, part)).OrderBy(o => o.IsActive ? 0 : 1).FirstOrDefault();
            if (p == null)
            {
                var siblings = all.Where(o => o.Level == 2 && ((o.ParentId == g.Id && g.Id != 0) || ReferenceEquals(o.Parent, g))).ToList();
                p = new OrgUnit { Level = 2, Name = part, Parent = g, ParentId = g.Id == 0 ? null : g.Id, SortOrder = NextSort(siblings.Select(o => o.SortOrder)) };
                _db.OrgUnits.Add(p);
                all.Add(p);
                created.Add(part);
            }
        }
        return (g, p, created);
    }

    // 기기: 대분류 › 하위 타입(선택)
    private async Task PasteEquipmentAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var all = await _db.EquipmentTypes.ToListAsync();
        foreach (var l in lines)
        {
            var top = l.At(0);
            var sub = l.At(1);
            if (CheckName(top, Limits.EquipmentName, "대분류") is { } bad) { Error(rows, l, bad, 2); continue; }
            if (sub?.Length > Limits.EquipmentName) { Error(rows, l, $"하위 타입 이름은 {Limits.EquipmentName}자 이내로 입력해 주세요.", 2); continue; }
            var created = new List<string>();
            var parent = all.Where(e => e.ParentId == null && e.Parent == null && Same(e.Name, top!)).OrderBy(e => e.IsActive ? 0 : 1).FirstOrDefault();
            if (parent == null)
            {
                parent = new EquipmentType { Name = top!, SortOrder = NextSort(all.Where(e => e.ParentId == null && e.Parent == null).Select(e => e.SortOrder)) };
                _db.EquipmentTypes.Add(parent);
                all.Add(parent);
                created.Add(top!);
            }
            if (sub != null)
            {
                var exists = all.Any(e => ((e.ParentId == parent.Id && parent.Id != 0) || ReferenceEquals(e.Parent, parent)) && Same(e.Name, sub));
                if (!exists)
                {
                    var siblings = all.Where(e => (e.ParentId == parent.Id && parent.Id != 0) || ReferenceEquals(e.Parent, parent)).ToList();
                    var e = new EquipmentType { Name = sub, Parent = parent, ParentId = parent.Id == 0 ? null : parent.Id, SortOrder = NextSort(siblings.Select(s => s.SortOrder)) };
                    _db.EquipmentTypes.Add(e);
                    all.Add(e);
                    created.Add(sub);
                }
            }
            var text = RowText(l, 2);
            if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, text, "exists"));
            else { added.Add(text); rows.Add(new MasterPasteRow(l.Line, text, "add")); }
        }
    }

    // 사용자: 이름, 그룹, 파트, 메일. 같은 이름(대소문자 무시)이 있으면 건너뛴다. 관리자 지정은 CSV 로 하지 않는다(권한은 화면에서 한 명씩)
    private async Task PasteUsersAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var users = await _db.Users.ToListAsync();
        var orgs = await _db.OrgUnits.ToListAsync();
        foreach (var l in lines)
        {
            var name = l.At(0);
            if (CheckName(name, Limits.UserName, "이름") is { } bad) { Error(rows, l, bad, 1); continue; }
            var group = l.At(1);
            var part = l.At(2);
            if (part != null && group == null) { Error(rows, l, "파트를 적으려면 그룹도 적어 주세요.", 3); continue; }
            if (group?.Length > Limits.OrgName || part?.Length > Limits.OrgName) { Error(rows, l, $"소속 이름은 {Limits.OrgName}자 이내로 입력해 주세요.", 3); continue; }
            string? email = null;
            if (l.At(3) is { } mail)
            {
                email = NotifyOptions.NormalizeEmail(mail);
                if (email == null) { Error(rows, l, $"메일 주소를 확인해 주세요: '{mail}'", 1); continue; }
            }
            if (users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))) { rows.Add(new MasterPasteRow(l.Line, name!, "exists")); continue; }
            OrgUnit? unit = null;
            var createdOrgs = new List<string>();
            if (group != null)
            {
                var (g, p, created) = EnsureOrg(orgs, group, part);
                unit = p ?? g;
                createdOrgs = created;
            }
            var u = new AppUser { Name = name!, OrgUnit = unit, OrgUnitId = unit?.Id == 0 ? null : unit?.Id, Email = email, CreatedAt = _clock.UtcNow };
            _db.Users.Add(u);
            users.Add(u);
            added.Add(name!);
            rows.Add(new MasterPasteRow(l.Line, name!, "add", createdOrgs.Count > 0 ? $"소속도 새로 만듭니다: {string.Join(" › ", createdOrgs)}" : null));
        }
    }

    private List<DateOnly> _lastHolidayDates = new();

    // 공휴일: 날짜, 이름. 이미 있는 날짜는 건너뛴다(PasteHolidaysAsync 와 같다)
    private async Task PasteHolidayRowsAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var existing = (await HolidayDatesAsync()).ToHashSet();
        var seen = new HashSet<DateOnly>();
        _lastHolidayDates = new List<DateOnly>();
        foreach (var l in lines)
        {
            var dateText = l.At(0);
            var name = l.At(1);
            if (dateText == null || !InputText.TryDate(dateText, _clock.Today, out var date)) { Error(rows, l, $"'{dateText}' 을(를) 날짜로 읽지 못했습니다. 2028-01-01 처럼 적어 주세요.", 2); continue; }
            if (CheckName(name, Limits.HolidayName, "이름") is { } bad) { Error(rows, l, bad, 2); continue; }
            var text = $"{date:yyyy-MM-dd} {name}";
            if (!seen.Add(date)) { Error(rows, l, $"{date:yyyy-MM-dd} 이(가) 두 번 들어 있습니다.", 2); continue; }
            if (existing.Contains(date)) { rows.Add(new MasterPasteRow(l.Line, text, "exists")); continue; }
            _db.Holidays.Add(new Holiday { Date = date, Name = name! });
            _lastHolidayDates.Add(date);
            added.Add(text);
            rows.Add(new MasterPasteRow(l.Line, text, "add"));
        }
    }

    // 유형: 유형 줄(종류 = 일반·물류 자동화·용기), 단계 줄(종류 = 일반·검수·입고, 기본 근무일, 동시, 체크 항목 ; 로 나눔), 작업 줄(작업명·시작·기간·선행).
    // 단계·작업 줄의 유형·단계가 없으면 함께 만든다
    private async Task PasteCategoriesAsync(List<CsvLine> lines, List<MasterPasteRow> rows, List<string> added)
    {
        var cats = await _db.Categories.Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems)
            .Include(c => c.StageTemplates).ThenInclude(t => t.Works).AsSplitQuery().ToListAsync();
        foreach (var l in lines)
        {
            var catName = l.At(0);
            var stageName = l.At(1);
            var kindText = l.At(2);
            var workTitle = l.At(6);
            if (CheckName(catName, Limits.CategoryName, "유형") is { } bad) { Error(rows, l, bad, 2); continue; }
            if (stageName?.Length > Limits.StageName) { Error(rows, l, $"단계명은 {Limits.StageName}자 이내로 입력해 주세요.", 2); continue; }
            if (workTitle != null && stageName == null) { Error(rows, l, "작업 템플릿 줄에는 단계도 적어 주세요.", 2); continue; }
            var created = new List<string>();

            var cat = cats.FirstOrDefault(c => Same(c.Name, catName!));
            if (cat == null)
            {
                var ck = stageName == null ? ParseCategoryKind(kindText) : CategoryKind.General;
                if (ck == null) { Error(rows, l, $"유형 종류 '{kindText}' 을(를) 모릅니다. 일반·물류 자동화·용기 중 하나로 적어 주세요.", 2); continue; }
                cat = new Category { Name = catName!, Kind = ck.Value, SortOrder = NextSort(cats.Select(c => c.SortOrder)) };
                _db.Categories.Add(cat);
                cats.Add(cat);
                created.Add(catName!);
            }
            if (stageName == null)
            {
                var text0 = catName!;
                if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, text0, "exists"));
                else { added.Add(text0); rows.Add(new MasterPasteRow(l.Line, text0, "add")); }
                continue;
            }

            var stage = cat.StageTemplates.FirstOrDefault(t => Same(t.Name, stageName));
            if (stage == null)
            {
                var sk = workTitle == null ? ParseStageKind(kindText) : StageKind.Normal;
                if (sk == null) { Error(rows, l, $"단계 종류 '{kindText}' 을(를) 모릅니다. 일반·검수·입고 중 하나로 적어 주세요.", 2); continue; }
                int? days = null;
                if (workTitle == null && l.At(3) is { } daysText)
                {
                    if (!int.TryParse(daysText, out var d) || d < 0 || d > Rules.MaxDefaultWorkDays) { Error(rows, l, $"기본 근무일은 0~{Rules.MaxDefaultWorkDays} 사이의 숫자로 적어 주세요.", 2); continue; }
                    days = d;
                }
                stage = new StageTemplate
                {
                    Name = stageName, Kind = sk.Value, DefaultWorkDays = days, Category = cat, CategoryId = cat.Id,
                    Parallel = workTitle == null && cat.StageTemplates.Count > 0 && IsYes(l.At(4)),
                    SortOrder = NextSort(cat.StageTemplates.Select(t => t.SortOrder)),
                };
                cat.StageTemplates.Add(stage);
                created.Add(stageName);
            }
            if (workTitle == null)
            {
                // 체크 항목(; 로 나눔): 없는 것만 더한다
                var checks = (l.At(5) ?? "").Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (checks.Any(c => c.Length > Limits.CheckText)) { Error(rows, l, $"체크 항목은 {Limits.CheckText}자 이내로 입력해 주세요.", 2); continue; }
                foreach (var c in checks.Where(c => !stage.CheckItems.Any(k => Same(k.Text, c))))
                {
                    stage.CheckItems.Add(new TemplateCheckItem { Text = c, SortOrder = NextSort(stage.CheckItems.Select(k => k.SortOrder)) });
                    created.Add("체크: " + c);
                }
                var text1 = $"{catName} › {stageName}";
                if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, text1, "exists"));
                else { added.Add(text1); rows.Add(new MasterPasteRow(l.Line, text1, "add", created.Count > 0 && !created.Contains(stageName) ? $"더하는 항목: {string.Join(", ", created)}" : null)); }
                continue;
            }

            // 작업 템플릿
            if (workTitle.Length > Limits.WorkTitle) { Error(rows, l, $"작업명은 {Limits.WorkTitle}자 이내로 입력해 주세요.", 2); continue; }
            var offset = 0;
            var duration = 1;
            if (l.At(7) != null && !int.TryParse(l.At(7), out offset)) { Error(rows, l, "시작 근무일은 숫자로 적어 주세요.", 2); continue; }
            if (l.At(8) != null && !int.TryParse(l.At(8), out duration)) { Error(rows, l, "기간 근무일은 숫자로 적어 주세요.", 2); continue; }
            try { Rules.ValidateTemplateWork(offset, duration); }
            catch (RuleException ex) { Error(rows, l, ex.Message, 2); continue; }
            TemplateWork? pred = null;
            if (l.At(9) is { } predName)
            {
                pred = stage.Works.FirstOrDefault(w => Same(w.Title, predName));
                if (pred == null) { Error(rows, l, $"선행 작업 '{predName}' 이(가) 이 단계에 없습니다. 선행 줄을 먼저 적어 주세요.", 2); continue; }
            }
            var text2 = $"{catName} › {stageName} › {workTitle}";
            if (stage.Works.Any(w => Same(w.Title, workTitle)))
            {
                if (created.Count == 0) rows.Add(new MasterPasteRow(l.Line, text2, "exists"));
                else { added.Add(text2); rows.Add(new MasterPasteRow(l.Line, text2, "add")); }
                continue;
            }
            stage.Works.Add(new TemplateWork
            {
                Title = workTitle, OffsetWorkDays = offset, DurationWorkDays = duration, PredecessorTemplateWork = pred,
                SortOrder = NextSort(stage.Works.Select(w => w.SortOrder)),
            });
            added.Add(text2);
            rows.Add(new MasterPasteRow(l.Line, text2, "add"));
        }
    }

    private static bool IsYes(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        return t.Length > 0 && !new[] { "n", "no", "false", "0", "-", "x", "아니오", "아니요", "순차" }.Contains(t);
    }

    private static CategoryKind? ParseCategoryKind(string? text) => (text ?? "").Trim() switch
    {
        "" or "일반" or "general" => CategoryKind.General,
        "물류 자동화" or "물류자동화" or "자동화" or "기기" or "automation" => CategoryKind.Automation,
        "용기" or "container" => CategoryKind.Container,
        _ => null,
    };

    private static StageKind? ParseStageKind(string? text) => (text ?? "").Trim() switch
    {
        "" or "일반" or "normal" => StageKind.Normal,
        "검수" or "inspection" => StageKind.Inspection,
        "입고" or "inbound" => StageKind.Inbound,
        _ => null,
    };
}
