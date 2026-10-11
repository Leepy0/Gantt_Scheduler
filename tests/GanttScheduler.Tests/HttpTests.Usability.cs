using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GanttScheduler.Services;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 화면 사용성: 동시 수정 뒤 다시 저장, 없는 과제 안내, 변경 시각·오늘 날짜, 삭제 권한 표시, 시각 표시
public partial class HttpTests
{
    private static readonly string KstToday = new KoreaClock().Today.ToString("yyyy-MM-dd");

    // 화면의 숨은 입력 값 (속성 순서에 기대지 않게 그 입력 태그 안에서 value 를 찾는다)
    private static string Hidden(string html, string name)
    {
        var tag = Regex.Match(html, "<input[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*>").Value;
        Assert.False(string.IsNullOrEmpty(tag), name + " 입력이 없음");
        return WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
    }

    private static HttpRequestMessage JsonGet(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    [Fact]
    public async Task 과제_수정_화면은_동시_수정_뒤_바뀐_값을_보여_주고_같은_화면에서_다시_저장한다()
    {
        int id;
        using (var s = _factory.Db.NewScope())
        {
            var cat = await s.Db.Categories.FirstAsync(c => c.Name == "물류");
            var corp = await s.Db.Locations.FirstAsync(l => l.Level == 1 && l.Name == "한국");
            id = await s.Projects.CreateAsync(new ProjectInput("HTTP 충돌 과제", cat.Id, corp.Id, "테스터", new DateOnly(2026, 10, 1), null, null, null, null));
        }
        var client = await RegisteredClientAsync();
        var url = "/Projects/Edit?id=" + id;
        var html = await client.GetStringAsync(url);
        var token = TokenOf(html);
        var opened = Hidden(html, "Form.Version");
        var openedBase = Hidden(html, "Form.Base");
        Assert.Contains("\"title\"", openedBase);

        FormUrlEncodedContent Form(string title, string version, string bas) => new(new Dictionary<string, string>
        {
            ["Form.Title"] = title, ["Form.CategoryId"] = Hidden(html, "Form.CategoryId"), ["Form.LocationId"] = Hidden(html, "Form.LocationId"),
            ["Form.Owner"] = "테스터", ["Form.StartDate"] = "2026-10-01", ["Form.Vendor"] = "", ["Form.Description"] = "",
            ["Form.Version"] = version, ["Form.Base"] = bas, ["__RequestVerificationToken"] = token,
        });

        // 다른 사용자가 같은 화면에서 먼저 저장했다
        var first = await client.PostAsync(url, Form("다른 사람이 바꾼 이름", opened, openedBase));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        // 옛 버전으로 저장하면 같은 화면을 다시 그린다: 사실대로 알리고, 그 사람이 바꾼 값을 보이고, 내 입력은 그대로, 버전은 최신
        var conflict = await client.PostAsync(url, Form("내가 바꾼 이름", opened, openedBase));
        var body = await conflict.Content.ReadAsStringAsync();
        var text = WebUtility.HtmlDecode(body);      // 화면 글자는 HTML 인코딩되어 나온다
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        Assert.Contains("다른 사용자가 이 과제를 먼저 수정했습니다", text);
        Assert.Contains("다른 사용자가 바꾼 값: 다른 사람이 바꾼 이름", text);
        Assert.Equal("내가 바꾼 이름", Hidden(body, "Form.Title"));
        var latest = Hidden(body, "Form.Version");
        Assert.NotEqual(opened, latest);
        // 바꾸지 않은 항목(착수일 등)은 바뀐 값으로 표시하지 않는다
        Assert.Single(Regex.Matches(body, "data-conflict-note"));

        // 같은 화면에서 다시 저장하면 통한다 (예전에는 같은 옛 버전으로 다시 그려 계속 거절됐다)
        var again = await client.PostAsync(url, Form("내가 바꾼 이름", latest, Hidden(body, "Form.Base")));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        var data = await client.GetFromJsonAsync<JsonElement>($"/Projects/Detail?handler=Data&id={id}");
        Assert.Equal("내가 바꾼 이름", data.GetProperty("project").GetProperty("title").GetString());
    }

    [Fact]
    public async Task 없는_과제는_안내_화면을_보이고_화면의_JSON_요청에는_JSON_404()
    {
        var client = await RegisteredClientAsync();
        var page = await client.GetAsync("/Projects/Detail?id=999999");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("찾는 화면이 없습니다", html);
        Assert.Contains("과제 목록으로 이동", html);

        foreach (var handler in new[] { "Data", "Stamp" })
        {
            var res = await client.SendAsync(JsonGet($"/Projects/Detail?handler={handler}&id=999999"));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("missing").GetBoolean());
            Assert.Contains("삭제", json.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task 변경_시각은_변경마다_바뀌고_응답과_서버_버전에_한국_날짜를_싣는다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 2);
        var before = (await client.SendAsync(JsonGet("/Projects/Detail?handler=Stamp&id=2")));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var stamp1 = (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAt").GetString()!;
        Assert.EndsWith("Z", stamp1);

        var res = await client.SendAsync(Json(token, "Comment", new { projectId = 2, text = "변경 시각 확인" }));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(KstToday, json.GetProperty("today").GetString());
        var updated = json.GetProperty("project").GetProperty("updatedAt").GetString()!;

        var stamp2 = (await (await client.SendAsync(JsonGet("/Projects/Detail?handler=Stamp&id=2"))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("updatedAt").GetString()!;
        // 내 저장 응답의 시각과 같아야 화면이 '다른 사용자가 바꿨습니다'로 착각하지 않는다
        Assert.Equal(updated, stamp2);
        Assert.True(DateTime.Parse(stamp2).ToUniversalTime() > DateTime.Parse(stamp1).ToUniversalTime());

        var version = await client.GetFromJsonAsync<JsonElement>("/version");
        Assert.Equal(KstToday, version.GetProperty("today").GetString());
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=2");
        Assert.Equal(KstToday, data.GetProperty("today").GetString());
    }

    [Fact]
    public async Task 화면이_보낸_오늘로_진행률만_올린_작업의_실적_시작을_넣는다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 2);
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=2");
        var stage = data.GetProperty("project").GetProperty("stages").EnumerateArray().First(s => s.GetProperty("completedDate").ValueKind == JsonValueKind.Null);
        var yesterday = DateOnly.Parse(KstToday).AddDays(-1).ToString("yyyy-MM-dd");

        var res = await client.SendAsync(Json(token, "SaveWork", new
        {
            id = (int?)null, stageId = stage.GetProperty("id").GetInt32(), title = "해외에서 시작한 작업", owner = "", coOwner = "",
            planStart = KstToday, planEnd = KstToday, actualStart = (string?)null, actualEnd = (string?)null, progress = 10, note = "",
            reason = (string?)null, version = (int?)null, predecessors = Array.Empty<int>(), clientToday = yesterday,
        }));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body);
        var work = JsonDocument.Parse(body).RootElement.GetProperty("project").GetProperty("stages").EnumerateArray()
            .SelectMany(s => s.GetProperty("works").EnumerateArray()).Single(w => w.GetProperty("title").GetString() == "해외에서 시작한 작업");
        Assert.Equal(yesterday, work.GetProperty("actualStart").GetString());
        Assert.Equal("진행 중", work.GetProperty("chip").GetProperty("text").GetString());
    }

    [Fact]
    public async Task 삭제_단추는_권한이_있는_사람에게만_표시한다()
    {
        // 통합테스터는 관리자가 아니고, 샘플 과제의 담당자·등록자도 아니다
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 5);
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=5");
        Assert.False(data.GetProperty("project").GetProperty("canDelete").GetBoolean());

        // 첨부를 올리고 지워도 과제의 변경 시각이 바뀐다 (열려 있는 다른 화면이 알아챌 수 있게)
        async Task<DateTime> StampAsync() =>
            DateTime.Parse((await (await client.SendAsync(JsonGet("/Projects/Detail?handler=Stamp&id=5"))).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("updatedAt").GetString()!).ToUniversalTime();
        var beforeUpload = await StampAsync();

        var up = await client.SendAsync(Upload(token, 5, "내 자료.txt", System.Text.Encoding.UTF8.GetBytes("권한 확인")));
        var body = await up.Content.ReadAsStringAsync();
        Assert.True(up.StatusCode == HttpStatusCode.OK, body);
        var mine = JsonDocument.Parse(body).RootElement.GetProperty("project").GetProperty("attachments")
            .EnumerateArray().Single(a => a.GetProperty("fileName").GetString() == "내 자료.txt");
        Assert.True(mine.GetProperty("canDelete").GetBoolean());

        var afterUpload = await StampAsync();
        Assert.True(afterUpload > beforeUpload, "첨부를 올려도 변경 시각이 그대로");

        var del = await client.SendAsync(Json(token, "DeleteFile", new { id = mine.GetProperty("id").GetInt32() }));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.True(await StampAsync() > afterUpload, "첨부를 지워도 변경 시각이 그대로");
    }

    [Fact]
    public async Task 변경_이력의_시각은_UTC_로_싣고_화면이_현지_시각으로_바꾼다()
    {
        var client = await RegisteredClientAsync();
        var html = await client.GetStringAsync("/Insights/History");
        Assert.Matches("<time class=\"history-time num\" datetime=\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z\">", html);
        // 대시보드의 기준 시각은 서버가 만든 한국 시간임을 밝힌다
        Assert.Contains("(한국 시간)", WebUtility.HtmlDecode(await client.GetStringAsync("/")));
    }
}
