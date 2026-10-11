using System.Net;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Tests;

// 분석 확장 화면(추세·변경 분석·부하·기간 보고·이력 Excel·다른 사람의 할 일)
public partial class HttpTests
{
    [Fact]
    public async Task 분석_새_탭과_기간_보고_조건이_열리고_이력_Excel_을_내려받는다()
    {
        var client = await RegisteredClientAsync();
        foreach (var url in new[] { "/Insights/Trend", "/Insights/Trend?range=1y", "/Insights/Changes?months=6", "/Insights/Load?weeks=12",
                     "/Insights/Weekly?period=month", "/Insights/Weekly?period=custom&from=2026-09-01&to=2026-10-09&changed=true" })
        {
            var res = await client.GetAsync(url);
            Assert.True(res.StatusCode == HttpStatusCode.OK, url + " " + res.StatusCode);
        }
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Insights/Weekly?period=month"));
        Assert.Contains("월간 보고", html);
        Assert.Contains("다음 달", html);

        var xlsx = await client.GetAsync("/Insights/History?handler=Excel&kind=schedule");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Contains("변경이력_", xlsx.Content.Headers.ContentDisposition!.FileNameStar ?? xlsx.Content.Headers.ContentDisposition.FileName);
        var report = await client.GetAsync("/Insights/Weekly?handler=Excel&period=month");
        Assert.Contains("월간보고_", report.Content.Headers.ContentDisposition!.FileNameStar ?? "");
    }

    [Fact]
    public async Task 부하에서_연_다른_사람의_할_일은_읽기_전용이고_없는_사람은_404()
    {
        var client = await RegisteredClientAsync();
        int other;
        using (var s = _factory.Db.NewScope())
            other = await s.Db.Users.Where(u => u.Name == "김물류").Select(u => u.Id).FirstAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/My?user={other}"));
        Assert.Contains("김물류 님의 할 일", html);
        Assert.Contains("data-my-readonly", html);
        Assert.DoesNotContain("js/my.js", html);
        Assert.DoesNotContain("id=\"news\"", html);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/My?user=999999")).StatusCode);
        Assert.Contains("js/my.js", await client.GetStringAsync("/My"));
    }
}
