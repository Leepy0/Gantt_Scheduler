using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Data;

public static class DbInitializer
{
    public static async Task InitAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDb>();
        var cfg = sp.GetRequiredService<IConfiguration>();
        var clock = sp.GetRequiredService<IClock>();
        var env = sp.GetRequiredService<IHostEnvironment>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        // SQL Server 는 마이그레이션으로 스키마를 올리고, SQLite(미리보기·테스트용)는 그때그때 새로 만든다
        if (db.Database.IsSqlServer())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            // SQLite 는 마이그레이션이 없으므로, 모델이 바뀌었으면 DB 를 새로 만든다(담긴 것은 샘플 데이터뿐이라는 전제).
            // 구조 판정은 EF 가 만든 생성 스크립트의 해시라 EF 패치 버전이 바뀌어도 달라질 수 있다 — 패키지 버전을
            // Directory.Packages.props 에 고정해 그런 일이 업그레이드 때만 생기게 했다
            var path = AppPaths.Sqlite(cfg, env.ContentRootPath);
            var marker = path + ".schema";
            var schema = SchemaHash(db.Database.GenerateCreateScript());
            if (File.Exists(marker) && await File.ReadAllTextAsync(marker) != schema)
            {
                if (env.IsDevelopment())
                {
                    log.LogWarning("모델이 바뀌어 SQLite DB 를 새로 만듭니다 (기존 미리보기 데이터는 지워집니다).");
                    await db.Database.EnsureDeletedAsync();
                }
                else
                {
                    // 개발 환경이 아니면 누군가 실제로 쓰던 데이터일 수 있어 지우지 않고 옆에 남겨 둔다
                    var backup = SetAside(path, clock.UtcNow);
                    log.LogWarning("모델이 바뀌어 SQLite DB 를 새로 만듭니다. 기존 DB 는 {Backup} 로 옮겨 두었습니다.", backup);
                }
            }
            await db.Database.EnsureCreatedAsync();
            await File.WriteAllTextAsync(marker, schema);
        }

        await Seeder.SeedMastersAsync(db);
        if (cfg.GetValue<bool>("Seed:Demo")) await Seeder.SeedDemoAsync(db, clock);
        // 기준선 차수 이력이 생기기 전에 확정한 과제에 1차를 만든다(멱등 — 이미 차수가 있으면 건너뛴다)
        var migrated = await GanttScheduler.Services.BaselineHistory.EnsureFirstRoundsAsync(db);
        if (migrated > 0) log.LogInformation("기준선 차수 이력: 과제 {Count}건의 기존 기준선을 1차로 옮겼습니다.", migrated);
    }

    public static string SchemaHash(string script) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));

    // SQLite 파일(그리고 WAL 보조 파일)을 gantt.db.<시각>.bak 으로 옮긴다. 옮긴 이름을 돌려준다.
    // 옮기지 못하면(다른 프로그램이 잡고 있음) 지우지 않고 시작을 멈춘다 — 데이터를 잃는 것보다 낫다
    internal static string SetAside(string path, DateTime utcNow)
    {
        var backup = $"{path}.{utcNow:yyyyMMddHHmmss}.bak";
        if (File.Exists(path)) File.Move(path, backup);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Move(path + suffix, backup + suffix);
        return backup;
    }
}

// 설정에서 파일 경로를 푼다 (상대 경로는 앱 폴더 기준)
public static class AppPaths
{
    public static string Sqlite(IConfiguration cfg, string contentRoot) =>
        Resolve(cfg["Database:SqlitePath"] ?? "App_Data/gantt.db", contentRoot);

    public static string Uploads(IConfiguration cfg, string contentRoot) =>
        Resolve(cfg["Storage:Path"] ?? "App_Data/uploads", contentRoot);

    public static string Keys(IConfiguration cfg, string contentRoot) =>
        Resolve(cfg["Storage:KeyPath"] ?? "App_Data/keys", contentRoot);

    public static string Updates(IConfiguration cfg, string contentRoot) =>
        Resolve(cfg["Update:Path"] ?? "App_Data/updates", contentRoot);

