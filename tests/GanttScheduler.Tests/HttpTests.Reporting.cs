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
}
