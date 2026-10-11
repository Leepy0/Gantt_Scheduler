using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GanttScheduler.Domain;

namespace GanttScheduler.Services;

// ── 이 빌드의 정보 ───────────────────────────────────────────────────────
// CI 가 Version · SourceRevisionId(커밋) · BuildChannel · SchemaId 를 넣는다
public record AppBuild(Version Version, string? Commit, string Channel, string Schema)
{
    public string VersionText => Version.ToString(3);

    // 브라우저가 "서버가 바뀌었는지" 비교하는 값 (되돌리기도 잡히도록 커밋까지 포함)
    public string Id => Commit == null ? VersionText : VersionText + "+" + Commit;

    public string ChannelName => Channel switch
    {
        "release" => "정식 배포",
        "dev" => "테스트 빌드",
        _ => "로컬 빌드",
    };

    // CI 가 만든 배포 패키지만 스스로 교체한다 (로컬 빌드는 bin 폴더라 교체하지 않는다)
    public bool SelfUpdating => Channel is "release" or "dev";

    public static AppBuild FromAssembly()
    {
        var asm = typeof(AppBuild).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        var version = TryParseVersion(parts[0], out var v) ? v : new Version(0, 0, 0);
        var commit = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;
        string Meta(string key) => asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
        return new AppBuild(version, commit, Meta("BuildChannel") is { Length: > 0 } c ? c : "local", Meta("SchemaId"));
    }

    // "v0.3.1", "0.3.1", "0.3.1-rc1" → 0.3.1
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        text = (text ?? "").Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) text = text[..cut];
        if (!Version.TryParse(text, out var parsed) || parsed.Minor < 0) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }
}

// 업데이트 실패: 원인(message)과 해결 방법(fix)을 같이 알린다
public class UpdateException : RuleException
{
    public string Fix { get; }
    public UpdateException(string message, string fix) : base(message) => Fix = fix;
}

// ── 저장되는 상태 (App_Data/updates/state.json) ──────────────────────────
// 버전이 바뀌어도 읽을 수 있어야 하므로 항목은 추가만 하고 뜻을 바꾸지 않는다
// Signed: 이 빌드의 공개키로 서명을 확인한 매니페스트에서 읽었는지 (서명 확인을 하는 빌드는 이것만 받는다)
public record ReleaseInfo(string Version, string File, string? Sha256, long Size, DateTime? Published, string? Page, string? Notes, string? Schema, string? Rid,
    bool Signed = false);

public record StagedUpdate(string Version, string Dir, string Sha256, string? Schema, string? Notes, DateTime At, string Origin, string By);

public record PreviousVersion(string Version, string Schema, string Dir, DateTime At);

// Backup: 적용 전에 만든 DB 백업 이름 (구조가 바뀌는 업데이트 — 되돌리려면 이 백업을 복원한다). 나중에 추가한 항목이다
public record UpdateLog(DateTime At, string Kind, string From, string To, string By, bool Ok, string? Error, string? Backup = null);

public record PendingSwap(string Kind, string From, string To, string FromSchema, string By, DateTime At, string? Backup = null);

// 적용한 새 버전이 시작되지 않은 일. 교체 도우미가 기록한다(RolledBack: 이전 버전으로 자동으로 되돌렸는지).
// 이 버전은 자동으로 다시 받거나 적용하지 않는다 — 관리자가 화면에서 '다시 시도'할 수 있다
public record StartFailure(string Version, string From, string Error, string? Detail, bool RolledBack, DateTime At);

// 서버가 요청을 받기 시작했다는 표시 (App_Data/updates/started.json). 교체 도우미가 새 버전이 떴는지 이것으로 안다
public record StartedMark(string Build, string Version, int Pid, DateTime At)
{
    public const string FileName = "started.json";
}