    // 메일 요약의 마지막 실행 기록 (하루 한 번만 보내기 위해)
    public static string Notify(IConfiguration cfg, string contentRoot) =>
        Resolve(cfg["Notify:StatePath"] ?? "App_Data/notify.json", contentRoot);

    private static string Resolve(string path, string contentRoot) =>
        Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
}

public static class Seeder
{
    // 기준 정보 기본값 — 테이블이 비어 있을 때만 넣는다 (공휴일은 해마다: 그 해 공휴일이 하나도 없을 때)
    public static async Task SeedMastersAsync(AppDb db)
    {
        if (!await db.Categories.AnyAsync())
        {
            var automation = Cat("물류 자동화", CategoryKind.Automation, 1,
                ("검토", StageKind.Normal), ("품의", StageKind.Normal), ("발주", StageKind.Normal), ("제작", StageKind.Normal),
                ("검수", StageKind.Inspection), ("입고", StageKind.Inbound), ("설치", StageKind.Normal), ("안정화", StageKind.Normal));
            Checks(automation, "검수", "FAT 성적서 확인", "주행 시험 결과 승인", "안전 점검 완료");
            Checks(automation, "입고", "통관 서류 확인", "수량·외관 검수");
            var general = Cat("물류", CategoryKind.General, 2,
                ("검토", StageKind.Normal), ("보고", StageKind.Normal), ("적용", StageKind.Normal));
            var container = Cat("용기", CategoryKind.Container, 3,
                ("검토", StageKind.Normal), ("품의", StageKind.Normal), ("발주", StageKind.Normal), ("제작", StageKind.Normal),
                ("입고", StageKind.Normal), ("적용", StageKind.Normal));
            var simulation = Cat("Simulation", CategoryKind.General, 4,
                ("데이터 수집", StageKind.Normal), ("모델링", StageKind.Normal), ("검증", StageKind.Normal), ("보고", StageKind.Normal));

            // 기본 기간(근무일) — 등록 화면의 목표일 초안. 해외 입고는 법인마다 운송 기간이 달라 비워 두고 추천 입고일(검수일 + 해상 운송)을 쓴다
            Durations(automation, 10, 10, 5, 40, 5, null, 10, 20);
            Durations(general, 10, 5, 15);
            Durations(container, 5, 10, 5, 20, 10, 10);
            Durations(simulation, 10, 15, 10, 5);
            // 유형 템플릿 작업 — 등록할 때 단계마다 함께 만든다(시작·기간은 근무일, 선행은 같은 단계 안)
            Works(container, "검토", ("현장 실사", 0, 3, null), ("용기 사양 검토", 3, 5, "현장 실사"));
            Works(container, "제작", ("시제품 제작", 0, 10, null), ("시제품 검증", 10, 5, "시제품 제작"));

            db.Categories.AddRange(automation, general, container, simulation);
        }

        if (!await db.EquipmentTypes.AnyAsync())
        {
            var order = 0;
            foreach (var top in new[] { "AGV", "AMR" })
            {
                var parent = new EquipmentType { Name = top, SortOrder = ++order };
                db.EquipmentTypes.Add(parent);
                var sub = 0;
                foreach (var name in new[] { "ANT", "Belt", "Roller", "상차형" })
                    db.EquipmentTypes.Add(new EquipmentType { Name = name, Parent = parent, SortOrder = ++sub });
            }
            db.EquipmentTypes.Add(new EquipmentType { Name = "컨베이어", SortOrder = ++order });
            db.EquipmentTypes.Add(new EquipmentType { Name = "리프터", SortOrder = ++order });
        }

        if (!await db.ContainerTypes.AnyAsync())
        {
            var order = 0;
            foreach (var name in new[] { "파렛트", "대차", "플라스틱 박스", "철제 랙" })
                db.ContainerTypes.Add(new ContainerType { Name = name, SortOrder = ++order });
        }

        // 지연 사유 분류 — 일정 변경 사유 시트의 '분류' 선택과 변경 분석의 원인 파레토(S10)
        if (!await db.DelayReasons.AnyAsync())
        {
            var order = 0;
            foreach (var name in new[] { "자재·납품 지연", "설계·사양 변경", "협력사 일정", "고객·현업 요청", "인력·우선순위", "품질·시험 문제", "기타" })
                db.DelayReasons.Add(new DelayReason { Name = name, SortOrder = ++order });
        }

        // 공휴일은 해마다 따로 본다: 내장 목록의 해 중 그 해 공휴일이 하나도 없는 해만 채운다. 서버는 시작할 때마다 이 일을 하므로
        // 새 릴리스가 다음 해 공휴일을 실어 보내면 운영 중인 서버에도 들어간다(예전에는 표가 비어 있을 때만 넣어 다음 해가 조용히 빠졌다).
        // 관리자가 일부를 지운 해는 다른 날이 남아 있으므로 다시 채우지 않는다. 내장 목록에 없는 해는 관리자 알림(OpsAlerts)이 10월부터 알린다
        var years = (await db.Holidays.AsNoTracking().Select(h => h.Date).ToListAsync()).Select(d => d.Year).ToHashSet();
        foreach (var (date, name) in KoreanHolidays().Where(h => !years.Contains(h.Date.Year)))
            db.Holidays.Add(new Holiday { Date = date, Name = name });

        await db.SaveChangesAsync();
    }

