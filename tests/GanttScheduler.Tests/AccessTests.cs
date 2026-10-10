using System.Net;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GanttScheduler.Tests;

// 사용자 식별·권한: 관리자 이름 사칭 차단, 첫 관리자, 관리자 전용 변경, 삭제 권한, IP 관리, 담당자 이름 처리
public class AccessTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private DateOnly Today => _db.Clock.Today;

    public AccessTests(TestDb db) => _db = db;

    private async Task<int> NewProjectAsync(string title, string? owner = null)
    {
        using var s = _db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        return await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, owner, Today.AddDays(-5), null, null, null, null));
    }

    private async Task<ProjectView> ViewAsync(int id)
    {
        using var s = _db.NewScope();
        return (await s.Projects.ViewAsync(id))!;
    }

    private async Task<bool> UserExistsAsync(string name)
    {
        using var s = _db.NewScope();
        return await s.Db.Users.AnyAsync(u => u.Name == name);
    }

    // ── 관리자 이름 사칭 ───────────────────────────────────────────────

    [Fact]
    public async Task 관리자_이름에는_등록_화면에서_새_IP를_연결하지_못한다()
    {
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Users.RegisterAsync("10.66.0.1", "테스터", null));
            Assert.Equal("name", ex.Field);
            Assert.Equal(UserService.AdminLinkRefused, ex.Message);
        }
        using (var s = _db.NewScope())
            Assert.False(await s.Db.UserAddresses.AnyAsync(a => a.Ip == "10.66.0.1"));

        // 이미 그 관리자에 연결된 IP 에서는 다시 등록해도 된다 (사용자 바꾸기에서 자기 이름을 다시 고른 경우)
        using (var s = _db.NewScope())
        {
            await s.Masters.AddAddressAsync(new MasterIp(_db.Me.Id, "10.66.0.2"));
            var again = await s.Users.RegisterAsync("10.66.0.2", "테스터", null);
            Assert.Equal(_db.Me.Id, again.Id);
        }

        // 일반 사용자 이름에는 지금처럼 IP 가 붙는다(PC 교체 대응)
        using (var s = _db.NewScope())
        {
            var kim = await s.Users.RegisterAsync("10.66.0.3", "이자동", null);
            Assert.False(kim.IsAdmin);
        }
    }

    [Fact]
    public async Task 사용자_바꾸기는_IP_연결을_다른_사람으로_옮긴다()
    {
        using (var s = _db.NewScope())
            await s.Users.RegisterAsync("10.66.1.1", "바꾸기전", null);
        using (var s = _db.NewScope())
            await s.Users.RegisterAsync("10.66.1.1", "바꾼사람", null);
        using (var s = _db.NewScope())
        {
            var addr = await s.Db.UserAddresses.Include(a => a.User).SingleAsync(a => a.Ip == "10.66.1.1");
            Assert.Equal("바꾼사람", addr.User!.Name);
            // 원래 사람은 그대로 남는다 (다른 기기에서 쓰거나 담당자로 남아 있을 수 있다)
            Assert.True(await s.Db.Users.AnyAsync(u => u.Name == "바꾸기전"));
        }
    }

    [Fact]
    public async Task IP_재확인에_맞다고_하면_마지막_접속을_갱신한다()
    {
        using (var s = _db.NewScope())
            await s.Users.RegisterAsync("10.66.2.1", "재확인", null);
        var old = _db.Clock.UtcNow.AddDays(-40);
        using (var s = _db.NewScope())
        {
            (await s.Db.UserAddresses.SingleAsync(a => a.Ip == "10.66.2.1")).LastSeenAt = old;
            await s.Db.SaveChangesAsync();
        }
        using (var s = _db.NewScope())
            await s.Users.ConfirmAsync("10.66.2.1");
        using (var s = _db.NewScope())
            Assert.Equal(_db.Clock.UtcNow, (await s.Db.UserAddresses.SingleAsync(a => a.Ip == "10.66.2.1")).LastSeenAt);
    }

    // ── 첫 관리자 ──────────────────────────────────────────────────────

    // 관리자가 없는 DB (테스트 DB 의 기본 관리자를 일반 사용자로 바꾼다)
    private static TestDb NoAdminDb()
    {
        var db = new TestDb();
        using var s = db.NewScope("테스터", admin: false);
        return db;
    }

    [Fact]
    public async Task 관리자가_없으면_서버_자신에서_등록한_사람만_관리자가_된다()
    {
        using var db = NoAdminDb();
        using (var s = db.NewScope())
        {
            var remote = await s.Users.RegisterAsync("10.0.0.5", "원격사용자", null, local: false);
            Assert.False(remote.IsAdmin);
            Assert.False(await s.Users.HasAdminAsync());
        }
        using (var s = db.NewScope())
        {
            var local = await s.Users.RegisterAsync("127.0.0.1", "서버담당", null, local: true);
            Assert.True(local.IsAdmin);
        }
        // 이미 관리자가 있으면 서버에서 등록해도 일반 사용자
        using (var s = db.NewScope())
            Assert.False((await s.Users.RegisterAsync("local", "두번째", null, local: true)).IsAdmin);
    }

    [Fact]
    public async Task 첫_관리자_제한을_끄면_처음_등록한_사람이_관리자가_된다()
    {
        using var db = NoAdminDb();
        using var s = db.NewScope(access: new AccessOptions { FirstAdminLocalOnly = false });
        Assert.True((await s.Users.RegisterAsync("10.0.0.6", "먼저온사람", null, local: false)).IsAdmin);
        Assert.False((await s.Users.RegisterAsync("10.0.0.7", "나중온사람", null, local: false)).IsAdmin);
    }

    // ── 관리자 전용·삭제 권한 ─────────────────────────────────────────

    [Fact]
    public async Task 관리자가_아니면_기준_정보와_사용자를_바꿀_수_없다()
    {
        using var s = _db.NewScope("일반사용자");
        var loc = (await s.Masters.ListAsync("location")).First();
        var calls = new List<Func<Task>>
        {
            () => s.Masters.SaveAsync(new MasterSave("location", 1, null, null, "새 법인", null, null, null, null, null, null)),
            () => s.Masters.ToggleAsync(new MasterRef("location", 1, loc.Id, 0)),
            () => s.Masters.MoveAsync(new MasterRef("location", 1, loc.Id, 1)),
            () => s.Masters.SaveAsync(new MasterSave("user", 1, s.Me.Id, null, "일반사용자", null, null, null, null, true, null)),
            () => s.Masters.AddAddressAsync(new MasterIp(s.Me.Id, "10.66.3.1")),
            () => s.Masters.RemoveAddressAsync(new MasterIp(_db.Me.Id, "10.66.3.2")),
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<RuleException>(call);
            Assert.Equal("관리자만 변경할 수 있습니다.", ex.Message);
        }
        Assert.False((await s.Db.Users.SingleAsync(u => u.Name == "일반사용자")).IsAdmin);
    }

    [Fact]
    public async Task 자기_관리자_권한은_해제할_수_없고_자기_자신은_사용_중지할_수_없다()
    {
        using var s = _db.NewScope("관리자갑", admin: true);
        var ex = await Assert.ThrowsAsync<RuleException>(() =>
            s.Masters.SaveAsync(new MasterSave("user", 1, s.Me.Id, null, "관리자갑", null, null, null, null, false, null)));
        Assert.Equal("isAdmin", ex.Field);
        await Assert.ThrowsAsync<RuleException>(() => s.Masters.ToggleAsync(new MasterRef("user", 1, s.Me.Id, 0)));
        Assert.True((await s.Db.Users.SingleAsync(u => u.Id == s.Me.Id)).IsAdmin);
    }

    [Fact]
    public async Task 과제_삭제는_담당자_등록자_관리자만_할_수_있다()
    {
        var id = await NewProjectAsync("테스트 삭제 권한", owner: "삭제담당");
        using (var s = _db.NewScope("지나가는사람"))
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.DeleteAsync(id));
        Assert.NotNull(await ViewAsync(id));

        // 담당자는 지울 수 있다
        using (var s = _db.NewScope("삭제담당"))
            await s.Projects.DeleteAsync(id);
        using (var s = _db.NewScope())
            Assert.Null(await s.Projects.ViewAsync(id));
    }

    [Fact]
    public async Task 첨부_삭제는_올린_사람_과제_담당자_관리자만_할_수_있다()
    {
        var id = await NewProjectAsync("테스트 첨부 권한");
        int fileId;
        using (var s = _db.NewScope("올린사람"))
        {
            var a = new Attachment { ProjectId = id, FileName = "a.txt", StoredName = "none.txt", ContentType = "text/plain", Size = 1, UploadedById = s.Me.Id, UploadedAt = _db.Clock.UtcNow };
            s.Db.Attachments.Add(a);
            await s.Db.SaveChangesAsync();
            fileId = a.Id;
        }

        using (var s = _db.NewScope("남의사람"))
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => Files(s).DeleteAsync(fileId));
            Assert.Contains("올린 사람", ex.Message);
        }
        using (var s = _db.NewScope("올린사람"))
            await Files(s).DeleteAsync(fileId);
        using (var s = _db.NewScope())
            Assert.False(await s.Db.Attachments.AnyAsync(a => a.Id == fileId));
    }

    private AttachmentService Files(TestDb.Scope s)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(Path.GetTempPath(), "gantt-perm-" + Guid.NewGuid().ToString("N")),
        }).Build();
        return new AttachmentService(s.Db, _db.Clock, s.Me, cfg, new Env());
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GanttScheduler";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ── 관리 > 사용자: IP 연결·해제 ────────────────────────────────────

    [Fact]
    public async Task 관리자가_사용자에_IP를_연결하고_해제한다()
    {
        int userId;
        using (var s = _db.NewScope())
        {
            userId = (await s.Users.RegisterAsync("10.66.4.1", "새PC관리자", null)).Id;
            // 다른 관리자의 새 PC 를 연결하는 복구 경로: IPv4 매핑 주소도 접속 때와 같은 모양으로 저장한다
            await s.Masters.AddAddressAsync(new MasterIp(userId, " ::ffff:10.66.4.2 "));
            var node = (await s.Masters.ListAsync("user")).Single(n => n.Id == userId);
            Assert.Equal(new[] { "10.66.4.1", "10.66.4.2" }, node.Addresses!.Select(a => a.Ip).OrderBy(x => x).ToArray());
            Assert.Equal(Today, node.LastSeen);
            Assert.All(node.Addresses!, a => Assert.False(a.Current));
        }

        using (var s = _db.NewScope())
        {
            // 다른 사람의 IP 는 먼저 해제해야 한다
            var taken = await Assert.ThrowsAsync<RuleException>(() => s.Masters.AddAddressAsync(new MasterIp(_db.Me.Id, "10.66.4.1")));
            Assert.Equal("ip", taken.Field);
            Assert.Contains("새PC관리자", taken.Message);
            // 형식이 아닌 값 ("10" 은 0.0.0.10 으로 읽히므로 받지 않는다)
            foreach (var bad in new[] { "10", "10.1", "abc", "" })
                Assert.Equal("ip", (await Assert.ThrowsAsync<RuleException>(() => s.Masters.AddAddressAsync(new MasterIp(userId, bad)))).Field);

            await s.Masters.RemoveAddressAsync(new MasterIp(userId, "10.66.4.1"));
            Assert.Equal(new[] { "10.66.4.2" }, (await s.Masters.ListAsync("user")).Single(n => n.Id == userId).Addresses!.Select(a => a.Ip));
        }

        // 지금 쓰고 있는 기기의 연결은 끊지 못한다 (스스로 잠기지 않게)
        using (var s = _db.NewScope())
        {
            await s.Masters.AddAddressAsync(new MasterIp(_db.Me.Id, "10.66.4.9"));
            s.Me.Ip = "10.66.4.9";
            Assert.True((await s.Masters.ListAsync("user")).Single(n => n.Id == _db.Me.Id).Addresses!.Single(a => a.Ip == "10.66.4.9").Current);
            await Assert.ThrowsAsync<RuleException>(() => s.Masters.RemoveAddressAsync(new MasterIp(_db.Me.Id, "10.66.4.9")));
        }
    }

    // ── 담당자 이름 처리 ───────────────────────────────────────────────

    [Fact]
    public async Task 저장에_실패하면_새_담당자_이름이_남지_않는다()
    {
        var id = await NewProjectAsync("테스트 유령 담당자");
        var view = await ViewAsync(id);
        var stage = view.Stages[0];
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "작업", null, null, Today, Today.AddDays(3), null, null, 0, null, null));
        var work = (await ViewAsync(id)).Stages[0].Works.Single();

        // 동시 수정(409)
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(work.Id, stage.Id, "작업", "유령담당", "유령부담당", work.PlanStart, work.PlanEnd, null, null, 0, null, null, work.Version - 1)));
        // 사유 없는 일정 변경(400)
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(work.Id, stage.Id, "작업", "유령담당", null, work.PlanStart.AddDays(1), work.PlanEnd.AddDays(1), null, null, 0, null, null, work.Version)));
        // 잘못된 선행 작업(400)
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "새 작업", "유령담당", null, Today, Today, null, null, 0, null, null, null, new List<int> { 999999 })));
        // 과제 수정 동시 수정(409)
        using (var s = _db.NewScope())
            await Assert.ThrowsAsync<ConflictException>(() => s.Projects.UpdateAsync(id,
                new ProjectInput(view.Title, view.CategoryId, view.LocationId, "유령과제담당", null, null, null, null, null, view.Version - 1)));
        // 허용 범위 밖 날짜(규칙 16). 작업 날짜 범위는 담당자 확인 뒤에 검사하므로, 새 사용자가 저장 전이어야 남지 않는다
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "새 작업", "유령담당", "유령부담당", Today.AddYears(20), Today.AddYears(20), null, null, 0, null, null)));
            Assert.Equal("planStart", ex.Field);
        }
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.UpdateAsync(id,
                new ProjectInput(view.Title, view.CategoryId, view.LocationId, "유령과제담당", Today.AddYears(20), null, null, null, null, view.Version)));
            Assert.Equal("startDate", ex.Field);
        }

        Assert.False(await UserExistsAsync("유령담당"));
        Assert.False(await UserExistsAsync("유령부담당"));
        Assert.False(await UserExistsAsync("유령과제담당"));
    }

    [Fact]
    public async Task 새_담당자_이름은_저장과_함께_만들어져_연결된다()
    {
        var id = await NewProjectAsync("테스트 새 담당자", owner: "새과제담당");
        var view = await ViewAsync(id);
        Assert.Equal("새과제담당", view.Owner);
        var stage = view.Stages[0];

        // 같은 새 이름을 담당·부담당에 함께 넣으면 한 명만 만들고 부담당은 비운다
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "작업1", "새작업담당", "새작업담당", Today, Today.AddDays(2), null, null, 0, null, null));
        // 담당과 부담당이 서로 다른 새 사람
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "작업2", "새담당A", "새부담당B", Today, Today.AddDays(2), null, null, 0, null, null));

        view = await ViewAsync(id);
        var w1 = view.Stages[0].Works.Single(w => w.Title == "작업1");
        Assert.Equal(("새작업담당", (string?)null), (w1.Owner, w1.CoOwner));
        var w2 = view.Stages[0].Works.Single(w => w.Title == "작업2");
        Assert.Equal(("새담당A", "새부담당B"), (w2.Owner, w2.CoOwner));

        // 기존 작업의 담당을 새 이름으로 바꾸기, 과제 담당을 새 이름으로 바꾸기
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(w2.Id, stage.Id, "작업2", "바뀐담당", "새부담당B", w2.PlanStart, w2.PlanEnd, null, null, 0, null, null, w2.Version));
        using (var s = _db.NewScope())
            await s.Projects.UpdateAsync(id, new ProjectInput(view.Title, view.CategoryId, view.LocationId, "바뀐과제담당", null, null, null, null, null, view.Version));

        view = await ViewAsync(id);
        Assert.Equal("바뀐과제담당", view.Owner);
        Assert.Equal("바뀐담당", view.Stages[0].Works.Single(w => w.Title == "작업2").Owner);
        using (var s = _db.NewScope())
        {
            Assert.Equal(1, await s.Db.Users.CountAsync(u => u.Name == "새작업담당"));
            var p = await s.Db.Projects.Include(x => x.Owner).SingleAsync(x => x.Id == id);
            Assert.Equal("바뀐과제담당", p.Owner!.Name);
            Assert.False(p.Owner.IsAdmin);
            // IP 없는 담당자로 만들어진다 (나중에 그 이름으로 등록하면 연결된다)
            Assert.False(await s.Db.UserAddresses.AnyAsync(a => a.UserId == p.OwnerId));
        }
    }

    [Fact]
    public async Task 사용_중지된_이름은_새로_담당자로_지정할_수_없지만_그대로_두는_것은_된다()
    {
        var id = await NewProjectAsync("테스트 중지 담당자", owner: "곧중지");
        var stage = (await ViewAsync(id)).Stages[0];
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(null, stage.Id, "작업", "곧중지", null, Today, Today.AddDays(2), null, null, 0, null, null));
        using (var s = _db.NewScope())
        {
            var u = await s.Db.Users.SingleAsync(x => x.Name == "곧중지");
            await s.Masters.ToggleAsync(new MasterRef("user", 1, u.Id, 0));
        }

        var view = await ViewAsync(id);
        var work = view.Stages[0].Works.Single();
        // 지금 담당이 사용 중지돼도 다른 항목(진행률)은 고칠 수 있다
        using (var s = _db.NewScope())
            await s.Projects.SaveWorkAsync(new WorkInput(work.Id, stage.Id, "작업", "곧중지", null, work.PlanStart, work.PlanEnd, Today, null, 30, null, null, work.Version));
        using (var s = _db.NewScope())
            await s.Projects.UpdateAsync(id, new ProjectInput("테스트 중지 담당자(수정)", view.CategoryId, view.LocationId, "곧중지", null, null, null, null, null, view.Version));

        // 새로 지정하는 것은 거절한다 — 부담당·다른 작업·새 과제
        work = (await ViewAsync(id)).Stages[0].Works.Single();
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(work.Id, stage.Id, "작업", "곧중지", "곧중지", work.PlanStart, work.PlanEnd, Today, null, 30, null, null, work.Version)));
            Assert.Equal("coOwner", ex.Field);
        }
        using (var s = _db.NewScope())
        {
            var ex = await Assert.ThrowsAsync<RuleException>(() => s.Projects.SaveWorkAsync(
                new WorkInput(null, stage.Id, "다른 작업", "곧중지", null, Today, Today, null, null, 0, null, null)));
            Assert.Equal("owner", ex.Field);
        }
        await Assert.ThrowsAsync<RuleException>(() => NewProjectAsync("테스트 중지 담당자 2", owner: "곧중지"));
    }

    // ── 접속 허용 대역 ────────────────────────────────────────────────

    [Fact]
    public void 허용_대역은_CIDR_과_주소를_받고_루프백은_항상_허용한다()
    {
        var access = new AccessOptions { AllowedNetworks = AccessOptions.ParseNetworks(new[] { "10.0.0.0/8, 192.168.1.10", "fd00::/8" }) };
        Assert.True(access.IsAllowed(IPAddress.Parse("10.20.30.40")));
        Assert.True(access.IsAllowed(IPAddress.Parse("::ffff:10.20.30.40")));     // 이중 스택 소켓의 IPv4
        Assert.True(access.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.False(access.IsAllowed(IPAddress.Parse("192.168.1.11")));
        Assert.True(access.IsAllowed(IPAddress.Parse("fd12::1")));
        Assert.False(access.IsAllowed(IPAddress.Parse("172.16.0.1")));
        Assert.True(access.IsAllowed(IPAddress.Loopback));
        Assert.True(access.IsAllowed(IPAddress.IPv6Loopback));
        Assert.True(access.IsAllowed(null));

        // 비어 있으면 모두 허용
        Assert.True(new AccessOptions().IsAllowed(IPAddress.Parse("172.16.0.1")));
    }

    [Fact]
    public void 허용_대역을_잘못_쓰면_시작할_때_알린다()
    {
        Assert.Throws<InvalidOperationException>(() => AccessOptions.ParseNetworks(new[] { "10.0.0.0/8", "사내망" }));
        Assert.Throws<InvalidOperationException>(() => AccessOptions.ParseNetworks(new[] { "10.0.0.0/33" }));
        // 접두 뒤 비트가 남은 대역은 접두까지만 본다
        Assert.True(new AccessOptions { AllowedNetworks = AccessOptions.ParseNetworks(new[] { "10.1.2.3/8" }) }.IsAllowed(IPAddress.Parse("10.200.0.1")));
        Assert.Throws<InvalidOperationException>(() => AccessOptions.ParseNetworks(new[] { "10" }));
    }

    [Fact]
    public void 접속_설정은_JSON_배열과_한_줄_목록을_모두_받는다()
    {
        static AccessOptions From(Dictionary<string, string?> values) =>
            AccessOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var defaults = From(new());
        Assert.True(defaults.FirstAdminLocalOnly);
        Assert.Equal(30, defaults.ReconfirmDays);
        Assert.Empty(defaults.AllowedNetworks);

        var array = From(new() { ["Access:AllowedNetworks:0"] = "10.0.0.0/8", ["Access:AllowedNetworks:1"] = "192.168.0.0/16", ["Access:ReconfirmDays"] = "0" });
        Assert.Equal(2, array.AllowedNetworks.Count);
        Assert.Equal(0, array.ReconfirmDays);

        var line = From(new() { ["Access:AllowedNetworks"] = "10.0.0.0/8,192.168.0.0/16", ["Access:FirstAdminLocalOnly"] = "false" });
        Assert.Equal(2, line.AllowedNetworks.Count);
        Assert.False(line.FirstAdminLocalOnly);

        // appsettings.json 의 빈 배열 위에 운영 설정 파일의 목록을 얹어도 목록이 살아 있어야 한다 (모두 허용으로 새지 않게)
        var json = Path.Combine(Path.GetTempPath(), "gantt-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(json);
        try
        {
            File.WriteAllText(Path.Combine(json, "a.json"), "{ \"Access\": { \"AllowedNetworks\": [] } }");
            File.WriteAllText(Path.Combine(json, "b.json"), "{ \"Access\": { \"AllowedNetworks\": [ \"10.0.0.0/8\" ] } }");
            var layered = AccessOptions.From(new ConfigurationBuilder().SetBasePath(json).AddJsonFile("a.json").AddJsonFile("b.json").Build());
            Assert.Single(layered.AllowedNetworks);
            Assert.False(layered.IsAllowed(IPAddress.Parse("172.16.0.1")));
            Assert.Empty(AccessOptions.From(new ConfigurationBuilder().SetBasePath(json).AddJsonFile("a.json").Build()).AllowedNetworks);
        }
        finally { Directory.Delete(json, true); }
    }
}
