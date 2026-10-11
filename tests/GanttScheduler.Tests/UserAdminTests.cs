using System.Text;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GanttScheduler.Tests;

// 사용자 정리(병합·담당 이관 후 사용 중지)·관리 변경 이력·사용 수·사용 현황·IP 보존 정리·과제 첨부 상한
public class UserAdminTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;
    private DateOnly D(int offset) => Today.AddDays(offset);

    public UserAdminTests(TestDb db) => _db = db;

    private static MasterSave MS(string kind, int level, int? id, int? parentId, string name,
        string? Country = null, int? OceanLeadDays = null, bool? IsAdmin = null, DateOnly? Date = null) =>
        new(kind, level, id, parentId, name, Country, OceanLeadDays, null, null, IsAdmin, Date);

    private UserAdminService People(TestDb.Scope s) => new(s.Db, _db.Clock, s.Me);
    private MasterLog Log(TestDb.Scope s) => new(s.Db, _db.Clock, s.Me);

    private async Task<int> UserAsync(string name, bool admin = false)
    {
        using var s = _db.NewScope(name, admin);
        return s.Me.Id;
    }

    private async Task<(int Id, int StageId)> NewProjectAsync(string title, string owner)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, owner, D(-10), null, null, null, null));
        return (id, (await s.Projects.ViewAsync(id))!.Stages[0].Id);
    }

    private async Task<int> AddWorkAsync(int stageId, string title, string? owner, string? coOwner = null, DateOnly? actualEnd = null)
    {
        using var s = _db.NewScope();
        await s.Projects.SaveWorkAsync(new WorkInput(null, stageId, title, owner, coOwner, D(-3), D(5), actualEnd == null ? null : D(-3), actualEnd,
            actualEnd == null ? 0 : 100, null, null));
        return await s.Db.WorkItems.Where(w => w.StageId == stageId && w.Title == title).Select(w => w.Id).FirstAsync();
    }

    // EF 모델에서 AppUser 를 가리키는 FK, 그리고 FK 없이 사용자 Id 를 담는 열(이름이 …UserId·OwnerId·…ById)
    private static List<(string Ref, string Table, string Column)> UserColumns(AppDb db)
    {
        var list = new List<(string, string, string)>();
        foreach (var e in db.Model.GetEntityTypes())
        {
            var table = e.GetTableName()!;
            var fkProps = e.GetForeignKeys().SelectMany(fk => fk.Properties.Select(p => (p, fk))).ToList();
            foreach (var p in e.GetProperties())
            {
                if (p.ClrType != typeof(int) && p.ClrType != typeof(int?)) continue;
                var fk = fkProps.FirstOrDefault(x => x.p == p).fk;
                var toUser = fk?.PrincipalEntityType.ClrType == typeof(AppUser);
                var looksLikeUser = fk == null && (p.Name.EndsWith("UserId") || p.Name == "OwnerId" || p.Name.EndsWith("ById"));
                if (toUser || looksLikeUser)
                    list.Add(($"{e.ClrType.Name}.{p.Name}", table, p.GetColumnName()));
            }
        }
        return list;
    }

    // 표·열 이름은 EF 모델에서 온 값이라(입력 아님) 그대로 넣고, 값만 매개변수로 넘긴다
#pragma warning disable EF1003
    private static async Task<int> CountAsync(AppDb db, string table, string column, int id) =>
        await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"" + table + "\" WHERE \"" + column + "\" = {0}", id).SingleAsync();
