using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GanttScheduler.Tests;

// 일정 엔진 관련 HTTP 확인 (같은 앱·사용자 도우미를 쓰도록 HttpTests 의 나머지 부분)
public partial class HttpTests
{
    private static JsonElement StageOf(JsonElement project, string workTitle) =>
        project.GetProperty("stages").EnumerateArray()
            .First(s => s.GetProperty("works").EnumerateArray().Any(w => w.GetProperty("title").GetString() == workTitle));

    [Fact]
    public async Task 범위_밖_날짜는_400_과_필드로_알리고_단계_완료와_함께_남은_작업을_닫는다()
    {
        var (client, token) = await DetailAsync(await RegisteredClientAsync(), 2);
        var data = await client.GetFromJsonAsync<JsonElement>("/Projects/Detail?handler=Data&id=2");
        var stage = StageOf(data.GetProperty("project"), "출하 검사");      // 제작 단계: 완료·진행 중·예정 작업
        var stageId = stage.GetProperty("id").GetInt32();
        Assert.Equal(2, stage.GetProperty("openWorks").GetInt32());

        object Body(string? target, string? completed, bool closeWorks) => new
        {
            projectId = 2, id = stageId, name = stage.GetProperty("name").GetString(),
            targetDate = target, completedDate = completed, reason = "범위 확인", cascade = false,
            version = stage.GetProperty("version").GetInt32(), closeWorks,
        };

        // 연도 오타는 저장 전에 거절하고, 상세 화면은 계속 열린다
        var bad = await client.SendAsync(Json(token, "SaveStage", Body("2620-10-01", null, false)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("targetDate", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Projects/Detail?id=2")).StatusCode);

        // 완료하면서 남은 작업을 닫는다 (closeWorks 가 JSON 에서 그대로 묶인다)
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9)).ToString("yyyy-MM-dd");   // 앱의 '오늘'(한국 시간)
        var ok = await client.SendAsync(Json(token, "SaveStage", Body(stage.GetProperty("targetDate").GetString(), today, true)));
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, body);
        var after = StageOf(JsonDocument.Parse(body).RootElement.GetProperty("project"), "출하 검사");
        Assert.Equal("done", after.GetProperty("status").GetString());
        Assert.All(after.GetProperty("works").EnumerateArray(), w =>
        {
            Assert.NotEqual(JsonValueKind.Null, w.GetProperty("actualEnd").ValueKind);
            Assert.False(w.GetProperty("late").GetBoolean());
        });
    }
}
