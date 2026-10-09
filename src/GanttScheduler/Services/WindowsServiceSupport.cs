namespace GanttScheduler.Services;

// Windows 서비스로 시작됐을 때 서비스 제어 관리자(SCM)의 시작·중지 신호에 답하게 한다.
// 패키지(Microsoft.Extensions.Hosting.WindowsServices)를 쓰는 곳은 이 파일 하나로 모은다 — 섀도 빌드는 이 파일을 가짜로 바꾼다
public static class WindowsServiceSupport
{
    public static void Use(WebApplicationBuilder builder, string serviceName) =>
        builder.Host.UseWindowsService(options => options.ServiceName = serviceName);
}
