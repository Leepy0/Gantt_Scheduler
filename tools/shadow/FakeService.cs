namespace GanttScheduler.Services;

// 섀도 빌드용: 서비스 호스팅 패키지가 없으므로 아무것도 하지 않는다 (Services/WindowsServiceSupport.cs 의 자리)
public static class WindowsServiceSupport
{
    public static void Use(WebApplicationBuilder builder, string serviceName) { }
}
