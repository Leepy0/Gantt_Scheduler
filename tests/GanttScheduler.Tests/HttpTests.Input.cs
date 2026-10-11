using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 입력 부담 줄이기의 HTTP 경로: 작업 여러 개 추가(미리보기·저장), 복제 위치 목록·위치 지정, Excel 가져오기(양식·미리보기·가져오기)
public partial class HttpTests
{
    private async Task<(int Id, int StageId, int StageVersion)> InputProjectAsync(string title)
    {
        using var s = _factory.Db.NewScope();
        var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
        var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
        var id = await s.Projects.CreateAsync(new ProjectInput(title, cat.Id, corp.Id, null, EDay(10, 12), null, null, null, null));
        var stage = (await s.Projects.ViewAsync(id))!.Stages[0];
        return (id, stage.Id, stage.Version);
    }

    [Fact]
    public async Task 작업_여러_개_추가는_미리보기로_해석하고_저장은_한_번에_한다()
    {
        var x = await InputProjectAsync("HTTP 붙여넣기");
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), x.Id);

        var pv = await OkJsonAsync(await client.SendAsync(Json(token, "PreviewWorks", new { stageId = x.StageId, text = "준비\t김물류\t10/12\t10/13\n실행\t\t2026.10.14\t2026.10.15\t준비\n틀림\t\t모름\t" })));
        var rows = pv.GetProperty("preview").GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("2026-10-12", rows[0].GetProperty("planStart").GetString());
        Assert.True(rows[1].GetProperty("ok").GetBoolean());
        Assert.False(rows[2].GetProperty("ok").GetBoolean());
        Assert.Equal("목표 시작", rows[2].GetProperty("issues")[0].GetProperty("column").GetString());
        Assert.Equal(1, pv.GetProperty("preview").GetProperty("errorCount").GetInt32());

        // CSRF 없이 보내면 거절
        var noToken = new HttpRequestMessage(HttpMethod.Post, "/Projects/Detail?handler=PreviewWorks") { Content = JsonContent.Create(new { stageId = x.StageId, text = "a\t\t10/12" }) };
        noToken.Headers.Add("Accept", "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noToken)).StatusCode);

        // 문제 있는 줄이 섞이면 400 이고 아무것도 저장하지 않는다
        var bad = await client.SendAsync(Json(token, "AddWorks", new
        {
            stageId = x.StageId, stageVersion = x.StageVersion,
            rows = new object[] { new { title = "준비", planStart = "2026-10-12", planEnd = "2026-10-13", line = 1 }, new { title = "틀림", planStart = "모름", line = 2 } },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("2번째 줄", await bad.Content.ReadAsStringAsync());

        var saved = await OkJsonAsync(await client.SendAsync(Json(token, "AddWorks", new
        {
            stageId = x.StageId, stageVersion = x.StageVersion,
            rows = new object[]
            {
                new { title = "준비", owner = "김물류", planStart = "2026-10-12", planEnd = "2026-10-13", line = 1 },
                new { title = "실행", planStart = "2026-10-14", planEnd = "2026-10-15", predecessor = "준비", line = 2 },
            },
        })));
        var works = saved.GetProperty("project").GetProperty("stages")[0].GetProperty("works");
        Assert.Equal(2, works.GetArrayLength());
        Assert.Equal(1, saved.GetProperty("project").GetProperty("links").GetArrayLength());

        // 단계 버전이 다르면 409
        var stale = await client.SendAsync(Json(token, "AddWorks", new
        {
            stageId = x.StageId, stageVersion = x.StageVersion + 5,
            rows = new object[] { new { title = "늦은 추가", planStart = "2026-10-20", line = 1 } },
        }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task 복제_시트는_위치_목록을_받고_위치와_담당을_정해_복제한다()
    {
        var x = await InputProjectAsync("HTTP 복제 위치");
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), x.Id);
        var locs = await OkJsonAsync(await client.GetAsync("/Projects/Detail?handler=Locations"));
        var line = locs.GetProperty("locations").EnumerateArray().First(l => l.GetProperty("name").GetString() == "5라인");
        var cloned = await OkJsonAsync(await client.SendAsync(Json(token, "Clone", new
        {
            projectId = x.Id, title = "HTTP 복제 위치 사본", startDate = "2026-10-12", includeWorks = true, locationId = line.GetProperty("id").GetInt32(), owner = "김물류",
        })));
        var id = int.Parse(cloned.GetProperty("redirect").GetString()!.Split("id=")[1]);
        using var s = _factory.Db.NewScope();
        var p = await s.Db.Projects.Include(q => q.Owner).FirstAsync(q => q.Id == id);
        Assert.Equal(line.GetProperty("id").GetInt32(), p.LocationId);
        Assert.Equal("김물류", p.Owner!.Name);
    }

    private static HttpRequestMessage ImportUpload(string token, string handler, byte[] bytes, string fileName = "가져오기.xlsx", bool? confirm = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        if (confirm != null) form.Add(new StringContent(confirm.Value ? "true" : "false"), "confirmNewUsers");
        var req = new HttpRequestMessage(HttpMethod.Post, "/Projects/Import?handler=" + handler) { Content = form };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 가져오기_화면은_양식을_주고_올린_파일을_미리본_뒤_가져온다()
    {
        var client = await RegisteredClientAsync();
        var page = await client.GetAsync("/Projects/Import");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var token = TokenOf(await page.Content.ReadAsStringAsync());
        Assert.Contains("href=\"/Projects/Import\"", await client.GetStringAsync("/Projects"));

        var template = await client.GetAsync("/Projects/Import?handler=Template");
        Assert.Equal(ExcelExporter.ContentType, template.Content.Headers.ContentType!.MediaType);
        var bytes = await template.Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            wb.Worksheet("과제").Cell(2, 1).Value = "HTTP 가져오기 과제";
            wb.Worksheet("과제").Cell(2, 2).Value = "물류";
            wb.Worksheet("과제").Cell(2, 3).Value = "한국";
            wb.Worksheet("과제").Cell(2, 5).Value = "HTTP가져오기담당";
            wb.Worksheet("과제").Cell(2, 6).Value = new DateTime(2026, 10, 12);
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }

        // xlsx 가 아니면 거절
        var wrong = await client.SendAsync(ImportUpload(token, "Preview", new byte[] { 1, 2 }, "가져오기.csv"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var pv = await OkJsonAsync(await client.SendAsync(ImportUpload(token, "Preview", bytes)));
        Assert.True(pv.GetProperty("canImport").GetBoolean());
        Assert.Equal("HTTP가져오기담당", pv.GetProperty("preview").GetProperty("newUsers")[0].GetString());
        Assert.Equal("2026-10-12", pv.GetProperty("preview").GetProperty("projects")[0].GetProperty("startDate").GetString());

        // 새 담당자를 확인하지 않으면 400 (아무것도 저장하지 않는다)
        var unconfirmed = await client.SendAsync(ImportUpload(token, "Import", bytes, confirm: false));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("confirmNewUsers", await unconfirmed.Content.ReadAsStringAsync());

        var done = await OkJsonAsync(await client.SendAsync(ImportUpload(token, "Import", bytes, confirm: true)));
        Assert.Equal(1, done.GetProperty("result").GetProperty("projects").GetInt32());
        Assert.StartsWith("/Projects/Detail?id=", done.GetProperty("redirect").GetString());
        using var s = _factory.Db.NewScope();
        Assert.True(await s.Db.Projects.AnyAsync(p => p.Title == "HTTP 가져오기 과제" && p.Owner!.Name == "HTTP가져오기담당"));
    }
}