public class UpdateState
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoApply { get; set; } = true;
    public int WindowFrom { get; set; } = 2;     // 자동 적용 시간대 (한국 시간, 시작 시 포함 ~ 끝 시 제외)
    public int WindowTo { get; set; } = 5;
    public int IdleMinutes { get; set; } = 30;   // 이만큼 아무도 쓰지 않았을 때만 자동 적용
    public ReleaseInfo? Latest { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string? CheckError { get; set; }
    public string? CheckFix { get; set; }
    public StagedUpdate? Staged { get; set; }
    public PreviousVersion? Previous { get; set; }
    public PendingSwap? Pending { get; set; }
    public string? SkipAuto { get; set; }        // 되돌린 버전 — 자동으로는 다시 받거나 적용하지 않는다 (직접 누르면 가능)
    public List<UpdateLog> History { get; set; } = new();
    public StartFailure? StartFailure { get; set; }   // 적용했지만 시작되지 않은 버전 (교체 도우미가 기록)
    public DateTime? LastCheckOkAt { get; set; }      // 마지막으로 확인에 성공한 시각 — 확인이 계속 실패하는지 알리기 위해
    public DateTime? CheckFailedSince { get; set; }   // 확인이 실패하기 시작한 시각 (성공하면 비운다)
}

// ── 새 버전을 어디서 찾을지 (Update:Source) ──────────────────────────────
//   github:<owner>/<repo>   GitHub 릴리스. 사내망에서 api.github.com 은 막혀 있어 github.com 주소를 먼저 쓴다
//   https://…               그 주소 아래의 GanttScheduler.json 과 패키지 (사내 서버)
//   폴더 경로               그 폴더의 GanttScheduler.json 과 패키지 (공유 폴더, 인터넷이 없는 서버)
// 빌드에 공개키(update-key.pub)가 들어 있으면 매니페스트 옆의 GanttScheduler.json.sig 로 서명을 확인하고, 맞지 않으면 받지 않는다
public class UpdateSource
{
    public const string ManifestName = "GanttScheduler.json";
    public const string SignatureName = ManifestName + UpdateKey.SignatureSuffix;
    public const string AssetName = "GanttScheduler-win-x64.zip";

    private readonly string _source;
    private readonly HttpClient _http;

    public UpdateSource(string? source, AppBuild build) : this(source, build, UpdateKey.FromAssembly(), null) { }

    // 시험용: 공개키와 HTTP 처리기를 바꿔 끼운다 (네트워크 없이 GitHub 응답을 흉내 낸다)
    internal UpdateSource(string? source, AppBuild build, UpdateKey? key, HttpMessageHandler? handler)
    {
        _source = (source ?? "").Trim();
        Key = key;
        // 시스템 프록시를 쓰고, 사내 프록시가 Windows 로그인 인증을 요구해도 통과하게 한다
        handler ??= new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // 시간 제한은 호출마다 토큰으로
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GanttScheduler/{build.VersionText}");
    }

    // 서명 확인에 쓰는 공개키. null 이면 이 빌드는 서명을 확인하지 않는다(키를 만들기 전 빌드)
    public UpdateKey? Key { get; }

    // api.github.com 단계의 시간 제한. 사내망은 이 주소가 막혀 응답 없이 시간만 끄는 경우가 많아, 전체 제한(30초)을 다 쓰지 않게 따로 둔다
    internal TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public bool Configured => _source.Length > 0;
    private bool IsGitHub => _source.StartsWith("github:", StringComparison.OrdinalIgnoreCase);
    private bool IsHttp => _source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || _source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    private string Repo => _source["github:".Length..].Trim('/');
    private string ReleasesPage => $"https://github.com/{Repo}/releases";

    public string Describe => !Configured ? "지정 안 됨" : IsGitHub ? $"GitHub 릴리스 ({Repo})" : _source;

    // 브라우저로 직접 받을 수 있는 주소 (파일로 올려 적용할 때 안내)
    public string? BrowserPage => IsGitHub ? ReleasesPage + "/latest" : IsHttp ? _source : null;

    // 최신 정식 버전. 정식 릴리스가 없으면 null
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        if (!Configured)
            throw new UpdateException("업데이트를 받아 올 위치가 지정되지 않았습니다.", "설정 Update:Source 를 지정하거나, 배포 파일(zip)을 직접 올려 적용하세요.");
        if (!IsGitHub) return await ManifestAsync(ManifestLocation(null), ct);

