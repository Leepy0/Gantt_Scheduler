using System.Text.Json.Serialization;
using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// 업데이트 교체 도우미로 불린 경우: 웹 서버를 띄우지 않고 파일만 바꾸고 끝낸다 (Services/UpdateApplier.cs)
if (args.Length > 0 && args[0] == UpdateApplier.Flag) return UpdateApplier.Run(args);
// Windows 서비스 등록·제거 명령: GanttScheduler.exe install / uninstall (Services/ServiceInstaller.cs)
if (ServiceInstaller.IsCommand(args)) return ServiceInstaller.Run(args);

// 서비스로 시작되면 SCM 이 "--service <이름>" 을 넘긴다
var serviceName = ServiceInstaller.ServiceNameFrom(args);
if (serviceName != null)
{
    // 서비스는 화면(콘솔)이 없다. 시작하다 죽으면(대개 DB 연결) 원인을 파일에 남겨 설치 명령이 보여 줄 수 있게 한다
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "App_Data");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ServiceInstaller.StartupErrorFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{e.ExceptionObject}");
        }
        catch (Exception) { /* 기록 실패는 무시 */ }
    };
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // 서비스는 System32 폴더에서 시작되므로 프로그램 폴더를 기준으로 삼는다 (설정 파일·wwwroot·App_Data)
    ContentRootPath = serviceName != null ? AppContext.BaseDirectory : null,
});
if (serviceName != null) WindowsServiceSupport.Use(builder, serviceName);
var config = builder.Configuration;

// DB: 운영은 SQL Server, 미리보기·개발·테스트는 SQLite (Database:Provider 로 선택)
var useSqlServer = string.Equals(config["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);
builder.Services.AddDbContext<AppDb>(options =>
{
    if (useSqlServer)
    {
        options.UseSqlServer(config.GetConnectionString("SqlServer"),
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDb.Schema));
    }
    else
    {
        // 상대 경로는 앱 폴더 기준으로 풀고, 폴더가 없으면 만든다
        var path = AppPaths.Sqlite(config, builder.Environment.ContentRootPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        options.UseSqlite($"Data Source={path}");
    }
});

builder.Services.AddSingleton<IClock, KoreaClock>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<MasterService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<InsightService>();
builder.Services.AddSingleton<ExcelExporter>();
builder.Services.AddSingleton<PdfExporter>();

// 자동 업데이트
var build = AppBuild.FromAssembly();
builder.Services.AddSingleton(build);
builder.Services.AddSingleton(new UpdatePaths
{
    AppDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
    DataDir = AppPaths.Updates(config, builder.Environment.ContentRootPath),
    ServiceName = serviceName,
});
builder.Services.AddSingleton(new UpdateSource(config["Update:Source"], build));
builder.Services.AddSingleton<ActivityTracker>();
builder.Services.AddSingleton<UpdateService>();
// 시작 작업(DB 준비 → 직전 교체 결과 정리)을 먼저 등록한다. 등록 순서대로 실행되고, 끝나야 요청을 받기 시작한다
builder.Services.AddHostedService<StartupWork>();
builder.Services.AddHostedService<UpdateWorker>();

// 위조 방지 토큰의 암호화 키를 데이터 폴더에 둔다. 서버가 다시 시작돼도(업데이트·재활용) 열려 있던 화면의 저장이 그대로 통한다
var keyDir = AppPaths.Keys(config, builder.Environment.ContentRootPath);
Directory.CreateDirectory(keyDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDir));

builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

var updates = app.Services.GetRequiredService<UpdateService>();

// 리버스 프록시(Codespaces, nginx 등) 뒤에서 실제 접속 IP 를 쓰려면 켠다
if (config.GetValue<bool>("TrustForwardedHeaders"))
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// 사람이 쓰고 있는지 기록한다 (브라우저가 주기적으로 부르는 /version 은 사용으로 치지 않는다)
var activity = app.Services.GetRequiredService<ActivityTracker>();
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/version") && !ctx.Request.Path.StartsWithSegments("/health")) activity.Touch();
    await next();
});

app.UseMiddleware<UserGateMiddleware>();
app.MapRazorPages();
app.MapGet("/health", () => Results.Text("ok"));
// 열려 있는 화면이 서버가 새 버전으로 바뀌었는지 확인하는 주소
app.MapGet("/version", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { ok = true, build = build.Id, version = build.VersionText, updating = updates.Applying });
});

app.Run();
return 0;

// 통합 테스트(WebApplicationFactory)에서 참조
public partial class Program { }