    private static void Checks(Category category, string stage, params string[] items)
    {
        var template = category.StageTemplates.First(t => t.Name == stage);
        var i = 0;
        foreach (var text in items)
            template.CheckItems.Add(new TemplateCheckItem { Text = text, SortOrder = ++i });
    }

    private static void Works(Category category, string stage, params (string Title, int Offset, int Days, string? After)[] items)
    {
        var template = category.StageTemplates.First(t => t.Name == stage);
        var i = 0;
        foreach (var (title, offset, days, after) in items)
            template.Works.Add(new TemplateWork
            {
                Title = title, OffsetWorkDays = offset, DurationWorkDays = days, SortOrder = ++i,
                PredecessorTemplateWork = after == null ? null : template.Works.First(w => w.Title == after),
            });
    }

    private static void Durations(Category category, params int?[] days)
    {
        var templates = category.StageTemplates.OrderBy(t => t.SortOrder).ToList();
        for (var i = 0; i < templates.Count && i < days.Length; i++) templates[i].DefaultWorkDays = days[i];
    }

    private static Category Cat(string name, CategoryKind kind, int order, params (string Name, StageKind Kind)[] stages)
    {
        var c = new Category { Name = name, Kind = kind, SortOrder = order };
        var i = 0;
        foreach (var s in stages)
            c.StageTemplates.Add(new StageTemplate { Name = s.Name, Kind = s.Kind, SortOrder = ++i });
        return c;
    }

