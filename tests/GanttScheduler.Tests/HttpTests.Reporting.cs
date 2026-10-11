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
}
