using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GanttScheduler.Services;

namespace GanttScheduler.Tests;

// 업데이트 안전망: 배포 파일 서명, 새 버전 시작 확인과 자동 되돌리기, 받아 오는 곳 주소, 확인 실패 원인, 주기 확인·정리
public partial class UpdateTests
{
    // ── 서명 ────────────────────────────────────────────────────────────
    // 시험용 키 쌍. 실제 키는 tools/release/keygen.sh 로 만들고 개인키는 GitHub 시크릿에만 둔다
    private sealed class Signer : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public UpdateKey Public => new(_key.ExportSubjectPublicKeyInfoPem());
        public string PublicPem => _key.ExportSubjectPublicKeyInfoPem();
        // openssl dgst -sha256 -sign 과 같은 형식 (DER)
        public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        public void Dispose() => _key.Dispose();
    }

    private string ManifestPath => Path.Combine(SourceDir, UpdateSource.ManifestName);

    private void SignManifest(Signer signer) =>
        File.WriteAllBytes(ManifestPath + UpdateKey.SignatureSuffix, signer.Sign(File.ReadAllBytes(ManifestPath)));

    [Fact]
    public async Task 공개키가_든_빌드는_서명이_맞는_배포_정보만_받는다()
    {
        using var signer = new Signer();
        MakePackage("0.3.1");
        SignManifest(signer);
        var svc = Service(key: signer.Public);

        await svc.CheckAsync(CancellationToken.None);
        var st = svc.Status();
        Assert.Null(st.CheckError);
        Assert.True(st.SignatureCheck);
        Assert.Equal(signer.Public.Id, st.KeyId);
        Assert.Equal("0.3.1", st.Latest!.Version);

        // 서명을 확인한 배포 정보로 받고 검증해 풀어 둔다
        svc.StartDownload("테스터");
        for (var i = 0; i < 100 && svc.Status().Phase != "idle"; i++) await Task.Delay(50);
        for (var i = 0; i < 100 && svc.Status().Staged == null && svc.Status().Error == null; i++) await Task.Delay(50);
        Assert.Null(svc.Status().Error);
        Assert.Equal("0.3.1", svc.Status().Staged!.Version);
    }

    [Fact]
    public async Task 서명이_없거나_맞지_않으면_받지_않고_이유를_알린다()
    {
        using var signer = new Signer();
        using var other = new Signer();
        MakePackage("0.3.1");

        // 서명 파일 없음
        var svc = Service(key: signer.Public);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().Latest);
        Assert.Contains("서명", svc.Status().CheckError);

        // 다른 키로 만든 서명 (릴리스 계정이 털려 남이 올린 경우)
        SignManifest(other);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().Latest);
        Assert.Contains("서명이 맞지 않습니다", svc.Status().CheckError);

        // 서명한 뒤 내용(해시)을 바꾼 매니페스트
        SignManifest(signer);
        File.WriteAllText(ManifestPath, File.ReadAllText(ManifestPath).Replace("0.3.1", "0.3.9"));
        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().Latest);
        Assert.Contains("서명이 맞지 않습니다", svc.Status().CheckError);
    }

    [Fact]
    public async Task 공개키가_없는_빌드는_서명_없이_예전처럼_받고_화면에_꺼짐을_알린다()
    {
        MakePackage("0.3.1");
        var svc = Service();
        await svc.CheckAsync(CancellationToken.None);
        var st = svc.Status();
        Assert.Equal("0.3.1", st.Latest!.Version);
        Assert.False(st.SignatureCheck);
        Assert.Null(st.KeyId);
    }

    [Fact]
    public async Task 서명을_확인하지_않은_배포_정보로는_받지_않는다()
    {
        // 키가 없던 옛 버전이 남긴 확인 결과(Signed=false)로 키가 든 새 버전이 받으려는 경우
        using var signer = new Signer();
        var zip = MakePackage("0.3.1");
        var svc = Service(key: signer.Public);
        var unsigned = UpdateSource.ParseManifest(File.ReadAllBytes(ManifestPath))!;
        await svc.DownloadAsync(unsigned, "테스터", CancellationToken.None);
        Assert.Null(svc.Status().Staged);
        Assert.Contains("서명", svc.Status().Error);
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task 서명을_확인하는_서버는_올린_파일도_배포_정보와_서명이_맞아야_받는다()
    {
        using var signer = new Signer();
        using var other = new Signer();
        var zip = MakePackage("0.3.2");
        var manifest = File.ReadAllBytes(ManifestPath);
        var svc = Service(key: signer.Public);

        async Task Upload(byte[]? m, byte[]? sig)
        {
            await using var fs = File.OpenRead(zip);
            await svc.UploadAsync(fs, "테스터", CancellationToken.None, m, sig);
        }

        // zip 만, 서명 없이, 다른 키의 서명
        Assert.Contains("서명", (await Assert.ThrowsAsync<UpdateException>(() => Upload(null, null))).Message);
        Assert.Contains("서명", (await Assert.ThrowsAsync<UpdateException>(() => Upload(manifest, null))).Message);
        await Assert.ThrowsAsync<UpdateException>(() => Upload(manifest, other.Sign(manifest)));
        // 서명은 맞지만 다른 zip 의 배포 정보 (해시가 다름)
        var otherManifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(UpdateSource.ParseManifest(manifest)!.Sha256!, new string('b', 64)));
        Assert.Contains("SHA-256", (await Assert.ThrowsAsync<UpdateException>(() => Upload(otherManifest, signer.Sign(otherManifest)))).Message);
        Assert.Null(svc.Status().Staged);
        Assert.Equal("idle", svc.Status().Phase);

        await Upload(manifest, signer.Sign(manifest));
        Assert.Equal("0.3.2", svc.Status().Staged!.Version);
    }

    [Fact]
    public async Task 공개키가_없는_서버도_배포_정보를_같이_올리면_zip_이_같은_파일인지_본다()
    {
        var zip = MakePackage("0.3.2");
        var manifest = File.ReadAllBytes(ManifestPath);
        var svc = Service();
        var wrong = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(UpdateSource.ParseManifest(manifest)!.Sha256!, new string('c', 64)));
        await using (var fs = File.OpenRead(zip))
            await Assert.ThrowsAsync<UpdateException>(() => svc.UploadAsync(fs, "테스터", CancellationToken.None, wrong));
        await using (var fs = File.OpenRead(zip))
            await svc.UploadAsync(fs, "테스터", CancellationToken.None, manifest);
        Assert.Equal("0.3.2", svc.Status().Staged!.Version);
    }

    // 업데이트 화면의 올리기: 한 입력란에서 여러 파일을 골라도 확장자로 zip·배포 정보·서명을 가른다
    private static async Task<Microsoft.AspNetCore.Mvc.JsonResult> PostUpload(UpdateService svc, params (string Name, byte[] Bytes)[] files)
    {
        var page = new GanttScheduler.Pages.Admin.UpdateModel(svc, new CurrentUser { User = new GanttScheduler.Domain.AppUser { Name = "관리자", IsAdmin = true } });
        var form = new Microsoft.AspNetCore.Http.FormFileCollection();
        foreach (var (name, bytes) in files)
            form.Add(new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=x";
        http.Request.Form = new Microsoft.AspNetCore.Http.FormCollection(new(), form);
        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http };
        return Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(await page.OnPostUploadAsync());
    }

    [Fact]
    public async Task 업데이트_화면은_zip_과_배포_정보_서명을_함께_받는다()
    {
        using var signer = new Signer();
        var zip = File.ReadAllBytes(MakePackage("0.3.2"));
        var manifest = File.ReadAllBytes(ManifestPath);
        var svc = Service(key: signer.Public);

        // zip 만 올리면 서명이 없어 거절 (원인과 해결 방법을 같이 준다)
        var only = await PostUpload(svc, ("GanttScheduler-win-x64.zip", zip));
        Assert.Equal(400, only.StatusCode);
        Assert.Contains("fix", JsonSerializer.Serialize(only.Value));
        // 올릴 수 없는 파일
        Assert.Equal(400, (await PostUpload(svc, ("GanttScheduler-win-x64.zip", zip), ("readme.txt", new byte[] { 1 }))).StatusCode);

        var ok = await PostUpload(svc, ("GanttScheduler.json", manifest), ("GanttScheduler-win-x64.zip", zip), ("GanttScheduler.json.sig", signer.Sign(manifest)));
        Assert.Null(ok.StatusCode);
        Assert.Equal("0.3.2", svc.Status().Staged!.Version);
        Assert.Equal("upload", svc.Status().Staged!.Origin);
    }

    [Fact]
    public void 공개키_파일에_키가_여럿이면_하나라도_맞으면_받는다()
    {
        // 키를 바꿀 때: 새 키를 덧붙인 버전을 옛 키로 서명해 내보내고, 그 뒤 새 키로 서명한다
        using var oldKey = new Signer();
        using var newKey = new Signer();
        var both = new UpdateKey(oldKey.PublicPem + "\n" + newKey.PublicPem);
        var data = Encoding.UTF8.GetBytes("""{ "version": "1.0.0" }""");
        Assert.True(both.Verify(data, oldKey.Sign(data)));
        Assert.True(both.Verify(data, newKey.Sign(data)));
        Assert.Contains(", ", both.Id);
        // 텍스트(base64)로 옮겨 붙인 서명도 받는다
        Assert.True(both.Verify(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(newKey.Sign(data)))));

        using var stranger = new Signer();
        Assert.False(both.Verify(data, stranger.Sign(data)));
        Assert.False(both.Verify(data, null));
        Assert.False(both.Verify(data, new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => new UpdateKey("공개키 아님"));
    }

    [Fact]
    public void 저장소에_공개키가_없으면_빌드는_서명을_확인하지_않는다()
    {
        // 키는 사용자가 keygen.sh 로 만든다. 만들기 전 빌드는 지금처럼 동작하고 화면에 '서명 검증 꺼짐'을 보인다
        var repoKey = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GanttScheduler", UpdateKey.ResourceName);
        Assert.Equal(File.Exists(repoKey), UpdateKey.FromAssembly() != null);
    }

    // ── 받아 오는 곳: 주소 조립과 확인 실패 원인 ─────────────────────────
    // 네트워크 없이 GitHub·사내 서버 응답을 흉내 낸다. 요청한 주소를 차례로 기록한다
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
        public List<string> Requests { get; } = new();
        public FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return _answer(request, ct);
        }

        public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
        public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private (UpdateService Svc, FakeHttp Http) Remote(string source, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, UpdateKey? key = null)
    {
        var http = new FakeHttp(answer);
        var src = new UpdateSource(source, Release, key, http) { ApiTimeout = TimeSpan.FromMilliseconds(300) };
        return (Service(source: src), http);
    }

    [Fact]
    public async Task GitHub_소스는_github_com_의_최신_릴리스_주소에서_매니페스트와_패키지를_받는다()
    {
        var zip = MakePackage("0.3.1");
        var manifest = File.ReadAllBytes(ManifestPath);
        var (svc, http) = Remote("github:acme/releases", (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath switch
        {
            "/acme/releases/releases/latest/download/GanttScheduler.json" => FakeHttp.Bytes(manifest),
            "/acme/releases/releases/download/v0.3.1/pkg-0.3.1.zip" => FakeHttp.Bytes(File.ReadAllBytes(zip)),
            _ => FakeHttp.Status(HttpStatusCode.NotFound),
        }));

        await svc.CheckAsync(CancellationToken.None);
        Assert.Equal("0.3.1", svc.Status().Latest!.Version);
        Assert.Equal("https://github.com/acme/releases/releases/latest/download/GanttScheduler.json", Assert.Single(http.Requests));
        Assert.Equal("https://github.com/acme/releases/releases/latest", svc.Status().BrowserPage);
        Assert.Equal("GitHub 릴리스 (acme/releases)", svc.Status().Source);

        await svc.DownloadAsync(UpdateSource.ParseManifest(manifest)!, "테스터", CancellationToken.None);
        Assert.Null(svc.Status().Error);
        Assert.Equal("0.3.1", svc.Status().Staged!.Version);
        Assert.Contains("https://github.com/acme/releases/releases/download/v0.3.1/pkg-0.3.1.zip", http.Requests);
    }

    [Fact]
    public async Task 사내_서버_주소는_그_아래의_매니페스트와_서명을_읽는다()
    {
        using var signer = new Signer();
        MakePackage("0.3.1");
        var manifest = File.ReadAllBytes(ManifestPath);
        var (svc, http) = Remote("https://intra.example/gantt/", (req, _) => Task.FromResult(req.RequestUri!.ToString() switch
        {
            "https://intra.example/gantt/GanttScheduler.json" => FakeHttp.Bytes(manifest),
            "https://intra.example/gantt/GanttScheduler.json.sig" => FakeHttp.Bytes(signer.Sign(manifest)),
            _ => FakeHttp.Status(HttpStatusCode.NotFound),
        }), signer.Public);

        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().CheckError);
        Assert.Equal("0.3.1", svc.Status().Latest!.Version);
        Assert.Equal(new[] { "https://intra.example/gantt/GanttScheduler.json", "https://intra.example/gantt/GanttScheduler.json.sig" }, http.Requests);
        Assert.Equal("https://intra.example/gantt/", svc.Status().BrowserPage);
    }

    [Fact]
    public async Task 폴더_소스가_없으면_폴더를_열_수_없다고_알린다()
    {
        var src = new UpdateSource(Path.Combine(_root, "없는 폴더"), Release, null, null);
        var svc = Service(source: src);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Contains("업데이트 폴더를 열 수 없습니다", svc.Status().CheckError);
        Assert.Null(svc.Status().BrowserPage);
    }

    [Fact]
    public async Task 매니페스트가_404_면_API_시간_초과가_원인을_가리지_않는다()
    {
        // 사내망: github.com 은 열려 있지만 릴리스에 매니페스트가 없고, api.github.com 은 막혀 응답이 없다
        var (svc, http) = Remote("github:acme/releases", async (req, ct) =>
        {
            if (req.RequestUri!.Host == "api.github.com") await Task.Delay(Timeout.Infinite, ct);
            return FakeHttp.Status(HttpStatusCode.NotFound);
        });
        var watch = Stopwatch.StartNew();
        await svc.CheckAsync(CancellationToken.None);
        var st = svc.Status();
        Assert.Equal("배포된 버전 정보를 찾지 못했습니다.", st.CheckError);
        Assert.DoesNotContain("응답하지", st.CheckError);
        // API 단계는 짧게 끊는다 (전체 제한 30초를 다 쓰지 않는다)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Contains(http.Requests, u => u.StartsWith("https://api.github.com/repos/acme/releases/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 첫_단계가_연결_실패면_그것을_원인으로_알린다()
    {
        var (svc, _) = Remote("github:acme/releases", async (req, ct) =>
        {
            if (req.RequestUri!.Host == "api.github.com") await Task.Delay(Timeout.Infinite, ct);
            throw new HttpRequestException("연결 거부");
        });
        await svc.CheckAsync(CancellationToken.None);
        Assert.Contains("연결하지 못했습니다", svc.Status().CheckError);
    }

    [Fact]
    public async Task 매니페스트가_없는_릴리스는_서명을_확인하는_빌드에서_받지_않는다()
    {
        // 리다이렉트로 버전은 알아도, 그 버전의 서명된 매니페스트가 없으면 받지 않는다
        using var signer = new Signer();
        var (svc, _) = Remote("github:acme/releases", (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath == "/acme/releases/releases/latest"
            ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://github.com/acme/releases/releases/tag/v0.3.5") }
            : FakeHttp.Status(HttpStatusCode.NotFound)), signer.Public);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Null(svc.Status().Latest);
        Assert.Contains("v0.3.5", svc.Status().CheckError);
        Assert.Contains("서명", svc.Status().CheckError);
    }

    // ── 주기 확인·정리 ──────────────────────────────────────────────────
    [Fact]
    public async Task 주기_확인은_새_버전을_미리_받아_두지만_되돌렸거나_시작되지_않은_버전은_받지_않는다()
    {
        MakePackage("0.3.1");
        var svc = Service();
        await svc.AutoCheckAsync(CancellationToken.None);
        Assert.Equal("0.3.1", svc.Status().Staged!.Version);
        Assert.Equal("자동", svc.Status().Staged!.By);

        foreach (var state in new[]
                 {
                     new UpdateState { SkipAuto = "0.3.1" },
                     new UpdateState { StartFailure = new StartFailure("0.3.1", "0.3.0", "시작되지 않음", null, true, DateTime.UtcNow) },
                     new UpdateState { AutoApply = false },
                 })
        {
            TryDeleteDir(DataDir);
            WriteState(state);
            var other = Service();
            await other.AutoCheckAsync(CancellationToken.None);
            Assert.Equal("0.3.1", other.Status().Latest?.Version);   // 확인은 한다
            Assert.Null(other.Status().Staged);                      // 받지는 않는다
        }

        // 주기 확인을 끈 서버, 로컬 빌드는 묻지도 않는다
        TryDeleteDir(DataDir);
        WriteState(new UpdateState { AutoCheck = false });
        var off = Service();
        await off.AutoCheckAsync(CancellationToken.None);
        Assert.Null(off.Status().CheckedAt);
        TryDeleteDir(DataDir);
        var local = Service(new AppBuild(new Version(0, 3, 0), null, "local", ""));
        await local.AutoCheckAsync(CancellationToken.None);
        Assert.Null(local.Status().CheckedAt);
    }

    [Fact]
    public void 정리는_받아_둔_버전만_남기고_쓰고_남은_폴더와_받다_만_파일을_지운다()
    {
        var keep = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(keep, "0.3.1");
        MakeVersion(Path.Combine(DataDir, "staged", "0.2.9"), "0.2.9");
        MakeVersion(Path.Combine(DataDir, "rolledback"), "0.2.8");
        MakeVersion(Path.Combine(DataDir, "previous"), "0.2.7");   // 이전 버전 기록이 없는 보관 폴더
        File.WriteAllText(Path.Combine(DataDir, "GanttScheduler_0.3.2.zip.partial"), "받다 만 파일");
        WriteState(new UpdateState { Staged = new StagedUpdate("0.3.1", keep, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "자동") });

        Service().Cleanup();

        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "staged", "0.2.9")));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "rolledback")));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "previous")));
        Assert.Empty(Directory.GetFiles(DataDir, "*.partial"));
    }

    [Fact]
    public void 시작되지_않은_버전은_자동으로_적용하지_않는다()
    {
        var idle = TimeSpan.FromMinutes(31);
        var failed = Ready();
        failed.StartFailure = new StartFailure("0.3.1", "0.3.0", "시작하다 멈춤", null, true, Night);
        Assert.False(UpdateService.ShouldAutoApply(failed, Release, Night, Night - idle));
        // 다른(더 새) 버전은 그대로 자동 적용 대상이다
        var newer = Ready("0.3.2");
        newer.StartFailure = failed.StartFailure;
        Assert.True(UpdateService.ShouldAutoApply(newer, Release, Night, Night - idle));
    }

    // ── 시작 확인과 자동 되돌리기 ────────────────────────────────────────
    [Fact]
    public void 서버가_요청을_받기_시작하면_시작_표시를_남기고_도우미가_그것을_읽는다()
    {
        var svc = Service();
        var path = Path.Combine(DataDir, StartedMark.FileName);
        Assert.Null(UpdateApplier.StartedVersion(path));

        svc.MarkStarted();

        var mark = JsonSerializer.Deserialize<StartedMark>(File.ReadAllText(path))!;
        Assert.Equal(("0.3.0", Release.Id, Environment.ProcessId), (mark.Version, mark.Build, mark.Pid));
        Assert.Equal("0.3.0", UpdateApplier.StartedVersion(path));
        Assert.False(File.Exists(path + ".tmp"));

        // 표시가 있으면 바로, 다른 버전의 표시면 시간이 다 될 때까지, 서버가 먼저 끝나면 바로 끝낸다
        Assert.True(UpdateApplier.WaitStarted(path, "0.3.0", DateTime.UtcNow.AddSeconds(30), () => false));
        var watch = Stopwatch.StartNew();
        Assert.False(UpdateApplier.WaitStarted(path, "0.3.1", DateTime.UtcNow.AddSeconds(30), () => true));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(UpdateApplier.WaitStarted(path, "0.3.1", DateTime.UtcNow.AddMilliseconds(300), () => false));

        File.WriteAllText(path, "{ 깨진");
        Assert.Null(UpdateApplier.StartedVersion(path));
    }

    [Fact]
    public void 시작_오류_기록은_이번에_다시_띄운_뒤의_것만_읽는다()
    {
        var log = Path.Combine(AppDir, "App_Data", ServiceInstaller.StartupErrorFile);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "2026-10-11 01:00:00\nSystem.IO.InvalidDataException: Failed to load configuration\n   at ...");
        Assert.Contains("InvalidDataException", UpdateApplier.StartupError(AppDir, DateTime.UtcNow.AddMinutes(-1)));
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-1));
        Assert.Null(UpdateApplier.StartupError(AppDir, DateTime.UtcNow.AddMinutes(-1)));
    }

    // 도우미를 이 프로세스 안에서 돌린다. 다시 띄우는 서버 대신 바로 끝나는 명령(dotnet --version)을 써서 "시작하자마자 죽는 새 버전"을 흉내 낸다
    private (int Code, string Staged, string Previous) RunHelperWithCrashingServer(string fromSchema, string toSchema)
    {
        MakeVersion(AppDir, "0.3.0", "old-only.txt");
        var staged = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(staged, "0.3.1", "new-only.txt");
        var previous = Path.Combine(DataDir, "previous");
        // 서버가 도우미를 띄우기 직전에 남기는 상태 (Launch)
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), toSchema, null, DateTime.UtcNow, "download", "자동"),
            Previous = new PreviousVersion("0.2.9", fromSchema, previous, DateTime.UtcNow),
            Pending = new PendingSwap("apply", "0.3.0", "0.3.1", fromSchema, "자동", DateTime.UtcNow),
            History = { new UpdateLog(DateTime.UtcNow.AddDays(-1), "apply", "0.2.9", "0.3.0", "평영", true, null) },
        });
        File.WriteAllText(Path.Combine(DataDir, StartedMark.FileName), """{ "Build": "0.3.0", "Version": "0.3.0", "Pid": 1, "At": "2026-10-11T00:00:00Z" }""");

        var code = UpdateApplier.Run(new[]
        {
            UpdateApplier.Flag, "--kind", "apply", "--app", AppDir, "--from", staged, "--backup", previous,
            "--result", Path.Combine(DataDir, "result.json"), "--from-version", "0.3.0", "--to", "0.3.1",
            "--verify-seconds", "20", "--from-schema", fromSchema, "--to-schema", toSchema, "--unknown-later-option", "무시",
            "--restart", "dotnet", "--restart-dir", AppDir, "--", "--version",
        });
        return (code, staged, previous);
    }

    [Fact]
    public void 새_버전이_시작하다_멈추면_도우미가_이전_버전으로_되돌리고_다시_뜬_서버가_이력에_남긴다()
    {
        var (code, staged, _) = RunHelperWithCrashingServer("S1", "S1");

        Assert.Equal(1, code);
        // 파일은 이전 버전으로 돌아왔다
        Assert.Equal("dll 0.3.0", Read(AppDir, UpdateApplier.EntryDll));
        Assert.True(File.Exists(Path.Combine(AppDir, "old-only.txt")));
        Assert.False(File.Exists(Path.Combine(AppDir, "new-only.txt")));
        Assert.False(File.Exists(Path.Combine(AppDir, UpdateApplier.OfflineFile)));
        // 결과: 실패, 되돌렸다는 이유
        var result = JsonSerializer.Deserialize<UpdateApplier.Result>(File.ReadAllText(Path.Combine(DataDir, "result.json")))!;
        Assert.False(result.Ok);
        Assert.Contains("되돌렸습니다", result.Error);
        // 상태: 교체 전 상태로 돌려놓고, 이 버전은 자동으로 다시 적용하지 않는다(옛 버전도 아는 SkipAuto), 시작 실패 기록
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "state.json")))!;
        Assert.Equal("0.3.1", state["SkipAuto"]!.GetValue<string>());
        Assert.Equal("0.3.1", state["StartFailure"]!["Version"]!.GetValue<string>());
        Assert.True(state["StartFailure"]!["RolledBack"]!.GetValue<bool>());
        Assert.NotNull(state["Pending"]);
        Assert.Null(state["Previous"]);   // 보관 폴더는 이번 교체로 0.3.0 파일이 됐다
        Assert.Contains("시작되지 않음", File.ReadAllText(Path.Combine(DataDir, "apply.log")));

        // 이전 버전(0.3.0)이 다시 떠서 결과를 정리한다
        var svc = Service();
        svc.Startup();
        var st = svc.Status();
        var log = st.History.First();
        Assert.Equal(("apply", "0.3.0", "0.3.1", false), (log.Kind, log.From, log.To, log.Ok));
        Assert.Contains("되돌렸습니다", log.Error);
        Assert.Equal(2, st.History.Count);   // 예전 이력은 그대로
        Assert.Equal("0.3.1", st.StartFailure!.Version);
        Assert.True(st.StartFailure.RolledBack);
        Assert.Equal("apply", st.StartFailure.Retry);        // 받아 둔 파일로 다시 시도할 수 있다
        Assert.Equal("0.3.1", st.Staged!.Version);
        Assert.Null(st.Previous);
        Assert.True(Directory.Exists(staged));

        // 안내를 닫아도 자동으로 다시 적용하지 않는 것은 그대로
        svc.DismissFailure();
        Assert.Null(svc.Status().StartFailure);
        Assert.Equal("0.3.1", svc.Status().SkipAuto);
    }

    [Fact]
    public void DB_구조가_바뀌는_버전은_시작되지_않아도_되돌리지_않고_복구_방법을_남긴다()
    {
        var (code, _, _) = RunHelperWithCrashingServer("S1", "S2");

        Assert.Equal(1, code);
        Assert.Equal("dll 0.3.1", Read(AppDir, UpdateApplier.EntryDll));   // 새 버전 그대로
        var result = JsonSerializer.Deserialize<UpdateApplier.Result>(File.ReadAllText(Path.Combine(DataDir, "result.json")))!;
        Assert.False(result.Ok);
        Assert.Contains("백업을 복원", result.Error);
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "state.json")))!;
        Assert.False(state["StartFailure"]!["RolledBack"]!.GetValue<bool>());
        Assert.Null(state["SkipAuto"]);

        // 새 버전이 나중에라도 뜨면(예: DB 가 늦게 살아남) 그것이 성공이고 시작 실패 기록은 지운다
        var svc = Service(new AppBuild(new Version(0, 3, 1), "def5678", "release", "S2"));
        svc.Startup();
        var st = svc.Status();
        Assert.True(st.History.First().Ok);
        Assert.Null(st.StartFailure);
    }

    [Fact]
    public void 시작에_성공한_버전은_시작_실패_기록을_지운다()
    {
        WriteState(new UpdateState
        {
            Pending = new PendingSwap("apply", "0.2.9", "0.3.0", "S1", "평영", DateTime.UtcNow),
            StartFailure = new StartFailure("0.3.0", "0.2.9", "시작하다 멈춤", null, true, DateTime.UtcNow.AddDays(-1)),
            SkipAuto = "0.3.0",
        });
        var svc = Service();   // 0.3.0 이 떴다 (다시 시도 성공)
        svc.Startup();
        Assert.Null(svc.Status().StartFailure);
        Assert.True(svc.Status().History.First().Ok);
    }

    [Fact]
    public void 새_버전이_요청을_받기_전에_죽어도_받아_둔_폴더가_남아_다시_시도할_수_있다()
    {
        // 0.3.0 서버가 0.3.1 적용을 시작하며 남긴 상태 (도우미는 이것을 교체 전 스냅숏으로 쥐고 있다)
        var staged = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(staged, "0.3.1");
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "자동"),
            Pending = new PendingSwap("apply", "0.3.0", "0.3.1", "S1", "자동", DateTime.UtcNow),
        });
        var snapshot = File.ReadAllText(Path.Combine(DataDir, "state.json"));

        // 0.3.1 이 시작 단계(StartupWork)를 마쳤지만 요청을 받기 전에 죽었다 (ApplicationStarted 없음)
        var fresh = Service(new AppBuild(new Version(0, 3, 1), "def5678", "release", "S1"));
        fresh.Startup();
        fresh.Cleanup();
        Assert.Null(UpdateApplier.MissingFile(staged));   // 아직 지우지 않았다

        // 도우미가 되돌리며 상태를 스냅숏으로 돌려놓고 시작 실패를 기록한 뒤, 0.3.0 이 다시 뜬다
        var state = JsonNode.Parse(snapshot)!;
        state["SkipAuto"] = "0.3.1";
        state["StartFailure"] = JsonSerializer.SerializeToNode(new StartFailure("0.3.1", "0.3.0", "시작하다 멈춤", null, true, DateTime.UtcNow));
        File.WriteAllText(Path.Combine(DataDir, "state.json"), state.ToJsonString());
        var old = Service();
        old.Startup();

        var st = old.Status();
        Assert.Equal("0.3.1", st.Staged!.Version);
        Assert.Equal("apply", st.StartFailure!.Retry);
        Assert.Null(UpdateApplier.MissingFile(staged));   // '다시 시도'가 온전한 파일로 적용한다
    }

    [Fact]
    public void 일부만_남은_받아_둔_폴더는_적용하지_않고_다시_받게_한다()
    {
        // Windows 에서 지우다 만 폴더: 잠겨 있던 실행 파일은 남고 나머지가 빠졌다
        var staged = Path.Combine(DataDir, "staged", "0.3.1");
        MakeVersion(staged, "0.3.1");
        File.Delete(Path.Combine(staged, "wwwroot", "js", "app.js"));
        Assert.Equal("wwwroot/js/app.js", UpdateApplier.MissingFile(staged));
        WriteState(new UpdateState { Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "자동") });

        var svc = Service();
        var ex = Assert.Throws<UpdateException>(() => svc.Apply("테스터"));
        Assert.Contains("app.js", ex.Message);
        Assert.Null(svc.Status().Staged);   // 다시 받게 한다

        // 시작할 때도 같은 기준으로 버린다 (다시 시도가 반쪽 폴더를 가리키지 않게)
        WriteState(new UpdateState
        {
            Staged = new StagedUpdate("0.3.1", staged, new string('a', 64), "S1", null, DateTime.UtcNow, "download", "자동"),
            StartFailure = new StartFailure("0.3.1", "0.3.0", "시작하다 멈춤", null, true, DateTime.UtcNow),
        });
        var restarted = Service();
        restarted.Startup();
        Assert.Null(restarted.Status().Staged);
        Assert.NotEqual("apply", restarted.Status().StartFailure!.Retry);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { /* 무시 */ }
    }
}
