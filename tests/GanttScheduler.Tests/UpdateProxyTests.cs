using System.Net;
using GanttScheduler.Services;
using Microsoft.Extensions.Configuration;

namespace GanttScheduler.Tests;

public class UpdateProxyTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] items) =>
        new ConfigurationBuilder().AddInMemoryCollection(items.ToDictionary(i => i.Key, i => i.Value)).Build();

    [Fact]
    public void 주소가_비어_있으면_시스템_프록시다()
    {
        Assert.Null(UpdateProxy.From(Config()));
        Assert.Null(UpdateProxy.From(Config(("Update:Proxy:Address", "  "))));
    }

    [Fact]
    public void 주소만_적으면_Windows_로그인으로_인증한다()
    {
        var p = UpdateProxy.From(Config(("Update:Proxy:Address", "proxy.corp:8080")))!;
        Assert.Equal(new Uri("http://proxy.corp:8080"), p.Address);
        Assert.Null(p.User);
        Assert.Contains("Windows 로그인", p.Describe);
        var web = (WebProxy)p.ToWebProxy();
        Assert.Same(CredentialCache.DefaultCredentials, web.Credentials);
        Assert.True(web.BypassProxyOnLocal);
        Assert.Equal(new Uri("http://proxy.corp:8080"), web.GetProxy(new Uri("https://github.com/x")));
    }

    [Fact]
    public void 계정을_적으면_그_계정으로_인증하고_비밀번호는_보이지_않는다()
    {
        var p = UpdateProxy.From(Config(("Update:Proxy:Address", "http://proxy.corp:3128"), ("Update:Proxy:User", "svc"), ("Update:Proxy:Password", "비밀")))!;
        var cred = Assert.IsType<NetworkCredential>(((WebProxy)p.ToWebProxy()).Credentials);
        Assert.Equal(("svc", "비밀"), (cred.UserName, cred.Password));
        Assert.Contains("svc", p.Describe);
        Assert.DoesNotContain("비밀", p.Describe);
    }

    [Fact]
    public void 잘못된_주소는_시작할_때_거절한다()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => UpdateProxy.From(Config(("Update:Proxy:Address", "ftp://x:1"))));
        Assert.Contains("Update:Proxy:Address", ex.Message);
        Assert.Throws<InvalidOperationException>(() => UpdateProxy.From(Config(("Update:Proxy:Address", "::not a url::"))));
    }

    [Fact]
    public void 설정한_프록시가_HTTP_처리기에_들어간다()
    {
        var proxy = UpdateProxy.From(Config(("Update:Proxy:Address", "http://proxy.corp:8080")));
        var src = new UpdateSource("github:a/b", new AppBuild(new Version(0, 1, 0), null, "release", "S1"), null, null, proxy);
        Assert.Equal("http://proxy.corp:8080 (Windows 로그인 인증)", src.Proxy!.Describe);
    }
}