    // 내장 공휴일: 정부가 확정 발표한 해(월력요항)만 넣는다 — 2026~2027 (대체공휴일 포함).
    // 2028년 이후는 대체·임시 공휴일이 아직 정해지지 않아 넣지 않는다(틀린 날이 들어가면 조용히 근무일이 틀린다). 그 해는 관리 화면에서
    // 여러 줄 붙여넣기로 넣거나, 발표 뒤의 릴리스가 여기에 더하면 다음 시작 때 채워진다. 기준 정보 시드 외에 시험에서도 쓴다
    internal static IEnumerable<(DateOnly Date, string Name)> KoreanHolidays()
    {
        (int y, int m, int d, string n)[] rows =
        {
            (2026, 1, 1, "신정"), (2026, 2, 16, "설날 연휴"), (2026, 2, 17, "설날"), (2026, 2, 18, "설날 연휴"),
            (2026, 3, 1, "삼일절"), (2026, 3, 2, "대체공휴일(삼일절)"), (2026, 5, 1, "노동절"), (2026, 5, 5, "어린이날"),
            (2026, 5, 24, "부처님오신날"), (2026, 5, 25, "대체공휴일(부처님오신날)"), (2026, 6, 3, "지방선거"),
            (2026, 6, 6, "현충일"), (2026, 7, 17, "제헌절"), (2026, 8, 15, "광복절"), (2026, 8, 17, "대체공휴일(광복절)"),
            (2026, 9, 24, "추석 연휴"), (2026, 9, 25, "추석"), (2026, 9, 26, "추석 연휴"),
            (2026, 10, 3, "개천절"), (2026, 10, 5, "대체공휴일(개천절)"), (2026, 10, 9, "한글날"), (2026, 12, 25, "성탄절"),

            (2027, 1, 1, "신정"), (2027, 2, 6, "설날 연휴"), (2027, 2, 7, "설날"), (2027, 2, 8, "설날 연휴"),
            (2027, 2, 9, "대체공휴일(설날)"), (2027, 3, 1, "삼일절"), (2027, 5, 1, "노동절"), (2027, 5, 3, "대체공휴일(노동절)"),
            (2027, 5, 5, "어린이날"), (2027, 5, 13, "부처님오신날"), (2027, 6, 6, "현충일"),
            (2027, 7, 17, "제헌절"), (2027, 7, 19, "대체공휴일(제헌절)"), (2027, 8, 15, "광복절"), (2027, 8, 16, "대체공휴일(광복절)"),
            (2027, 9, 14, "추석 연휴"), (2027, 9, 15, "추석"), (2027, 9, 16, "추석 연휴"),
            (2027, 10, 3, "개천절"), (2027, 10, 4, "대체공휴일(개천절)"), (2027, 10, 9, "한글날"), (2027, 10, 11, "대체공휴일(한글날)"),
            (2027, 12, 25, "성탄절"), (2027, 12, 27, "대체공휴일(성탄절)"),
        };
        foreach (var r in rows) yield return (new DateOnly(r.y, r.m, r.d), r.n);
    }

