using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 보고 정확성·데이터 일관성·기준 정보: 첨부 내려받기 머리글, 상한 문구, 주간 보고·리드타임 화면, 대시보드 링크, 관리 화면
public partial class HttpTests
{
    [Fact]
    public async Task 첨부_내려받기는_ETag_범위_요청_private_캐시를_쓰고_상한_문구는_설정값이다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 3);
        var content = System.Text.Encoding.UTF8.GetBytes("0123456789 범위 요청 확인");
        var up = await client.SendAsync(Upload(token, 3, "범위.txt", content));
        Assert.True(up.StatusCode == HttpStatusCode.OK, await up.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await up.Content.ReadAsStringAsync()).RootElement.GetProperty("project").GetProperty("attachments")
            .EnumerateArray().First(a => a.GetProperty("fileName").GetString() == "범위.txt").GetProperty("id").GetInt32();
        var url = "/Projects/Files?handler=Download&id=" + id;

        var down = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        var etag = down.Headers.ETag;
        Assert.NotNull(etag);
        Assert.Contains("bytes", down.Headers.AcceptRanges);
        Assert.True(down.Headers.CacheControl?.Private, down.Headers.CacheControl?.ToString());
        Assert.NotNull(down.Content.Headers.LastModified);

        // 같은 파일은 304 (저장 이름이 GUID 라 내용이 바뀌지 않는다)
        var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);

        // 범위 요청 — 큰 PDF 를 미리 볼 때 필요한 부분만 받는다
        var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(0, 3);
        var part = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(content.Take(4).ToArray(), await part.Content.ReadAsByteArrayAsync());

        // 상한 문구는 설정(Storage:MaxMB, 기본 30)을 쓴다 — 화면 안내와 페이지 데이터
        var html = await client.GetStringAsync("/Projects/Detail?id=3");
        Assert.Contains("30MB 이하", html);
        Assert.Contains("\"maxMB\":30", html);
    }

    [Fact]
    public async Task 대시보드_숫자와_막대는_같은_조건의_목록으로_잇고_목록_Excel_제목에_유형_법인을_적는다()
    {
        var client = await RegisteredClientAsync();
        var year = new GanttScheduler.Services.KoreaClock().Today.Year;
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("/Projects?state=active&due=7", html);
        Assert.Contains($"/Projects?state=done&doneYear={year}", html);
        Assert.Matches("data-bar-category=\"\\d+\" href=\"/Projects\\?state=all&category=\\d+\"", html);
        Assert.Matches("data-bar-corp=\"\\d+\" href=\"/Projects\\?state=all&corp=\\d+\"", html);

        // 목록: 대시보드 조건 안내와 해제 링크, 상태 탭을 바꾸면 풀리는 숨은 입력
        var list = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects?state=done&doneYear={year}"));
        Assert.Contains($"{year}년 완료", list);
        Assert.Contains("data-special", list);

        int category, corp;
        using (var s = _factory.Db.NewScope())
        {
            category = await s.Db.Categories.Where(c => c.Name == "물류 자동화").Select(c => c.Id).FirstAsync();
            corp = await s.Db.Locations.Where(l => l.Level == 1 && l.Name == "베트남").Select(l => l.Id).FirstAsync();
        }
        var bytes = await client.GetByteArrayAsync($"/Projects?handler=Excel&state=all&category={category}&corp={corp}");
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var sub = book.Worksheets.First().Cell(2, 1).GetString();
        Assert.Contains("유형: 물류 자동화", sub);
        Assert.Contains("법인: 베트남", sub);
    }

    private static HttpRequestMessage AdminJson(string token, string handler, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/Admin?handler=" + handler) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 관리_화면은_이름_길이_상한을_싣고_사용_중지는_원하는_상태로_공휴일_붙여넣기는_안내를_돌려준다()
    {
        var client = await RegisteredClientAsync();
        // 통합테스터는 관리자가 아니다 — 이 시험 동안만 관리자로 두고 끝나면 되돌린다(같은 묶음의 다른 시험이 기대한다)
        using (_factory.Db.NewScope("통합테스터", admin: true)) { }
        try
        {
            await AdminChecksAsync(client);
        }
        finally
        {
            using (_factory.Db.NewScope("통합테스터", admin: false)) { }
        }
    }

    private static async Task AdminChecksAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin");
        var token = TokenOf(html);
        var limits = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "data-limits=\"([^\"]+)\"").Groups[1].Value)).RootElement;
        Assert.Equal(new[] { 50, 50, 200, 200 }, limits.GetProperty("category").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(100, limits.GetProperty("vendor")[0].GetInt32());
        Assert.Equal(4, limits.GetProperty("location").GetArrayLength());

        // 길이 초과는 400 과 안내 (SQL Server 에서만 나던 500 이 아니다)
        var tooLong = await client.SendAsync(AdminJson(token, "Save", new { kind = "container", level = 1, name = new string('가', 51) }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("name", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        // 사용 중지: 원하는 상태를 실어 보내고, 같은 요청을 두 번 보내도 중지 그대로
        var add = await client.SendAsync(AdminJson(token, "Save", new { kind = "container", level = 1, name = "HTTP 상자" }));
        var box = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("name").GetString() == "HTTP 상자");
        var id = box.GetProperty("id").GetInt32();
        for (var i = 0; i < 2; i++)
        {
            var off = await client.SendAsync(AdminJson(token, "Toggle", new { kind = "container", level = 1, id, dir = 0, active = false }));
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            var node = (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetInt32() == id);
            Assert.False(node.GetProperty("active").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(AdminJson(token, "Toggle", new { kind = "container", level = 1, id, dir = 0 }))).StatusCode);

        // 공휴일 여러 줄 붙여넣기: 응답에 결과·일정 영향 안내
        var paste = await client.SendAsync(AdminJson(token, "PasteHolidays", new { text = "2029-01-01\t신정\n2029-03-01 삼일절" }));
        Assert.Equal(HttpStatusCode.OK, paste.StatusCode);
        var body = await paste.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("공휴일 2건을 추가했습니다.", body.GetProperty("notice").GetString());
        Assert.Contains(body.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("date").GetString() == "2029-03-01");
        var bad = await client.SendAsync(AdminJson(token, "PasteHolidays", new { text = "삼일절 2029-03-01" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("text", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task 주간_보고와_리드타임_화면은_새_구획과_열을_그린다()
    {
        var client = await RegisteredClientAsync();
        var weekly = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Insights/Weekly"));
        Assert.Contains("data-kpi=\"changes\"", weekly);
        Assert.Contains("상태·기준선", weekly);
        Assert.Contains("과제 완료", weekly);
        // 지난 주 보고는 기준일을 밝힌다
        var last = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Insights/Weekly?day=2026-01-05"));
        Assert.Contains("data-weekly-asof", last);
        Assert.Contains("진행률·현재 단계는 지금 기준", last);

        var lead = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Insights/LeadTime?zero=exclude"));
        Assert.Contains("평균(근무일)", lead);
        Assert.Contains("처음 목표 대비", lead);
        Assert.Contains("기준선 대비", lead);
        Assert.Contains("0일 표본", lead);
    }
}
