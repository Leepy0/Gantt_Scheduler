using System.IO.Compression;
using System.Text.Json;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GanttScheduler.Tests;

public sealed class JsonDatabaseTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private JsonDatabaseService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Open());
        var provider = services.BuildServiceProvider();
        return new JsonDatabaseService(provider.GetRequiredService<IServiceScopeFactory>(),
            BackupHelper.Config(_db, Path.GetTempPath()));
    }

    [Fact]
    public async Task SQLite_JSON_백업은_테이블별_JSON과_manifest를_포함한다()
    {
        if (TestDb.UseSqlServer) return;
        var bytes = await CreateService().ExportAsync(CancellationToken.None);
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        var tables = zip.Entries.Where(e => e.FullName.StartsWith("tables/", StringComparison.Ordinal)
            && e.FullName.EndsWith(".json", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(tables);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.Equal("SQLite", manifest.RootElement.GetProperty("Provider").GetString());
        Assert.Equal(tables.Count, manifest.RootElement.GetProperty("Tables").GetArrayLength());
    }

    [Fact]
    public async Task SQLite_JSON_복원은_전체_테이블_데이터를_트랜잭션으로_되돌린다()
    {
        if (TestDb.UseSqlServer) return;
        var service = CreateService();
        var archive = await service.ExportAsync(CancellationToken.None);
        string original;
        using (var db = _db.Open())
        {
            var project = await db.Projects.OrderBy(x => x.Id).FirstAsync();
            original = project.Title;
            project.Title = "JSON 복원 시험용 변경";
            await db.SaveChangesAsync();
        }

        await using (var input = new MemoryStream(archive))
            await service.RestoreAsync(input, CancellationToken.None);

        using (var db = _db.Open())
        {
            var project = await db.Projects.OrderBy(x => x.Id).FirstAsync();
            Assert.Equal(original, project.Title);
        }
    }

    [Fact]
    public async Task SQL_Server_설정에서는_JSON_내보내기와_복원을_거절한다()
    {
        if (!TestDb.UseSqlServer) return;
        var service = CreateService();
        await Assert.ThrowsAsync<RuleException>(() => service.ExportAsync(CancellationToken.None));
        await Assert.ThrowsAsync<RuleException>(() => service.RestoreAsync(new MemoryStream(), CancellationToken.None));
    }
}