    // 미리보기·개발용 샘플 과제 — 과제가 하나도 없을 때만 넣는다. 날짜는 오늘 기준 상대값
    public static async Task SeedDemoAsync(AppDb db, IClock clock)
    {
        if (await db.Projects.AnyAsync()) return;

        var today = clock.Today;
        var now = clock.UtcNow;
        DateOnly D(int offset) => today.AddDays(offset);

        // 조직: 그룹 → 파트
        OrgUnit Org(string name, int order, OrgUnit? parent = null)
        {
            var u = new OrgUnit { Name = name, Level = parent == null ? 1 : 2, Parent = parent, SortOrder = order };
            db.OrgUnits.Add(u);
            return u;
        }
        var logistics = Org("물류기술그룹", 1);
        var autoPart = Org("자동화파트", 1, logistics);
        var innoPart = Org("물류혁신파트", 2, logistics);
        var facility = Org("설비기술그룹", 2);
        var facilityPart = Org("설비파트", 1, facility);

        // 사용자 (접속 IP 없이 담당자로만 등록)
        var kim = new AppUser { Name = "김물류", OrgUnit = autoPart, CreatedAt = now };
        var lee = new AppUser { Name = "이자동", OrgUnit = autoPart, CreatedAt = now };
        var park = new AppUser { Name = "박설비", OrgUnit = facilityPart, CreatedAt = now };
        var choi = new AppUser { Name = "최모델", OrgUnit = innoPart, CreatedAt = now };
        db.Users.AddRange(kim, lee, park, choi);

        // 위치 계층
        Location Corp(string name, string country, int lead, int order)
        {
            var l = new Location { Level = 1, Name = name, Country = country, OceanLeadDays = lead, SortOrder = order };
            db.Locations.Add(l);
            return l;
        }
        Location Child(Location parent, string name, int order)
        {
            var l = new Location { Level = parent.Level + 1, Name = name, Parent = parent, SortOrder = order };
            db.Locations.Add(l);
            return l;
        }

        var kr = Corp("한국", "대한민국", 0, 1);
        var vn = Corp("베트남", "베트남", 12, 2);
        var mx = Corp("멕시코", "멕시코", 35, 3);
        var pl = Corp("폴란드", "폴란드", 45, 4);

        var krRef = Child(kr, "냉장고", 1);
        var krAc = Child(kr, "에어컨", 2);
        var krRef3 = Child(krRef, "3라인", 1);
        var krRef3F1 = Child(krRef3, "1F", 1);
        Child(krRef3, "2F", 2);
        var krRef5 = Child(krRef, "5라인", 2);
        var krAc1 = Child(krAc, "1라인", 1);
        var vnWash = Child(vn, "세탁기", 1);
        var vnRef = Child(vn, "냉장고", 2);
        var vnWash2 = Child(vnWash, "2라인", 1);
        var vnRef1 = Child(vnRef, "1라인", 1);
        var mxAc = Child(mx, "에어컨", 1);
        var mxRef = Child(mx, "냉장고", 2);
        var plWash = Child(pl, "세탁기", 1);

        foreach (var name in new[] { "한성로보틱스", "대명오토메이션", "동양용기" })
            db.Vendors.Add(new Vendor { Name = name });

        await db.SaveChangesAsync();

        // 컬렉션 두 겹(템플릿 → 체크 항목)은 나눠 읽는 방식을 정해 둔다 (정하지 않으면 EF 가 시작할 때마다 경고한다)
        var cats = await db.Categories.Include(c => c.StageTemplates).ThenInclude(t => t.CheckItems).AsSplitQuery().ToListAsync();
        var cal = new WorkCalendar((await db.Holidays.ToListAsync()).Select(h => h.Date));
        var equip = await db.EquipmentTypes.Include(e => e.Parent).ToListAsync();
        var cont = await db.ContainerTypes.ToListAsync();
        Category CatOf(string name) => cats.First(c => c.Name == name);
        EquipmentType Eq(string parent, string name) => equip.First(e => e.Name == name && e.Parent != null && e.Parent.Name == parent);
        ContainerType Ct(string name) => cont.First(c => c.Name == name);

        // 과제 생성: 유형의 단계 템플릿을 복사하고, (목표일 오프셋, 완료일 오프셋)을 순서대로 채운다
        Project Make(string title, string category, Location corp, Location loc, AppUser owner, int startOffset,
                     ProjectState state, params (int? Target, int? Done)[] dates)
        {
            var cat = CatOf(category);
            var p = new Project
            {
                Title = title, CategoryId = cat.Id, CorporationId = corp.Id, LocationId = loc.Id,
                OwnerId = owner.Id, CreatedById = owner.Id, State = state, StartDate = D(startOffset),
                CreatedAt = now.AddDays(startOffset), UpdatedAt = now,
            };
            var i = 0;
            foreach (var t in cat.StageTemplates.OrderBy(t => t.SortOrder))
            {
                var d = i < dates.Length ? dates[i] : (null, null);
                var stage = new Stage
                {
                    Name = t.Name, Kind = t.Kind, SortOrder = t.SortOrder,
                    TargetDate = d.Target == null ? null : D(d.Target.Value),
                    CompletedDate = d.Done == null ? null : D(d.Done.Value),
                };
                // 체크리스트 복사 — 완료한 단계는 모두 체크된 상태로
                foreach (var c in t.CheckItems.OrderBy(c => c.SortOrder))
                {
                    stage.Checks.Add(new CheckItem
                    {
                        Text = c.Text, SortOrder = c.SortOrder, IsDone = d.Done != null,
                        DoneById = d.Done != null ? owner.Id : null,
                        DoneAt = d.Done != null ? now.AddDays(d.Done.Value) : null,
                    });
                }
                p.Stages.Add(stage);
                i++;
            }
            p.Activities.Add(new Activity { Type = ActivityType.Created, Text = "과제 등록", UserId = owner.Id, At = now.AddDays(startOffset) });
            db.Projects.Add(p);
            return p;
        }

        WorkItem Work(Project p, string stage, string title, AppUser owner, int planStart, int planEnd,
                      int? actualStart = null, int? actualEnd = null, int progress = 0, AppUser? co = null)
        {
            var w = new WorkItem
            {
                Title = title, OwnerId = owner.Id, CoOwnerId = co?.Id,
                PlanStart = D(planStart), PlanEnd = D(planEnd),
                ActualStart = actualStart == null ? null : D(actualStart.Value),
                ActualEnd = actualEnd == null ? null : D(actualEnd.Value),
                Progress = actualEnd != null ? 100 : progress, CreatedAt = now,
            };
            p.Stages.First(s => s.Name == stage).Works.Add(w);
            return w;
        }

        // 기준선: 단계별 최초 목표일 오프셋을 순서대로 지정. 작업 기준선은 단계가 밀린 만큼 앞당겨 잡는다
        void Baseline(Project p, params int?[] offsets)
        {
            p.BaselineAt = p.CreatedAt.AddDays(1);
            var i = 0;
            foreach (var s in p.Stages.OrderBy(s => s.SortOrder))
            {
                s.BaselineDate = i < offsets.Length && offsets[i] != null ? D(offsets[i]!.Value) : s.TargetDate;
                var slip = s.BaselineDate != null && s.TargetDate != null ? s.TargetDate.Value.DayNumber - s.BaselineDate.Value.DayNumber : 0;
                foreach (var w in s.Works)
                {
                    w.BaselineStart = w.PlanStart.AddDays(-slip);
                    w.BaselineEnd = w.PlanEnd.AddDays(-slip);
                }
                i++;
            }
        }

        // 의존 관계: 서비스와 같은 규칙(Rules.Reschedule)으로 아직 시작하지 않은 후행 작업을 기간을 유지한 채 뒤로 민다
        void Link(Project p, WorkItem from, WorkItem to)
        {
            p.Links.Add(new WorkLink { Predecessor = from, Successor = to });
            foreach (var m in Rules.Reschedule(p, cal, new[] { to }))
            {
                m.Work.PlanStart = m.NewStart;
                m.Work.PlanEnd = m.NewEnd;
            }
        }

        void Note(Project p, ActivityType type, string text, AppUser user, int dayOffset, string? reason = null)
            => p.Activities.Add(new Activity { Type = type, Text = text, Reason = reason, UserId = user.Id, At = now.AddDays(dayOffset) });

        // 1) 정상 진행 — 검수 단계, 세부 작업 있음
        var p1 = Make("광주 냉장고 3라인 AGV 도입", "물류 자동화", kr, krRef3F1, kim, -120, ProjectState.Active,
            (-100, -102), (-80, -78), (-60, -60), (-10, -8), (5, null), (12, null), (30, null), (50, null));
        p1.Vendor = "한성로보틱스";
        p1.Description = "냉장고 3라인 1층 자재 공급을 AGV 12대로 전환";
        p1.Equipments.Add(new ProjectEquipment { EquipmentTypeId = Eq("AGV", "ANT").Id, Quantity = 12 });
        var fat = Work(p1, "검수", "FAT 준비", lee, -7, -3, -6, -3);
        var drive = Work(p1, "검수", "주행 시험", lee, -2, 3, -1, null, 40, park);
        Work(p1, "검수", "검수 문서 검토", kim, 1, 5);
        Link(p1, fat, drive);
        Baseline(p1);
        var firstCheck = p1.Stages.First(s => s.Name == "검수").Checks.First();
        firstCheck.IsDone = true;
        firstCheck.DoneById = lee.Id;
        firstCheck.DoneAt = now.AddDays(-3);
        Note(p1, ActivityType.Comment, "주행 시험 1차 결과 양호. 곡선 구간 속도만 재확인 예정", lee, -1);

        // 2) 지연 — 제작 단계가 목표일을 넘김, 세부 작업도 지연
        var p2 = Make("베트남 세탁기 2라인 AMR 확대", "물류 자동화", vn, vnWash2, lee, -90, ProjectState.Active,
            (-75, -76), (-60, -58), (-45, -40), (-6, null), (10, null), (25, null), (40, null), (60, null));
        p2.Vendor = "대명오토메이션";
        p2.Equipments.Add(new ProjectEquipment { EquipmentTypeId = Eq("AMR", "Belt").Id, Quantity = 8 });
        p2.Equipments.Add(new ProjectEquipment { EquipmentTypeId = Eq("AMR", "Roller").Id, Quantity = 4 });
        var frame = Work(p2, "제작", "프레임 제작", park, -38, -24, -38, -21);
        var controller = Work(p2, "제작", "제어기 조립", park, -22, -9, -19, null, 70);
        var shipping = Work(p2, "제작", "출하 검사", lee, -8, -6);
        Link(p2, frame, controller);
        Link(p2, controller, shipping);
        Baseline(p2, -75, -60, -45, -20, -4, 11, 26, 46);   // 제작부터 2주씩 밀린 상태
        Note(p2, ActivityType.TargetChanged, "제작 목표일 변경", lee, -30, "협력사 부품 수급 지연으로 2주 순연");
        Note(p2, ActivityType.Comment, "제어기 보드 입고 완료, 조립 재개", park, -12);

        // 3) 목표일 임박 — 용기
        var p3 = Make("멕시코 에어컨 용기 표준화", "용기", mx, mxAc, kim, -45, ProjectState.Active,
            (-30, -31), (-12, -12), (3, null), (30, null), (65, null), (80, null));
        p3.Vendor = "동양용기";
        p3.Containers.Add(new ProjectContainer { ContainerTypeId = Ct("파렛트").Id, Quantity = 400 });
        p3.Containers.Add(new ProjectContainer { ContainerTypeId = Ct("플라스틱 박스").Id, Quantity = 1200 });

        // 4) Simulation — 모델링 진행 중
        var p4 = Make("광주 2공장 물류 동선 Simulation", "Simulation", kr, krRef5, choi, -30, ProjectState.Active,
            (-14, -15), (7, null), (20, null), (28, null));
        var layout = Work(p4, "모델링", "레이아웃 반영", choi, -14, -8, -14, -9);
        var logic = Work(p4, "모델링", "AGV 로직 구현", choi, -8, 4, -8, null, 55);
        var scenario = Work(p4, "모델링", "시나리오 정의", kim, 2, 7);
        Baseline(p4, -14, 4, 17, 25);
        Link(p4, layout, logic);
        Link(p4, logic, scenario);

        // 5) 지연 — 첫 단계부터 밀림
        Make("폴란드 세탁기 창고 입출고 개선", "물류", pl, plWash, kim, -20, ProjectState.Active,
            (-3, null), (10, null), (30, null));

        // 6) 보류
        var p6 = Make("한국 에어컨 1라인 리프터 교체", "물류 자동화", kr, krAc1, park, -60, ProjectState.Hold,
            (-45, -44), (-20, null), (null, null), (null, null), (null, null), (null, null), (null, null), (null, null));
        p6.Equipments.Add(new ProjectEquipment { EquipmentTypeId = equip.First(e => e.Name == "리프터").Id, Quantity = 2 });
        Note(p6, ActivityType.StateChanged, "상태 변경: 진행 → 보류", park, -18, "투자 우선순위 조정으로 내년 재검토");

        // 7) 완료 — 통계용
        Make("베트남 냉장고 1라인 AGV 2차 증설", "물류 자동화", vn, vnRef1, lee, -230, ProjectState.Done,
            (-215, -214), (-195, -196), (-180, -178), (-120, -117), (-105, -104), (-90, -88), (-60, -61), (-30, -28));

        // 8) 완료 — 일반 물류
        Make("광주 냉장고 용기 회수 프로세스 개선", "물류", kr, krRef, kim, -100, ProjectState.Done,
            (-80, -82), (-60, -57), (-35, -36));

        // 9) 막 등록 — 목표일 대부분 미정
        Make("멕시코 냉장고 AGV 도입 검토", "물류 자동화", mx, mxRef, kim, -2, ProjectState.Active,
            (14, null));

        await db.SaveChangesAsync();
    }
}
