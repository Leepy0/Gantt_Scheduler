using System.Net;
using System.Reflection;
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
public record ReleaseInfo(string Version, string File, string? Sha256, long Size, DateTime? Published, string? Page, string? Notes, string? Schema, string? Rid);

public record StagedUpdate(string Version, string Dir, string Sha256, string? Schema, string? Notes, DateTime At, string Origin, string By);

public record PreviousVersion(string Version, string Schema, string Dir, DateTime At);

public record UpdateLog(DateTime At, string Kind, string From, string To, string By, bool Ok, string? Error);

public record PendingSwap(string Kind, string From, string To, string FromSchema, string By, DateTime At);

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
}

// ── 새 버전을 어디서 찾을지 (Update:Source) ──────────────────────────────
//   github:<owner>/<repo>   GitHub 릴리스. 사내망에서 api.github.com 은 막혀 있어 github.com 주소를 먼저 쓴다
//   https://…               그 주소 아래의 GanttScheduler.json 과 패키지 (사내 서버)
//   폴더 경로               그 폴더의 GanttScheduler.json 과 패키지 (공유 폴더, 인터넷이 없는 서버)
public class UpdateSource
{
    public const string ManifestName = "GanttScheduler.json";
    public const string AssetName = "GanttScheduler-win-x64.zip";

    private readonly string _source;
    private readonly HttpClient _http;

    public UpdateSource(string? source, AppBuild build)
    {
        _source = (source ?? "").Trim();
        // 시스템 프록시를 쓰고, 사내 프록시가 Windows 로그인 인증을 요구해도 통과하게 한다
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // 시간 제한은 호출마다 토큰으로
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GanttScheduler/{build.VersionText}");
    }

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
        if (!IsGitHub) return await FromManifestAsync(ct);

        Exception? first = null;
        foreach (var source in new Func<CancellationToken, Task<ReleaseInfo?>>[] { FromManifestAsync, FromRedirectAsync, FromApiAsync })
        {
            try
            {
                var r = await source(ct);
                if (r != null) return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { first ??= ex; }   // 다음 방법으로
        }
        if (first != null) throw first;
        return null;
    }

    private async Task<ReleaseInfo?> FromManifestAsync(CancellationToken ct)
    {
        string? json;
        if (IsGitHub) json = await GetTextAsync($"{ReleasesPage}/latest/download/{ManifestName}", ct);
        else if (IsHttp) json = await GetTextAsync($"{_source.TrimEnd('/')}/{ManifestName}", ct);
        else
        {
            var path = Path.Combine(_source, ManifestName);
            if (!Directory.Exists(_source))
                throw new UpdateException($"업데이트 폴더를 열 수 없습니다: {_source}", "폴더 경로와 서버 계정의 읽기 권한을 확인하세요.");
            json = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        return json == null ? null : ParseManifest(json);
    }

    public static ReleaseInfo? ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
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
        return new ReleaseInfo(version.ToString(3), AssetName, null, 0, null, $"{ReleasesPage}/tag/v{version.ToString(3)}", "", "", "");
    }

    // GitHub API (사외망)
    private async Task<ReleaseInfo?> FromApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        if (!AppBuild.TryParseVersion(Str(r, "tag_name"), out var version)) return null;
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