        // 매니페스트 → 리다이렉트 → API 순서로 찾는다. 앞 단계가 "없음(404)"으로 끝났으면 그것이 대표 원인이다:
        // 뒤 단계(사내망에서 막힌 api.github.com)의 시간 초과가 '응답 없음'으로 바뀌어 진짜 원인을 가리지 않게 한다
        Exception? first = null;
        var notFound = false;
        foreach (var source in new Func<CancellationToken, Task<ReleaseInfo?>>[] { c => ManifestAsync(ManifestLocation(null), c), FromRedirectAsync, FromApiAsync })
        {
            try
            {
                var r = await source(ct);
                if (r != null) return r;
                notFound = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (notFound) return null;
                throw;
            }
            catch (UpdateException) { throw; }   // 서명 불일치처럼 다음 방법으로 넘어가면 안 되는 실패
            catch (Exception ex) { first ??= ex; }   // 다음 방법으로
        }
        if (notFound) return null;
        if (first != null) throw first;
        return null;
    }

    // 매니페스트 위치: version 이 null 이면 최신, 아니면 그 버전의 릴리스
    private string ManifestLocation(string? version) =>
        IsGitHub ? (version == null ? $"{ReleasesPage}/latest/download/{ManifestName}" : $"{ReleasesPage}/download/v{version}/{ManifestName}")
        : IsHttp ? $"{_source.TrimEnd('/')}/{ManifestName}"
        : Path.Combine(_source, ManifestName);

    // 매니페스트를 읽고, 공개키가 있으면 같은 곳의 .sig 로 서명을 확인한다. 매니페스트가 없으면 null
    private async Task<ReleaseInfo?> ManifestAsync(string location, CancellationToken ct)
    {
        if (!IsGitHub && !IsHttp && !Directory.Exists(_source))
            throw new UpdateException($"업데이트 폴더를 열 수 없습니다: {_source}", "폴더 경로와 서버 계정의 읽기 권한을 확인하세요.");
        var bytes = await GetBytesAsync(location, ct);
        if (bytes == null) return null;
        var signed = false;
        if (Key != null)
        {
            var sig = await GetBytesAsync(location + UpdateKey.SignatureSuffix, ct);
            CheckSignature(Key, bytes, sig);
            signed = true;
        }
        var info = ParseManifest(bytes);
        return info == null ? null : info with { Signed = signed };
    }

    // 서명이 없거나 맞지 않으면 받지 않는다 (올린 파일도 같은 규칙)
    public static void CheckSignature(UpdateKey key, byte[] manifest, byte[]? signature)
    {
        if (signature == null || signature.Length == 0)
            throw new UpdateException($"배포 정보에 서명({SignatureName})이 없습니다.",
                "이 서버는 서명된 배포 파일만 받습니다. 릴리스의 GanttScheduler.json 과 GanttScheduler.json.sig 를 함께 두었는지 확인하세요.");
        if (!key.Verify(manifest, signature))
            throw new UpdateException("배포 정보의 서명이 맞지 않습니다. 정식 배포 파일이 아니거나 내용이 바뀌었습니다.",
                $"공식 릴리스에서 파일을 다시 받아 주세요. 계속되면 서명 키(공개키 지문 {key.Id})가 바뀌지 않았는지 관리자에게 확인하세요.");
    }

    public static ReleaseInfo? ParseManifest(string json) => ParseManifest(Encoding.UTF8.GetBytes(json));

    public static ReleaseInfo? ParseManifest(byte[] bytes)
    {
        // 메모장 등으로 저장한 파일의 BOM 은 건너뛴다 (서명은 원래 바이트로 확인한다)
        var span = bytes.AsMemory();
        if (span.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) span = span[3..];
        using var doc = JsonDocument.Parse(span);
        var m = doc.RootElement;
        if (!AppBuild.TryParseVersion(Str(m, "version"), out var version)) return null;
        var sha = Str(m, "sha256");
        return new ReleaseInfo(version.ToString(3),
            Str(m, "file") is { Length: > 0 } f ? f : AssetName,
            sha.Length == 64 ? sha.ToLowerInvariant() : null,
            m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            DateTime.TryParse(Str(m, "published"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var p) ? p : null,
            Str(m, "page") is { Length: > 0 } page ? page : null,
            Str(m, "notes"), Str(m, "schema"), Str(m, "rid"));
    }

    // 매니페스트가 없는 릴리스: releases/latest → releases/tag/vX.Y.Z 로 넘어가는 주소에서 버전만 읽는다
    private async Task<ReleaseInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{ReleasesPage}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var final = res.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        var i = final.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0 || !AppBuild.TryParseVersion(Uri.UnescapeDataString(final[(i + 5)..]), out var version)) return null;
        if (Key != null) return await SignedOf(version.ToString(3), ct);
        return new ReleaseInfo(version.ToString(3), AssetName, null, 0, null, $"{ReleasesPage}/tag/v{version.ToString(3)}", "", "", "");
    }

    // GitHub API (사외망). 막힌 곳에서 오래 기다리지 않도록 짧게 끊는다
    private async Task<ReleaseInfo?> FromApiAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ApiTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await _http.SendAsync(req, limit.Token);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(limit.Token));
        var r = doc.RootElement;
        if (!AppBuild.TryParseVersion(Str(r, "tag_name"), out var version)) return null;
        if (Key != null) return await SignedOf(version.ToString(3), ct);
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            if (!Str(a, "name").Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var digest = Str(a, "digest");
            return new ReleaseInfo(version.ToString(3), AssetName,
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                DateTime.TryParse(Str(r, "published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var p) ? p : null,
                Str(r, "html_url"), Str(r, "body"), "", "");
        }
        return null;
    }

    // 서명을 확인하는 빌드: 버전만 알아낸 경우에도 그 릴리스의 서명된 매니페스트가 있어야 받는다
    private async Task<ReleaseInfo?> SignedOf(string version, CancellationToken ct) =>
        await ManifestAsync(ManifestLocation(version), ct)
        ?? throw new UpdateException($"v{version} 릴리스에 서명된 배포 정보({ManifestName})가 없습니다.", "정식 배포 워크플로로 만든 릴리스인지 확인하세요.");

    // 패키지를 target 으로 받는다 (진행률 0~100)
    public async Task DownloadAsync(ReleaseInfo r, string target, Action<int> progress, CancellationToken ct)
    {
        if (!IsGitHub && !IsHttp)
        {
            var path = Path.Combine(_source, r.File);
            if (!File.Exists(path))
                throw new UpdateException($"업데이트 폴더에 {r.File} 파일이 없습니다.", "GanttScheduler.json 과 같은 폴더에 배포 파일(zip)을 넣어 주세요.");
            await using var src = File.OpenRead(path);
            await CopyAsync(src, target, src.Length, progress, ct);
            return;
        }
        using var res = await _http.GetAsync(AssetUrl(r, r.File), HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        await CopyAsync(body, target, res.Content.Headers.ContentLength ?? r.Size, progress, ct);
    }

    // 매니페스트에 해시가 없을 때: 패키지 옆의 "<파일>.sha256" ("해시  파일명")
    public async Task<string?> FetchShaAsync(ReleaseInfo r, CancellationToken ct)
    {
        string? text;
        if (IsGitHub || IsHttp) text = await GetTextAsync(AssetUrl(r, r.File + ".sha256"), ct);
        else
        {
            var path = Path.Combine(_source, r.File + ".sha256");
            text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        var first = (text ?? "").Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 64 ? first.ToLowerInvariant() : null;
    }

    private string AssetUrl(ReleaseInfo r, string file) =>
        IsGitHub ? $"{ReleasesPage}/download/v{r.Version}/{file}" : $"{_source.TrimEnd('/')}/{file}";

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    // 주소면 받아 오고, 폴더 경로면 파일을 읽는다. 없으면(404) null
    private async Task<byte[]?> GetBytesAsync(string location, CancellationToken ct)
    {
        if (!IsGitHub && !IsHttp) return File.Exists(location) ? await File.ReadAllBytesAsync(location, ct) : null;
        using var res = await _http.GetAsync(location, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task CopyAsync(Stream src, string target, long total, Action<int> progress, CancellationToken ct)
    {
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buf = new byte[81920];
        long done = 0;
        var reported = -1;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            var pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
            if (pct != reported) progress(reported = pct);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
