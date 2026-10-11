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
// 관리자 복구 명령: GanttScheduler.exe admin <이름> [--ip <주소>] (Services/AdminCommand.cs)
if (AdminCommand.IsCommand(args)) return await AdminCommand.RunAsync(args);

// 서비스로 시작되면 SCM 이 "--service <이름>" 을 넘긴다
var serviceName = ServiceInstaller.ServiceNameFrom(args);
// 서비스는 화면(콘솔)이 없다. 시작하다 죽으면(대개 DB 연결·깨진 설정 파일) 원인을 파일에 남겨 설치 명령과 업데이트 도우미가
// 보여 줄 수 있게 한다. 업데이트 뒤 새 버전이 뜨지 않아 되돌릴 때 그 이유가 되므로 직접 실행한 서버도 남긴다
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

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // 서비스는 System32 폴더에서 시작되므로 프로그램 폴더를 기준으로 삼는다 (설정 파일·wwwroot·App_Data)
    ContentRootPath = serviceName != null ? AppContext.BaseDirectory : null,
});
if (serviceName != null) WindowsServiceSupport.Use(builder, serviceName);
var config = builder.Configuration;

// DB: 운영은 SQL Server, 미리보기·개발·테스트는 SQLite (Database:Provider 로 선택, Data/DbSetup.cs)
builder.Services.AddDbContext<AppDb>(options => DbSetup.Configure(options, config, builder.Environment.ContentRootPath));

builder.Services.AddSingleton<IClock, KoreaClock>();
// 접속 제어(허용 대역·첫 관리자·IP 재확인). 테스트가 바꾼 설정도 보이도록 서비스가 처음 쓰일 때 읽는다
builder.Services.AddSingleton(sp => AccessOptions.From(sp.GetRequiredService<IConfiguration>()));
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
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDir));
// Windows 서비스면 키 파일을 DPAPI(이 컴퓨터)로 암호화해 둔다. 폴더 사본만으로는 키를 쓸 수 없게 하기 위해서다.
// 이미 평문으로 저장된 키는 그대로 읽히고, 새로 만드는 키부터 암호화된다
if (serviceName != null && OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// 설정의 허용 대역을 잘못 쓰면 '모두 허용'으로 지나가지 않고 시작할 때 멈춘다
app.Services.GetRequiredService<AccessOptions>();
var updates = app.Services.GetRequiredService<UpdateService>();

// 모든 응답에 보안 헤더 (nosniff, 같은 출처에서만 틀 안에 표시, 다른 사이트로 주소를 넘기지 않음)
app.UseSecurityHeaders();

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

// 본문 없는 오류 응답(없는 과제 404 등)은 오류 화면으로 다시 그린다. 화면이 JSON 으로 묻는 요청은 그대로 둔다 —
// 시트·폴링이 상태 코드와 JSON 본문으로 판단하므로 HTML 이 섞이면 안 된다
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.Accept.ToString().Contains("application/json")
        && ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
        pages.Enabled = false;
    await next();
});

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
// today: 한국 날짜. 오래 열어 둔 화면이 날짜가 바뀐 것을 알아채고 다시 그린다(DB 를 읽지 않는다)
app.MapGet("/version", (HttpContext ctx, IClock clock) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { ok = true, build = build.Id, version = build.VersionText, updating = updates.Applying, today = clock.Today });
});

app.Run();
return 0;

// 통합 테스트(WebApplicationFactory)에서 참조
public partial class Program { }
