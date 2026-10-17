using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// HTTP: 등록 화면의 단계·작업 CSV 붙여넣기(미리보기·샘플·등록), 관리 CSV 일괄 입력, 진행 내용의 이미지 첨부
public partial class HttpTests
{
    private static HttpRequestMessage EditJson(string token, string handler, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/Projects/Edit?handler=" + handler) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 등록_화면의_CSV_붙여넣기는_미리보기와_샘플을_주고_폼_제출로_단계_작업을_만든다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Projects/Edit");
        Assert.Contains("id=\"paste-text\"", html);
        Assert.Contains("샘플 CSV 복사", html);
        var token = TokenOf(html);
        int catId, corpId;
        using (var s = _factory.Db.NewScope())
        {
            catId = (await s.Db.Categories.FirstAsync(c => c.Name == "물류")).Id;
            corpId = (await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국")).Id;
        }

        // 샘플: JSON(복사용)과 CSV 파일(UTF-8 BOM)
        var sample = await client.GetFromJsonAsync<JsonElement>($"/Projects/Edit?handler=PasteSample&categoryId={catId}&start=2026-10-01");
        var text = sample.GetProperty("text").GetString()!;
        Assert.StartsWith("단계,작업명,담당,목표 시작,목표 종료,선행 단계,선행 작업명,동시\n검토,", text);
        var file = await client.GetAsync($"/Projects/Edit?handler=PasteSample&categoryId={catId}&start=2026-10-01&download=true");
        Assert.Equal("text/csv", file.Content.Headers.ContentType!.MediaType);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // 미리보기: 샘플은 문제가 없다. 틀린 글은 줄마다 문제
        var pv = await client.SendAsync(EditJson(token, "PastePreview", new { categoryId = catId, startDate = "2026-10-01", text }));
        var body = await pv.Content.ReadAsStringAsync();
        Assert.True(pv.StatusCode == HttpStatusCode.OK, body);
        var preview = JsonDocument.Parse(body).RootElement.GetProperty("preview");
        Assert.Equal(0, preview.GetProperty("errorCount").GetInt32());
        Assert.Equal(3, preview.GetProperty("stages").GetArrayLength());
        var bad = await client.SendAsync(EditJson(token, "PastePreview", new { categoryId = catId, startDate = "2026-10-01", text = "검토,,,,2026-13-01,,\n" }));
        Assert.Equal(1, (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preview").GetProperty("errorCount").GetInt32());
        var empty = await client.SendAsync(EditJson(token, "PastePreview", new { categoryId = catId, startDate = "2026-10-01", text = " " }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("pasteText", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        // 등록(폼): 붙여 넣은 단계로 과제가 만들어지고 상세로 간다
        var paste = "단계,작업명,담당,목표 시작,목표 종료,선행 작업명,동시\n설계,,,,2026-10-14,,\n설계,현장 실사,,2026-10-02,2026-10-06,,\n제작,,,,,,\n제작,시제품,,2026-10-15,2026-10-28,,\n교육,,,,2026-10-28,,동시\n";
        var res = await client.PostAsync("/Projects/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Title"] = "HTTP CSV 등록", ["Form.CategoryId"] = catId.ToString(), ["Form.LocationId"] = corpId.ToString(),
            ["Form.StartDate"] = "2026-10-01", ["Form.Owner"] = "통합테스터", ["Form.PasteText"] = paste, ["Form.TemplateWorks"] = "true",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var id = int.Parse(System.Text.RegularExpressions.Regex.Match(res.Headers.Location!.OriginalString, @"id=(\d+)").Groups[1].Value);
        var project = (await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={id}")).GetProperty("project");
        var stages = project.GetProperty("stages");
        Assert.Equal(new[] { "설계", "제작", "교육" }, stages.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("2026-10-28", stages[1].GetProperty("targetDate").GetString());    // 작업 기준
        Assert.True(stages[1].GetProperty("autoTarget").GetBoolean());
        Assert.True(stages[2].GetProperty("parallel").GetBoolean());
        Assert.Equal("설계 · 제작", "설계 · 제작");    // 첫 미완료 단계 '설계'만 진행 중 (제작·교육은 묶음이지만 설계 뒤)
        Assert.Equal("설계", project.GetProperty("currentStage").GetString());

        // 문제 있는 글로 제출하면 같은 화면에 오류와 글을 그대로 보인다
        var rejected = await client.PostAsync("/Projects/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Form.Title"] = "HTTP CSV 거절", ["Form.CategoryId"] = catId.ToString(), ["Form.LocationId"] = corpId.ToString(),
            ["Form.StartDate"] = "2026-10-01", ["Form.PasteText"] = "설계,,,,2026-10-40,,\n", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        // Razor 는 한글을 문자 참조(&#x…;)로 내보내므로 풀어서 본다
        var page = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
        Assert.Contains("붙여넣기 1번째 줄", page);
        Assert.Contains("2026-10-40", page);
    }

    [Fact]
    public async Task 관리_CSV_붙여넣기는_관리자만_미리보기와_적용을_할_수_있다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Admin");
        var token = TokenOf(html);
        // 관리자가 아니면 거절
        var denied = await client.SendAsync(AdminJson(token, "Paste", new { kind = "vendor", text = "HTTP 협력사", apply = false }));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        using (_factory.Db.NewScope("통합테스터", admin: true)) { }
        try
        {
            var sample = await client.GetFromJsonAsync<JsonElement>("/Admin?handler=PasteSample&kind=org");
            Assert.Equal(new[] { "그룹", "파트" }, sample.GetProperty("columns").EnumerateArray().Select(x => x.GetString()));
            Assert.StartsWith("그룹,파트\n", sample.GetProperty("text").GetString());

            var pv = await client.SendAsync(AdminJson(token, "Paste", new { kind = "org", text = "그룹,파트\nHTTP그룹,HTTP파트\nHTTP그룹,\n", apply = false }));
            var body = await pv.Content.ReadAsStringAsync();
            Assert.True(pv.StatusCode == HttpStatusCode.OK, body);
            var result = JsonDocument.Parse(body).RootElement.GetProperty("result");
            Assert.False(result.GetProperty("applied").GetBoolean());
            Assert.Equal(new[] { "add", "exists" }, result.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("action").GetString()));

            var applied = await client.SendAsync(AdminJson(token, "Paste", new { kind = "org", text = "HTTP그룹,HTTP파트", apply = true }));
            var ab = await applied.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(ab.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Contains(ab.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("name").GetString() == "HTTP파트" && n.GetProperty("level").GetInt32() == 2);
            Assert.StartsWith("1건을 추가했습니다", ab.GetProperty("notice").GetString());

            var unknown = await client.GetAsync("/Admin?handler=PasteSample&kind=usage");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        finally
        {
            using (_factory.Db.NewScope("통합테스터", admin: false)) { }
        }
    }

    [Fact]
    public async Task 진행_내용에_이미지_첨부를_넣으면_참조가_본문에_남고_다른_과제의_첨부는_거절한다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 2);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9 };
        var up = await client.SendAsync(Upload(token, 2, "현장.png", png));
        var upBody = await up.Content.ReadAsStringAsync();
        Assert.True(up.StatusCode == HttpStatusCode.OK, upBody);
        var attachmentId = JsonDocument.Parse(upBody).RootElement.GetProperty("attachmentId").GetInt32();
        Assert.True(attachmentId > 0);

        var ok = await client.SendAsync(Json(token, "Comment", new { projectId = 2, text = $"현장 확인 [이미지:{attachmentId}]" }));
        var okBody = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, okBody);
        var latest = JsonDocument.Parse(okBody).RootElement.GetProperty("project").GetProperty("activities")[0];
        Assert.Equal($"현장 확인 [이미지:{attachmentId}]", latest.GetProperty("text").GetString());

        // 이미지는 inline 으로 바로 연다 (화면이 <img> 로 그린다)
        var img = await client.GetAsync($"/Projects/Files?handler=Download&id={attachmentId}&inline=true");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);
        Assert.Null(img.Content.Headers.ContentDisposition?.FileName);

        var other = await client.SendAsync(Json(token, "Comment", new { projectId = 3, text = $"남의 사진 [이미지:{attachmentId}]" }));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal("text", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }
}
