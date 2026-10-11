using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GanttScheduler.Tests;

// 메일 요약: 보낼 때 판정(한국 시간·근무일·하루 한 번), 사용자별 묶음, 보낸 알림은 다시 싣지 않음, 실패 기록 — 가짜 통로로 확인한다
public class NotifyTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;

    public NotifyTests(TestDb db) => _db = db;

    private static readonly NotifyOptions On = new(true, 8, "http://gantt.local/", new SmtpOptions("mail.local", 25, "gantt@example.com", false, null, null));

    // 한국 시간 → UTC
    private static DateTime Kst(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-9);

    private sealed class FakeChannel : INotifyChannel
    {
        public List<MailItem> Sent { get; } = new();
        public bool Fail { get; set; }
        public bool Ready { get; set; } = true;

        public Task SendAsync(MailItem mail, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("메일 서버가 연결을 거부했습니다");
            Sent.Add(mail);
            return Task.CompletedTask;
        }
    }

    // 운영과 같은 구성(범위마다 새 DbContext, 지금 사용자 없음)으로 워커를 만든다
    private (NotifyWorker Worker, NotifyState State, ServiceProvider Services) Worker(FakeChannel channel, NotifyOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(_db.Clock);
        services.AddScoped(_ => _db.Open());
        services.AddScoped<CurrentUser>();
        services.AddScoped<UserService>();
        services.AddScoped<ActivityLog>();
        services.AddScoped<ProjectService>();
        services.AddSingleton(options ?? On);
        services.AddSingleton<INotifyChannel>(channel);
        services.AddScoped<DigestService>();
        var sp = services.BuildServiceProvider();
        var state = new NotifyState(Path.Combine(Path.GetTempPath(), "gantt-notify-" + Guid.NewGuid().ToString("N"), "notify.json"));
        return (new NotifyWorker(sp.GetRequiredService<IServiceScopeFactory>(), options ?? On, state, _db.Clock, NullLogger<NotifyWorker>.Instance), state, sp);
    }

    [Fact]
    public void 정한_시각부터_네_시간_안_근무일에_하루_한_번만()
    {
        var cal = new WorkCalendar(new[] { new DateOnly(2026, 10, 9) });     // 한글날
        Assert.False(NotifyWorker.Due(On, Kst(10, 12, 7, 59), null, cal));
        Assert.True(NotifyWorker.Due(On, Kst(10, 12, 8, 0), null, cal));       // 월요일 08:00
        Assert.True(NotifyWorker.Due(On, Kst(10, 12, 11, 59), null, cal));
        Assert.False(NotifyWorker.Due(On, Kst(10, 12, 12, 0), null, cal));     // 늦게 다시 시작한 서버는 그날 보내지 않는다
        Assert.False(NotifyWorker.Due(On, Kst(10, 12, 9, 0), new DateOnly(2026, 10, 12), cal));   // 오늘 이미 보냄
        Assert.True(NotifyWorker.Due(On, Kst(10, 12, 9, 0), new DateOnly(2026, 10, 8), cal));
        Assert.False(NotifyWorker.Due(On, Kst(10, 10, 9, 0), null, cal));      // 토요일
        Assert.False(NotifyWorker.Due(On, Kst(10, 9, 9, 0), null, cal));       // 공휴일
        Assert.False(NotifyWorker.Due(On with { Enabled = false }, Kst(10, 12, 9, 0), null, cal));
        // 한국 시간으로 본다: UTC 23시는 한국의 다음 날 8시
        Assert.True(NotifyWorker.InWindow(On, new DateTime(2026, 10, 11, 23, 0, 0, DateTimeKind.Utc), null));
    }

    [Fact]
    public async Task 사용자별로_읽지_않은_알림과_내_작업의_지연_임박을_한_통에_묶고_다시_보내지_않는다()
    {
        var saved = (_db.Clock.UtcNow, _db.Clock.Today);
        try
        {
            // 김물류: 메일 주소 있음(샘플 과제 대부분의 담당 — 지연·임박 작업이 있다), 이자동: 주소가 잘못돼 건너뜀
            using (var s = _db.NewScope())
            {
                (await s.Db.Users.SingleAsync(u => u.Name == "김물류")).Email = "kim@example.com";
                (await s.Db.Users.SingleAsync(u => u.Name == "이자동")).Email = "not-an-email";
                await s.Db.SaveChangesAsync();
            }
            var project = 0;
            using (var s = _db.NewScope())
            {
                project = await s.Db.Projects.Where(p => p.Title == "광주 냉장고 3라인 AGV 도입").Select(p => p.Id).SingleAsync();
                await s.Projects.CommentAsync(new CommentInput(project, null, null, "검수 서류 확인 부탁드립니다"));
            }

            var channel = new FakeChannel();
            var (worker, state, sp) = Worker(channel);
            using var provider = sp;
            _db.Clock.UtcNow = Kst(10, 12, 8, 30);
            _db.Clock.Today = new DateOnly(2026, 10, 12);

            var run = await worker.TickAsync(CancellationToken.None);
            Assert.NotNull(run);
            Assert.Equal(0, run!.Failed);
            Assert.True(run.Sent >= 1);
            var mail = Assert.Single(channel.Sent, m => m.To == "kim@example.com");
            Assert.StartsWith("[과제 스케줄러] 10-12 요약 — 알림 1건", mail.Subject);
            Assert.Contains("[읽지 않은 알림 1건]", mail.Body);
            Assert.Contains("[내 과제] 광주 냉장고 3라인 AGV 도입 — 댓글: 검수 서류 확인 부탁드립니다 (테스터,", mail.Body);
            Assert.Contains("작업", mail.Body);
            Assert.Contains("http://gantt.local/My/Notifications", mail.Body);
            using (var s = _db.NewScope())
                Assert.All(await s.Db.Notifications.Where(n => n.ProjectId == project).ToListAsync(), n => Assert.NotNull(n.EmailedAt));
            Assert.Equal(new DateOnly(2026, 10, 12), state.Last!.Date);
            Assert.Equal(new DateOnly(2026, 10, 12), NotifyState.Load(state.Path)!.Date);   // 다시 시작해도 오늘은 보내지 않게 파일에 남는다

            // 같은 날 다시 확인해도 보내지 않는다
            _db.Clock.UtcNow = Kst(10, 12, 9, 30);
            var count = channel.Sent.Count;
            Assert.Null(await worker.TickAsync(CancellationToken.None));
            Assert.Equal(count, channel.Sent.Count);

            // 다음 근무일: 이미 보낸 알림은 다시 싣지 않는다(지연·임박 작업은 그날 기준으로 다시 본다)
            _db.Clock.UtcNow = Kst(10, 13, 8, 5);
            _db.Clock.Today = new DateOnly(2026, 10, 13);
            run = await worker.TickAsync(CancellationToken.None);
            Assert.NotNull(run);
            Assert.DoesNotContain("[읽지 않은 알림", channel.Sent.Last(m => m.To == "kim@example.com").Body);
        }
        finally
        {
            (_db.Clock.UtcNow, _db.Clock.Today) = saved;
        }
    }

    [Fact]
    public async Task 보내지_못하면_기록을_남기고_알림은_다음_요약에_다시_싣는다()
    {
        var saved = (_db.Clock.UtcNow, _db.Clock.Today);
        try
        {
            using (var s = _db.NewScope("메일실패"))
            {
                s.Me.User!.Email = "fail@example.com";
                await s.Db.SaveChangesAsync();
            }
            // 테스터가 담당인 새 과제(테스터는 메일 주소 없음)를 '메일실패' 가 관심 과제로 두고, 이자동이 댓글을 남긴다
            int project;
            using (var s = _db.NewScope())
            {
                var cat = await s.Db.Categories.FirstAsync(c => c.Name == "용기");
                var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "멕시코");
                project = await s.Projects.CreateAsync(new ProjectInput("메일 실패 과제", cat.Id, corp.Id, null, _db.Clock.Today, null, null, null, null));
            }
            using (var s = _db.NewScope("메일실패"))
                await s.Projects.WatchAsync(project, true);
            using (var s = _db.NewScope("이자동"))
                await s.Projects.CommentAsync(new CommentInput(project, null, null, "용기 시안 공유"));

            var channel = new FakeChannel { Fail = true };
            var (worker, state, sp) = Worker(channel);
            using var provider = sp;
            _db.Clock.UtcNow = Kst(10, 14, 8, 0);
            _db.Clock.Today = new DateOnly(2026, 10, 14);
            var run = await worker.TickAsync(CancellationToken.None);
            Assert.True(run!.Failed >= 1);
            Assert.Contains("연결을 거부", run.Error);
            Assert.Contains("연결을 거부", state.Last!.Error);
            int userId;
            using (var s = _db.NewScope())
            {
                userId = await s.Db.Users.Where(u => u.Name == "메일실패").Select(u => u.Id).SingleAsync();
                Assert.All(await s.Db.Notifications.Where(n => n.UserId == userId).ToListAsync(), n => Assert.Null(n.EmailedAt));
            }

            // 다음 날 통로가 살아나면 그 알림을 싣는다
            channel.Fail = false;
            _db.Clock.UtcNow = Kst(10, 15, 8, 0);
            _db.Clock.Today = new DateOnly(2026, 10, 15);
            await worker.TickAsync(CancellationToken.None);
            Assert.Contains(channel.Sent, m => m.To == "fail@example.com" && m.Body.Contains("[관심 과제] 메일 실패 과제 — 댓글: 용기 시안 공유 (이자동,"));
        }
        finally
        {
            (_db.Clock.UtcNow, _db.Clock.Today) = saved;
        }
    }

    [Fact]
    public async Task 꺼져_있으면_보내지_않고_시험_메일은_설정만_있으면_보낸다()
    {
        var channel = new FakeChannel();
        var (worker, _, sp) = Worker(channel, On with { Enabled = false });
        using var provider = sp;
        Assert.Null(await worker.TickAsync(CancellationToken.None));

        using var scope = sp.CreateScope();
        var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
        await digest.SendTestAsync("admin@example.com", "평영", CancellationToken.None);
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("[과제 스케줄러] 시험 메일", mail.Subject);
        Assert.Contains("평영 님이", mail.Body);

        var ex = await Assert.ThrowsAsync<RuleException>(() => digest.SendTestAsync("잘못된 주소", "평영", CancellationToken.None));
        Assert.Equal("email", ex.Field);
        channel.Fail = true;
        ex = await Assert.ThrowsAsync<RuleException>(() => digest.SendTestAsync("admin@example.com", "평영", CancellationToken.None));
        Assert.StartsWith("보내지 못했습니다", ex.Message);
        channel.Fail = false;
        channel.Ready = false;
        ex = await Assert.ThrowsAsync<RuleException>(() => digest.SendTestAsync("admin@example.com", "평영", CancellationToken.None));
        Assert.Contains("Notify:Smtp", ex.Message);
    }

    [Fact]
    public void 설정은_기본이_꺼짐이고_메일_서버가_없으면_보낼_수_없다()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notify:DailyHour"] = "30", ["Notify:BaseUrl"] = "http://gantt:5080", ["Notify:Smtp:Host"] = " relay.local ",
        }).Build();
        var o = NotifyOptions.From(cfg);
        Assert.False(o.Enabled);
        Assert.Equal(23, o.DailyHour);
        Assert.Equal("http://gantt:5080/", o.BaseUrl);
        Assert.Equal(25, o.Smtp.Port);
        Assert.False(o.Smtp.Configured);              // 보내는 주소(From)가 없다
        Assert.False(new SmtpChannel(o).Ready);
        Assert.Equal(24, CommentOptions.From(cfg).EditHours);
    }
}
