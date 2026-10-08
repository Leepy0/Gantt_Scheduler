using System.Text.Json.Serialization;
using GanttScheduler.Data;
using GanttScheduler.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitAsync(scope.ServiceProvider);
}

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
app.UseMiddleware<UserGateMiddleware>();
app.MapRazorPages();
app.MapGet("/health", () => Results.Text("ok"));

app.Run();

// 통합 테스트(WebApplicationFactory)에서 참조
public partial class Program { }