#pragma warning restore EF1003

    [Fact]
    public void 병합이_옮기는_참조_목록은_EF_모델의_사용자_참조와_같다()
    {
        using var s = _db.NewScope();
        var found = UserColumns(s.Db).Select(x => x.Ref).OrderBy(x => x).ToList();
        Assert.Equal(UserAdminService.References.OrderBy(x => x).ToList(), found);
    }

    [Fact]
    public async Task 병합은_사용자를_가리키는_모든_행을_옮기고_유일_색인_충돌은_합친다()
    {
        var fromId = await UserAsync("병합 오타", admin: true);
        var intoId = await UserAsync("병합 원본");
        var (pid, stageId) = await NewProjectAsync("병합 과제", "병합 오타");
        var both = await AddWorkAsync(stageId, "같아지는 작업", "병합 오타", "병합 원본");
        var co = await AddWorkAsync(stageId, "부담당 작업", "병합 원본", "병합 오타");
        int workVersion;
        using (var s = _db.NewScope("병합 오타", admin: true))
        {
            workVersion = (await s.Db.WorkItems.SingleAsync(w => w.Id == both)).Version;
            // 댓글(활동), 원본을 멘션
            await s.Projects.CommentAsync(new CommentInput(pid, null, null, "오타 이름으로 남긴 댓글", new List<int> { intoId }));
            await s.Projects.WatchAsync(pid, true);
            await s.Projects.OpenAsync(pid);
        }
        using (var s = _db.NewScope("병합 원본"))
        {
            // 원본도 같은 과제를 보고 관심 과제로 둔다 — (사용자, 과제) 유일 색인이 겹친다. 오타를 멘션한 댓글 → 오타에게 알림
            await s.Projects.WatchAsync(pid, true);
            await s.Projects.OpenAsync(pid);
            await s.Projects.CommentAsync(new CommentInput(pid, null, null, "오타에게", new List<int> { fromId }));
        }
        using (var s = _db.NewScope())
        {
            // 등록자·지운 사람(휴지통 과제의 기록)도 오타 사용자로 둔다
            var proj = await s.Db.Projects.SingleAsync(x => x.Id == pid);
            proj.CreatedById = fromId;
            proj.DeletedById = fromId;
            s.Db.CheckItems.Add(new CheckItem { StageId = stageId, Text = "병합 체크", IsDone = true, DoneById = fromId, DoneAt = _db.Clock.UtcNow });
            s.Db.Attachments.Add(new Attachment { ProjectId = pid, FileName = "a.txt", StoredName = "x.txt", ContentType = "text/plain", Size = 1, UploadedById = fromId, UploadedAt = _db.Clock.UtcNow });
            s.Db.Baselines.Add(new Baseline { ProjectId = pid, No = 99, At = _db.Clock.UtcNow, UserId = fromId });
            s.Db.UserAddresses.Add(new UserAddress { Ip = "10.88.0.1", UserId = fromId, LastSeenAt = _db.Clock.UtcNow });
            s.Db.ProjectSnapshots.Add(new ProjectSnapshot { Date = Today, ProjectId = pid, OwnerId = fromId, CategoryId = 1, CorporationId = 1 });
            s.Db.MasterChanges.Add(new MasterChange { At = _db.Clock.UtcNow, UserId = fromId, Kind = "user", Text = "오타가 남긴 관리 변경" });
            await s.Db.SaveChangesAsync();
            // 시험 준비가 모든 참조 열을 채웠는지 — 새 참조가 생기면 여기에도 행을 더해야 병합 시험이 의미가 있다
            foreach (var (refName, table, column) in UserColumns(s.Db))
                Assert.True(await CountAsync(s.Db, table, column, fromId) > 0, $"{refName} 에 시험 행이 없습니다");
        }

        int projectVersion;
        using (var s = _db.NewScope())
            projectVersion = (await s.Db.Projects.SingleAsync(p => p.Id == pid)).Version;

        using (var s = _db.NewScope(admin: true))
        {
            await Assert.ThrowsAsync<RuleException>(() => People(s).MergeAsync(new MergeInput(fromId, fromId, null)));
            await Assert.ThrowsAsync<RuleException>(() => People(s).MergeAsync(new MergeInput(s.Me.Id, intoId, null)));
            var result = await People(s).MergeAsync(new MergeInput(fromId, intoId, "이름 오타"));
            Assert.Equal(1, result.Projects);
            Assert.Contains("병합 오타 님을 병합 원본 님으로 합쳤습니다", result.Text);
        }

        using (var s = _db.NewScope())
        {
            // 병합 뒤 from 을 가리키는 행 0 (모든 참조 열)
            foreach (var (refName, table, column) in UserColumns(s.Db))
                Assert.True(await CountAsync(s.Db, table, column, fromId) == 0, $"{refName} 에 옮기지 않은 행이 남았습니다");
            Assert.False(await s.Db.Users.AnyAsync(u => u.Id == fromId));
            var into = await s.Db.Users.SingleAsync(u => u.Id == intoId);
            Assert.True(into.IsAdmin);                                   // 관리자 권한은 합집합
            Assert.Equal("10.88.0.1", (await s.Db.UserAddresses.SingleAsync(a => a.UserId == intoId)).Ip);
            // 유일 색인 충돌은 하나로
            Assert.Equal(1, await s.Db.ProjectWatches.CountAsync(w => w.UserId == intoId && w.ProjectId == pid));
            Assert.Equal(1, await s.Db.ProjectVisits.CountAsync(v => v.UserId == intoId && v.ProjectId == pid));
            // 담당 = 부담당이 되면 부담당을 비운다, 작업 Version++
            var w1 = await s.Db.WorkItems.SingleAsync(w => w.Id == both);
            Assert.Equal(intoId, w1.OwnerId);
            Assert.Null(w1.CoOwnerId);
            Assert.True(w1.Version > workVersion);
            var w2 = await s.Db.WorkItems.SingleAsync(w => w.Id == co);
            Assert.Equal(intoId, w2.OwnerId);
            Assert.Null(w2.CoOwnerId);
            var p = await s.Db.Projects.SingleAsync(x => x.Id == pid);
            Assert.Equal(intoId, p.OwnerId);
            Assert.True(p.Version > projectVersion);
            // 바뀐 과제마다 활동 한 건
            var acts = await s.Db.Activities.Where(a => a.ProjectId == pid && a.Text.StartsWith("사용자 병합")).ToListAsync();
            Assert.Single(acts);
            Assert.Equal("사용자 병합: 병합 오타 → 병합 원본", acts[0].Text);
            Assert.Equal("이름 오타", acts[0].Reason);
            // 관리 변경 이력
            Assert.Contains(await s.Db.MasterChanges.Where(c => c.Kind == "user").ToListAsync(), c => c.Text.StartsWith("사용자 병합: 병합 오타 → 병합 원본"));
        }
    }

    [Fact]
    public async Task 담당_이관은_진행_과제와_미완료_작업만_넘기고_사용_중지한다()
    {
        var fromId = await UserAsync("이관 퇴사");
        var toId = await UserAsync("이관 후임");
        var (open, stageId) = await NewProjectAsync("이관 진행 과제", "이관 퇴사");
        var (done, _) = await NewProjectAsync("이관 완료 과제", "이관 퇴사");
        var openWork = await AddWorkAsync(stageId, "이관 열린 작업", "이관 퇴사");
        var coWork = await AddWorkAsync(stageId, "이관 부담당 작업", "이관 후임", "이관 퇴사");
        var closedWork = await AddWorkAsync(stageId, "이관 끝난 작업", "이관 퇴사", actualEnd: D(-1));
        using (var s = _db.NewScope())
        {
            (await s.Db.Projects.SingleAsync(p => p.Id == done)).State = ProjectState.Done;
            await s.Db.SaveChangesAsync();
        }

        using (var s = _db.NewScope(admin: true))
        {
            var load = await People(s).LoadAsync(fromId);
            Assert.Equal(1, load.Projects);
            Assert.Equal(2, load.Works);
            var users = await s.Masters.ListAsync("user");
            Assert.Equal(1, users.Single(u => u.Id == fromId).OpenProjects);
            Assert.Equal(2, users.Single(u => u.Id == fromId).OpenWorks);
            // 맡은 일이 있으면 넘겨받을 사람이 필요하다
            var missing = await Assert.ThrowsAsync<RuleException>(() => People(s).ReassignAsync(new ReassignInput(fromId, null, true, true, null)));
            Assert.Equal("toId", missing.Field);
            var r = await People(s).ReassignAsync(new ReassignInput(fromId, toId, true, true, "퇴사"));
            Assert.Equal(1, r.Projects);
            Assert.Equal(2, r.Works);
        }

        using (var s = _db.NewScope())
        {
            Assert.False((await s.Db.Users.SingleAsync(u => u.Id == fromId)).IsActive);
            Assert.Equal(toId, (await s.Db.Projects.SingleAsync(p => p.Id == open)).OwnerId);
            Assert.Equal(fromId, (await s.Db.Projects.SingleAsync(p => p.Id == done)).OwnerId);      // 완료 과제는 기록으로 남긴다
            Assert.Equal(toId, (await s.Db.WorkItems.SingleAsync(w => w.Id == openWork)).OwnerId);
            var cw = await s.Db.WorkItems.SingleAsync(w => w.Id == coWork);
            Assert.Equal(toId, cw.OwnerId);
            Assert.Null(cw.CoOwnerId);                                          // 넘겨받는 사람이 담당이면 부담당을 비운다
            Assert.Equal(fromId, (await s.Db.WorkItems.SingleAsync(w => w.Id == closedWork)).OwnerId);
            // 한 묶음(BatchId)의 활동 3건, 받는 사람에게 알림
            var acts = await s.Db.Activities.Where(a => a.Text.Contains("이관: 이관 퇴사 → 이관 후임")).ToListAsync();
            Assert.Equal(3, acts.Count);
            Assert.Single(acts.Select(a => a.BatchId).Distinct());
            Assert.NotNull(acts[0].BatchId);
            Assert.True(await s.Db.Notifications.AnyAsync(n => n.UserId == toId && n.ProjectId == open));
            Assert.Contains(await s.Db.MasterChanges.Where(c => c.EntityId == fromId).ToListAsync(), c => c.Text.StartsWith("담당 이관 후 사용 중지"));
        }

        // 맡은 일이 없으면 넘겨받을 사람 없이 바로 사용 중지
        var idle = await UserAsync("이관 한가");
        using (var s = _db.NewScope(admin: true))
        {
            var r = await People(s).ReassignAsync(new ReassignInput(idle, null, true, true, null));
            Assert.Equal(0, r.Rows);
            Assert.False((await s.Db.Users.AsNoTracking().SingleAsync(u => u.Id == idle)).IsActive);
            // 사용 중지된 사람에게는 넘기지 않는다
            await Assert.ThrowsAsync<RuleException>(() => People(s).ReassignAsync(new ReassignInput(toId, idle, true, true, null)));
        }
        // 관리자만
        using (var s = _db.NewScope("이관 일반"))
            await Assert.ThrowsAsync<RuleException>(() => People(s).ReassignAsync(new ReassignInput(toId, fromId, true, true, null)));
    }

    [Fact]
    public async Task 관리_변경은_지점마다_변경_이력을_남기고_조건으로_찾는다()
    {
        int corpId, catId, holidayId;
        using (var s = _db.NewScope(admin: true))
        {
            var before = await s.Db.MasterChanges.CountAsync();
            // 계층 추가·해상 운송일 수정
            await s.Masters.SaveAsync(MS("location", 1, null, null, "이력 법인", Country: "VN", OceanLeadDays: 10));
            corpId = await s.Db.Locations.Where(l => l.Name == "이력 법인").Select(l => l.Id).SingleAsync();
            await s.Masters.SaveAsync(MS("location", 1, corpId, null, "이력 법인", Country: "VN", OceanLeadDays: 14));
            // 유형 추가 → 순서 이동 → 사용 중지
            await s.Masters.SaveAsync(MS("category", 1, null, null, "이력 유형"));
            catId = await s.Db.Categories.Where(c => c.Name == "이력 유형").Select(c => c.Id).SingleAsync();
            await s.Masters.MoveAsync(new MasterRef("category", 1, catId, -1));
            await s.Masters.ToggleAsync(new MasterRef("category", 1, catId, 0, false));
            // 공휴일 추가·삭제·붙여넣기
            await s.Masters.SaveAsync(MS("holiday", 1, null, null, "이력 휴일", Date: new DateOnly(2031, 3, 3)));
            holidayId = await s.Db.Holidays.Where(h => h.Name == "이력 휴일").Select(h => h.Id).SingleAsync();
            await s.Masters.ToggleAsync(new MasterRef("holiday", 1, holidayId, 0, null));
            await s.Masters.PasteHolidaysAsync("2031-05-01 이력 근로자의 날\n2031-05-05 이력 어린이날");
            // 사용자: 관리자 지정, IP 연결·해제
            var uid = await UserAsync("이력 사용자");
            var u = await s.Db.Users.AsNoTracking().SingleAsync(x => x.Id == uid);
            await s.Masters.SaveAsync(MS("user", 1, uid, null, u.Name, IsAdmin: true));
            await s.Masters.AddAddressAsync(new MasterIp(uid, "10.66.1.2"));
            await s.Masters.RemoveAddressAsync(new MasterIp(uid, "10.66.1.2"));

            var rows = await s.Db.MasterChanges.AsNoTracking().OrderBy(c => c.Id).Skip(before).ToListAsync();
            string[] expected =
            {
                "법인 추가: 이력 법인", "법인 '이력 법인' 수정: 해상 운송 소요일", "유형 추가: 이력 유형", "유형 순서 위로: 이력 유형", "유형 사용 중지: 이력 유형",
                "공휴일 추가: 2031-03-03 이력 휴일", "공휴일 삭제: 2031-03-03 이력 휴일", "공휴일 붙여넣기: 2건 추가",
                "관리자 지정: 이력 사용자", "IP 연결: 이력 사용자 — 10.66.1.2", "IP 연결 해제: 이력 사용자 — 10.66.1.2",
            };
            foreach (var text in expected) Assert.Contains(rows, r => r.Text == text);
            Assert.All(rows, r => Assert.Equal(s.Me.Id, r.UserId));
            var lead = rows.Single(r => r.Text.StartsWith("법인 '이력 법인' 수정"));
            Assert.Equal(("10", "14"), (lead.OldValue, lead.NewValue));
            Assert.Equal(corpId, lead.EntityId);
            Assert.Equal(catId, rows.Single(r => r.Text == "유형 추가: 이력 유형").EntityId);      // 새 항목의 Id 도 채운다
            Assert.Contains("국가 VN", rows.Single(r => r.Text == "법인 추가: 이력 법인").NewValue);
            // 바꾼 것이 없는 저장은 남기지 않는다
            var n = await s.Db.MasterChanges.CountAsync();
            await s.Masters.SaveAsync(MS("location", 1, corpId, null, "이력 법인", Country: "VN", OceanLeadDays: 14));
            Assert.Equal(n, await s.Db.MasterChanges.CountAsync());

            // 조회: 종류·사람·기간·쪽
            var log = Log(s);
            var holidays = await log.ListAsync(new MasterChangeFilter(Kind: "holiday"));
            Assert.All(holidays.Items, i => Assert.Equal("공휴일", i.KindName));
            Assert.Contains(holidays.Items, i => i.Text == "공휴일 붙여넣기: 2건 추가");
            var mine = await log.ListAsync(new MasterChangeFilter(UserId: s.Me.Id, From: Today, To: Today));
            Assert.True(mine.Total >= expected.Length);
            Assert.Equal(s.Me.Name, mine.Items[0].User);
            Assert.Empty((await log.ListAsync(new MasterChangeFilter(From: D(5), To: D(6)))).Items);
            await Assert.ThrowsAsync<RuleException>(() => log.ListAsync(new MasterChangeFilter(From: D(2), To: D(1))));
            Assert.Contains(await log.PeopleAsync(), p => p.Id == s.Me.Id);
        }
        // 관리자만 본다
        using (var s = _db.NewScope("이력 일반"))
            await Assert.ThrowsAsync<RuleException>(() => Log(s).ListAsync(new MasterChangeFilter()));
    }

    [Fact]
    public async Task 이름_등록과_담당_입력으로_생긴_사용자도_변경_이력에_남는다()
    {
        using (var s = _db.NewScope())
        {
            await s.Users.RegisterAsync("10.55.0.7", "이력 등록자", null);
            var reg = await s.Db.MasterChanges.AsNoTracking().Include(c => c.User).SingleAsync(c => c.Text == "사용자 등록: 이력 등록자 (10.55.0.7)");
            Assert.Equal("이력 등록자", reg.User!.Name);             // 행위자는 등록한 본인
            Assert.Equal(reg.UserId, reg.EntityId);
            // 같은 사람이 다른 PC 에서 등록 → IP 연결
            await s.Users.RegisterAsync("10.55.0.8", "이력 등록자", null);
            Assert.True(await s.Db.MasterChanges.AnyAsync(c => c.Kind == "ip" && c.Text == "IP 연결(이름 등록 화면): 이력 등록자 — 10.55.0.8"));
        }
        var (_, stageId) = await NewProjectAsync("이력 담당 과제", "테스터");
        await AddWorkAsync(stageId, "새 담당 작업", "이력 새담당");
        using (var s = _db.NewScope())
        {
            var row = await s.Db.MasterChanges.AsNoTracking().SingleAsync(c => c.Text == "사용자 추가(담당 입력): 이력 새담당");
            Assert.Equal(await s.Db.Users.Where(u => u.Name == "이력 새담당").Select(u => u.Id).SingleAsync(), row.EntityId);
        }
    }

    [Fact]
    public async Task admin_명령도_변경_이력에_서버로_남는다()
    {
        using var db = _db.Open();
        await AdminCommand.ApplyAsync(db, _db.Clock, "이력 콘솔관리자", "10.44.0.9");
        var row = await db.MasterChanges.AsNoTracking().SingleAsync(c => c.Text.StartsWith("admin 명령(서버 콘솔): 이력 콘솔관리자"));
        Assert.Null(row.UserId);
        Assert.Contains("관리자 지정", row.Text);
        Assert.Contains("IP 연결 10.44.0.9", row.Text);
    }

    [Fact]
    public async Task 기준_정보_목록은_그_항목을_쓰는_과제_수를_싣는다()
    {
        string catName = "사용 수 유형";
        int catId, corpId;
        using (var s = _db.NewScope(admin: true))
        {
            await s.Masters.SaveAsync(MS("category", 1, null, null, catName));
            catId = await s.Db.Categories.Where(c => c.Name == catName).Select(c => c.Id).SingleAsync();
            await s.Masters.SaveAsync(MS("location", 1, null, null, "사용 수 법인"));
            corpId = await s.Db.Locations.Where(l => l.Name == "사용 수 법인").Select(l => l.Id).SingleAsync();
            await s.Masters.SaveAsync(MS("location", 2, null, corpId, "사용 수 제품군"));
        }
        int lineId;
        using (var s = _db.NewScope())
            lineId = await s.Db.Locations.Where(l => l.Name == "사용 수 제품군").Select(l => l.Id).SingleAsync();
        int deleted;
        using (var s = _db.NewScope())
        {
            await s.Projects.CreateAsync(new ProjectInput("사용 수 1", catId, corpId, "테스터", D(0), null, null, null, null));
            await s.Projects.CreateAsync(new ProjectInput("사용 수 2", catId, lineId, "테스터", D(0), null, null, null, null));
            deleted = await s.Projects.CreateAsync(new ProjectInput("사용 수 지움", catId, lineId, "테스터", D(0), null, null, null, null));
            await s.Projects.DeleteAsync(deleted, "시험");
        }
        using (var s = _db.NewScope(admin: true))
        {
            var cats = await s.Masters.ListAsync("category");
            Assert.Equal(2, cats.Single(n => n.Level == 1 && n.Id == catId).Uses);       // 휴지통 과제는 세지 않는다
            var locs = await s.Masters.ListAsync("location");
            Assert.Equal(2, locs.Single(n => n.Id == corpId).Uses);                    // 하위 위치의 과제를 상위로 올려 센다
            Assert.Equal(1, locs.Single(n => n.Id == lineId).Uses);
        }
    }

    [Fact]
    public async Task 사용_현황은_조직별_접속자_갱신_조용한_과제를_센다()
    {
        int orgId;
        using (var s = _db.NewScope(admin: true))
        {
            await s.Masters.SaveAsync(MS("org", 1, null, null, "현황 그룹"));
            orgId = await s.Db.OrgUnits.Where(o => o.Name == "현황 그룹").Select(o => o.Id).SingleAsync();
        }
        var a = await UserAsync("현황 접속");
        var b = await UserAsync("현황 미접속");
        var (busy, _) = await NewProjectAsync("현황 바쁜 과제", "현황 접속");
        var (quiet, _) = await NewProjectAsync("현황 조용한 과제", "현황 미접속");
        using (var s = _db.NewScope())
        {
            foreach (var u in await s.Db.Users.Where(u => u.Id == a || u.Id == b).ToListAsync()) u.OrgUnitId = orgId;
            s.Db.UserAddresses.Add(new UserAddress { Ip = "10.33.0.1", UserId = a, LastSeenAt = _db.Clock.UtcNow.AddDays(-2) });
            s.Db.UserAddresses.Add(new UserAddress { Ip = "10.33.0.2", UserId = b, LastSeenAt = _db.Clock.UtcNow.AddDays(-40) });
            (await s.Db.Projects.SingleAsync(p => p.Id == quiet)).UpdatedAt = _db.Clock.UtcNow.AddDays(-45);
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope("현황 접속"))
        {
            await s.Projects.CommentAsync(new CommentInput(busy, null, null, "현황 갱신 1"));
            await s.Projects.CommentAsync(new CommentInput(busy, null, null, "현황 갱신 2"));
        }
        using (var s = _db.NewScope("현황 일반"))
        {
            var r = await People(s).UsageAsync();
            Assert.Null(r.People);                                  // 사람별은 관리자만
            Assert.Equal(30, r.Days);
            var row = r.Orgs.Single(o => o.Name == "현황 그룹");
            Assert.Equal(2, row.Users);
            Assert.Equal(1, row.ActiveUsers);
            Assert.Equal(50, row.ActivePercent);
            // 과제 등록 활동은 테스터 이름이라 세지 않는다 — 접속자의 댓글 2건
            Assert.Equal(2, row.Updates);
            Assert.Equal(2, row.OpenProjects);
            Assert.Equal(1, row.QuietProjects);
            Assert.Equal(50, row.QuietPercent);
            Assert.True(r.Total.Users >= row.Users);
        }
        using (var s = _db.NewScope(admin: true))
        {
            var r = await People(s).UsageAsync();
            var person = r.People!.Single(p => p.Id == a);
            Assert.Equal(KoreaClock.KstDate(_db.Clock.UtcNow.AddDays(-2)), person.LastSeen);
            Assert.Equal(2, person.Updates);
            Assert.Equal(1, person.OpenProjects);
            Assert.Null(r.People!.Single(p => p.Name == "현황 일반").LastSeen);
        }
    }

    [Fact]
    public async Task IP_연결은_보존_기간이_지나면_정리하고_이력을_남긴다()
    {
        var uid = await UserAsync("보존 사용자");
        using (var s = _db.NewScope())
        {
            s.Db.UserAddresses.Add(new UserAddress { Ip = "10.22.0.1", UserId = uid, LastSeenAt = _db.Clock.UtcNow.AddDays(-400) });
            s.Db.UserAddresses.Add(new UserAddress { Ip = "10.22.0.2", UserId = uid, LastSeenAt = _db.Clock.UtcNow.AddDays(-100) });
            await s.Db.SaveChangesAsync();
        }
        using (var s = new TestDb.Scope(_db))
        {
            // 배경 작업처럼 현재 사용자 없이
            var svc = new UserAdminService(s.Db, _db.Clock, new CurrentUser());
            Assert.Equal(0, await svc.PruneAddressesAsync(0));        // 0 이하면 끈다
            Assert.Equal(1, await svc.PruneAddressesAsync(365));
        }
        using (var s = _db.NewScope())
        {
            Assert.Equal(new[] { "10.22.0.2" }, await s.Db.UserAddresses.Where(a => a.UserId == uid).Select(a => a.Ip).ToListAsync());
            var row = await s.Db.MasterChanges.SingleAsync(c => c.Kind == "ip" && c.OldValue == "10.22.0.1");
            Assert.Null(row.UserId);
            Assert.StartsWith("IP 연결 정리: 보존 사용자 — 10.22.0.1", row.Text);
        }
        var opts = AccessOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Access:AddressKeepDays"] = "90" }).Build());
        Assert.Equal(90, opts.AddressKeepDays);
        Assert.Equal(365, AccessOptions.From(new ConfigurationBuilder().Build()).AddressKeepDays);
    }

    [Fact]
    public async Task 과제_첨부_합계가_상한을_넘으면_거절한다()
    {
        var (pid, _) = await NewProjectAsync("첨부 상한 과제", "테스터");
        var (other, _) = await NewProjectAsync("첨부 상한 다른 과제", "테스터");
        var chunk = new byte[600 * 1024];
        using (var s = _db.NewScope())
        {
            var files = Files(s, projectMaxMB: "1");
            Assert.Equal(1, files.ProjectMaxMB);
            await files.SaveAsync(pid, null, "a.txt", chunk.Length, new MemoryStream(chunk));
            var ex = await Assert.ThrowsAsync<RuleException>(() => files.SaveAsync(pid, null, "b.txt", chunk.Length, new MemoryStream(chunk)));
            Assert.Equal("file", ex.Field);
            Assert.Contains("상한(1MB)", ex.Message);
            await files.SaveAsync(other, null, "c.txt", chunk.Length, new MemoryStream(chunk));      // 과제마다 따로 센다
            Assert.Equal(1, await s.Db.Attachments.CountAsync(x => x.ProjectId == pid));
        }
        using (var s = _db.NewScope())
        {
            // 기본은 상한 없음
            var files = Files(s, projectMaxMB: null);
            Assert.Null(files.ProjectMaxMB);
            await files.SaveAsync(pid, null, "d.txt", chunk.Length, new MemoryStream(chunk));
        }
    }

    private AttachmentService Files(TestDb.Scope s, string? projectMaxMB)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "gantt-useradmin-test"),
            ["Storage:ProjectMaxMB"] = projectMaxMB,
        }).Build();
        return new AttachmentService(s.Db, _db.Clock, s.Me, cfg, new Env());
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
