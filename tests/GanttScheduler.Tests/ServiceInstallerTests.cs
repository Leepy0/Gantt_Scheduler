using GanttScheduler.Services;

namespace GanttScheduler.Tests;

// Windows 서비스 등록 명령의 계산 부분. 실제 등록·시작·업데이트 뒤 재시작은 tools/release/selftest.sh (MODE=service) 가 확인한다
public class ServiceInstallerTests
{
    private const string Sid = "S-1-5-80-1234567890-123456789-1234567890-123456789-1234567890";

    [Theory]
    [InlineData(new[] { "install" }, true)]
    [InlineData(new[] { "uninstall", "--name", "X" }, true)]
    [InlineData(new[] { "--urls", "http://*:80" }, false)]
    [InlineData(new[] { "--service", "GanttScheduler" }, false)]
    [InlineData(new string[0], false)]
    public void 설치_명령인지_가린다(string[] args, bool expected) => Assert.Equal(expected, ServiceInstaller.IsCommand(args));

    [Fact]
    public void 서비스로_시작됐는지는_인자의_서비스_이름으로_안다()
    {
        Assert.Equal("GanttScheduler", ServiceInstaller.ServiceNameFrom(new[] { "--service", "GanttScheduler", "--urls", "http://*:80" }));
        Assert.Equal("Other", ServiceInstaller.ServiceNameFrom(new[] { "--urls", "http://*:80", "--service", "Other" }));
        Assert.Null(ServiceInstaller.ServiceNameFrom(new[] { "--urls", "http://*:80" }));
        Assert.Null(ServiceInstaller.ServiceNameFrom(new[] { "--service" }));
        Assert.Null(ServiceInstaller.ServiceNameFrom(Array.Empty<string>()));
    }

    [Fact]
    public void 서비스_실행_명령에는_이름과_포트가_들어가고_경로는_따옴표로_감싼다()
    {
        var bin = ServiceInstaller.BinPath(@"D:\Program Files\GanttScheduler\GanttScheduler.exe", "GanttScheduler", 8080);
        Assert.Equal("\"D:\\Program Files\\GanttScheduler\\GanttScheduler.exe\" --service GanttScheduler --urls http://*:8080", bin);
        // 이 명령으로 시작된 프로세스는 자기가 어느 서비스인지 안다
        Assert.Equal("GanttScheduler", ServiceInstaller.ServiceNameFrom(bin.Split(' ')));
    }

    [Fact]
    public void 서비스_시작_권한은_감사_설정_앞에_넣는다()
    {
        const string dacl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
        const string sacl = "S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";

        var result = ServiceInstaller.AddStartRight(dacl + sacl, Sid);
        Assert.Equal($"{dacl}(A;;RPLC;;;{Sid}){sacl}", result);

        // 감사 설정이 없는 경우, 앞뒤 공백·줄바꿈이 붙은 sc 출력
        Assert.Equal($"{dacl}(A;;RPLC;;;{Sid})", ServiceInstaller.AddStartRight("\r\n" + dacl + "\r\n", Sid));
    }

    [Fact]
    public void 서비스_시작_권한을_두_번_넣지_않는다()
    {
        const string sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";
        var once = ServiceInstaller.AddStartRight(sddl, Sid);
        Assert.Equal(once, ServiceInstaller.AddStartRight(once, Sid));
    }

    [Fact]
    public void 알_수_없는_보안_설명자는_건드리지_않고_거절한다() =>
        Assert.Throws<FormatException>(() => ServiceInstaller.AddStartRight("O:SYG:SY", Sid));

    [Theory]
    [InlineData("        STATE              : 4  RUNNING \r\n", "RUNNING")]
    [InlineData("        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 1  STOPPED\r\n        WIN32_EXIT_CODE    : 0  (0x0)", "STOPPED")]
    [InlineData("        상태               : 2  START_PENDING\r\n", "START_PENDING")]
    [InlineData("        STATE              : 3  STOP_PENDING\r\n", "STOP_PENDING")]
    public void sc_출력에서_서비스_상태를_읽는다(string output, string expected) => Assert.Equal(expected, ServiceInstaller.StateOf(output));

    [Fact]
    public void 서비스가_없다는_출력에서는_상태를_읽지_못한다() =>
        Assert.Null(ServiceInstaller.StateOf("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service."));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData(" 10.0.0.0/8 , 192.168.1.10,localsubnet ", "10.0.0.0/8,192.168.1.10,LocalSubnet")]
    [InlineData("fd00::/8", "fd00::/8")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    public void 방화벽_원격_주소_제한을_읽는다(string? value, string? expected) => Assert.Equal(expected, ServiceInstaller.RemoteIp(value));

    [Theory]
    [InlineData("사내망")]
    [InlineData("10.0.0.0/8;192.168.0.0/16")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10")]
    [InlineData("010.0.0.0/8")]            // 앞자리 0 은 8진수로 읽혀 8.0.0.0/8 이 된다
    [InlineData("192.168.001.010")]
    [InlineData("0x0A.0.0.0/8")]
    public void 방화벽_원격_주소를_잘못_쓰면_등록하기_전에_거절한다(string value) =>
        Assert.Throws<ArgumentException>(() => ServiceInstaller.RemoteIp(value));

    [Fact]
    public void LocalSubnet_을_쓰면_앱_설정에는_실제_대역을_적으라고_안내한다()
    {
        Assert.Contains("같은 값", ServiceInstaller.AllowedNetworksHint("10.0.0.0/8,192.168.1.10"));
        var local = ServiceInstaller.AllowedNetworksHint("LocalSubnet");
        Assert.Contains("CIDR", local);
        Assert.Contains("LocalSubnet 은 방화벽 전용", local);
        Assert.Contains("10.0.0.0/8", ServiceInstaller.AllowedNetworksHint("10.0.0.0/8,LocalSubnet"));
        // 안내대로 그대로 적으면 서버가 시작하지 않는 값이다
        var ex = Assert.Throws<InvalidOperationException>(() => AccessOptions.ParseNetworks(new[] { "LocalSubnet" }));
        Assert.Contains("CIDR", ex.Message);
    }

    [Fact]
    public void 방화벽_규칙은_원격_주소를_줄_때만_제한한다()
    {
        var open = ServiceInstaller.FirewallRule("GanttScheduler", 8080, null);
        Assert.Equal(new[] { "advfirewall", "firewall", "add", "rule", "name=GanttScheduler", "dir=in", "action=allow", "protocol=TCP", "localport=8080" }, open);
        var limited = ServiceInstaller.FirewallRule("GanttScheduler", 80, "10.0.0.0/8,LocalSubnet");
        Assert.Equal("remoteip=10.0.0.0/8,LocalSubnet", limited[^1]);
        Assert.Equal("localport=80", limited[^2]);
    }
}
