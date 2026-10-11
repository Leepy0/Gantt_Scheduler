using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 관리자 복구 명령(GanttScheduler.exe admin <이름> [--ip <주소>]).
// 끝까지 실행하는 시험은 환경 변수로 DB 를 가리키므로, 환경 변수를 쓰는 HTTP 시험과 같은 묶음에서 차례로 돈다
[Collection(AppCollection.Name)]
public class AdminCommandTests
{
    [Theory]
    [InlineData(new[] { "admin", "홍길동" }, true)]
    [InlineData(new[] { "install" }, false)]
    [InlineData(new[] { "--urls", "http://*:80" }, false)]
    [InlineData(new string[0], false)]
    public void 관리자_명령인지_가린다(string[] args, bool expected) => Assert.Equal(expected, AdminCommand.IsCommand(args));

    [Theory]
    [InlineData(new[] { "admin", "홍길동" }, "홍길동", null)]
    [InlineData(new[] { "admin", " 홍길동 ", "--ip", "10.1.2.3" }, "홍길동", "10.1.2.3")]
    [InlineData(new[] { "admin", "--ip", "::ffff:10.1.2.3", "홍길동" }, "홍길동", "10.1.2.3")]   // 접속할 때와 같은 모양으로
    [InlineData(new[] { "admin", "홍길동", "--IP", "::1" }, "홍길동", "127.0.0.1")]
    [InlineData(new[] { "admin", "홍 길동", "--ip", "fd00::12" }, "홍 길동", "fd00::12")]
    public void 이름과_IP를_읽는다(string[] args, string name, string? ip)
    {
        Assert.True(AdminCommand.TryParse(args, out var opt, out var error), error);
        Assert.Equal(name, opt!.UserName);
        Assert.Equal(ip, opt.Ip);
    }

    // 인자는 | 로 나눠 적는다
    [Theory]
    [InlineData("admin")]
    [InlineData("admin|  ")]
    [InlineData("admin|홍길동|--ip")]
    [InlineData("admin|홍길동|--ip|10")]
    [InlineData("admin|홍길동|--ip|서버")]
    [InlineData("admin|홍길동|--ip|192.168.001.010")]     // 8진수로 읽혀 192.168.1.8 이 된다
    [InlineData("admin|홍길동|--ip|0x0A.1.2.3")]
    [InlineData("admin|홍|길동")]
    [InlineData("admin|홍길동|--port|80")]
    public void 잘못된_인자는_이유를_알린다(string joined)
    {
        Assert.False(AdminCommand.TryParse(joined.Split('|'), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task 사용자를_관리자로_만들고_IP를_연결한다()
    {
        using var db = new TestDb();
        int kimId;
        using (var s = db.NewScope())
        {
            kimId = (await s.Users.RegisterAsync("10.77.0.1", "김물류", null)).Id;
            await s.Masters.ToggleAsync(new MasterRef("user", 1, kimId, 0, Active: false));    // 사용 중지
        }

        // 없는 이름: 새로 만든다
        using (var ctx = db.Open())
        {
            var r = await AdminCommand.ApplyAsync(ctx, db.Clock, "새관리자", "10.77.0.2");
            Assert.Equal((true, false, false, null, true), (r.Created, r.Reactivated, r.WasAdmin, r.MovedFrom, r.IpAdded));
        }
        // 사용 중지된 사람: 다시 사용 + 관리자, 다른 사람의 IP 는 옮겨 온다
        using (var ctx = db.Open())
        {
            var r = await AdminCommand.ApplyAsync(ctx, db.Clock, "김물류", "10.77.0.2");
            Assert.Equal((false, true, false, "새관리자", true), (r.Created, r.Reactivated, r.WasAdmin, r.MovedFrom, r.IpAdded));
        }
        // 이미 관리자이고 이미 연결된 IP: 바뀌는 것이 없다
        using (var ctx = db.Open())
        {
            var r = await AdminCommand.ApplyAsync(ctx, db.Clock, "김물류", "10.77.0.2");
            Assert.Equal((false, false, true, null, false), (r.Created, r.Reactivated, r.WasAdmin, r.MovedFrom, r.IpAdded));
        }

        using (var ctx = db.Open())
        {
            var kim = await ctx.Users.Include(u => u.Addresses).SingleAsync(u => u.Id == kimId);
            Assert.True(kim.IsAdmin);
            Assert.True(kim.IsActive);
            Assert.Equal(new[] { "10.77.0.1", "10.77.0.2" }, kim.Addresses.Select(a => a.Ip).OrderBy(x => x).ToArray());
            Assert.True((await ctx.Users.SingleAsync(u => u.Name == "새관리자")).IsAdmin);
        }
    }

    [Fact]
    public async Task 명령을_끝까지_실행하면_서버와_같은_설정으로_DB를_연다()
    {
        using var db = new TestDb();
        var root = Path.Combine(Path.GetTempPath(), "gantt-admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var saved = Console.Out;
        var output = new StringWriter();
        try
        {
            // 웹 서버와 같은 방식(appsettings + 환경 변수)으로 DB 를 찾는다
            if (TestDb.UseSqlServer)
            {
                Environment.SetEnvironmentVariable("Database__Provider", "SqlServer");
                Environment.SetEnvironmentVariable("ConnectionStrings__SqlServer", db.SqlServerConnection);
            }
            else
            {
                Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
                Environment.SetEnvironmentVariable("Database__SqlitePath", db.SqlitePath);
            }
            Console.SetOut(output);
            var args = new[] { "admin", "복구관리자", "--ip", "10.78.0.1", "--contentRoot", root, "--environment", "Test" };

            if (!TestDb.UseSqlServer)
            {
                // SQLite 는 서버가 시작할 때 남기는 구조 표식이 맞아야 한다 — 없으면 아무것도 바꾸지 않고 끝낸다
                Assert.Equal(1, await AdminCommand.RunAsync(args));
                Assert.Contains("DB 구조", output.ToString());
                using var ctx = db.Open();
                await File.WriteAllTextAsync(db.SqlitePath + ".schema", DbInitializer.SchemaHash(ctx.Database.GenerateCreateScript()));
            }

            Assert.Equal(0, await AdminCommand.RunAsync(args));
            Assert.Contains("관리자로 지정했습니다", output.ToString());
            using (var ctx = db.Open())
            {
                var user = await ctx.Users.Include(u => u.Addresses).SingleAsync(u => u.Name == "복구관리자");
                Assert.True(user.IsAdmin);
                Assert.Equal("10.78.0.1", user.Addresses.Single().Ip);
            }

            // 인자가 잘못되면 사용법을 보여 주고 2
            Assert.Equal(2, await AdminCommand.RunAsync(new[] { "admin" }));
            Assert.Contains("사용법", output.ToString());
        }
        finally
        {
            Console.SetOut(saved);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DB_파일이_없으면_새로_만들지_않고_끝낸다()
    {
        if (TestDb.UseSqlServer) return;
        var root = Path.Combine(Path.GetTempPath(), "gantt-admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "App_Data", "gantt.db");
        var saved = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
            Environment.SetEnvironmentVariable("Database__SqlitePath", missing);
            Console.SetOut(new StringWriter());
            Assert.Equal(1, await AdminCommand.RunAsync(new[] { "admin", "누구", "--contentRoot", root }));
            Assert.False(File.Exists(missing));
        }
        finally
        {
            Console.SetOut(saved);
            Directory.Delete(root, true);
        }
    }
}
