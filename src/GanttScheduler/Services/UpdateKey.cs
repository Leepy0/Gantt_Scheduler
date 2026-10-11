using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GanttScheduler.Services;

// 배포 파일의 출처 확인. 릴리스 워크플로가 매니페스트(GanttScheduler.json)에 분리 서명(GanttScheduler.json.sig,
// ECDSA P-256 · SHA-256 · DER)을 붙이고, 서버는 빌드에 들어 있는 공개키(update-key.pub)로 확인한다.
// SHA-256 대조는 같은 곳에서 받은 값끼리 비교하므로 손상만 잡는다. 릴리스를 올린 계정·토큰이 털려도 남의 파일이
// 서버에서 실행되지 않게 하려면 개인키(시크릿 RELEASE_SIGNING_KEY)로 만든 서명을 따로 확인해야 한다.
// 공개키 파일에는 키를 여러 개 둘 수 있고 하나라도 맞으면 받는다 — 키를 바꿀 때 새 키를 먼저 넣은 버전을 옛 키로 서명해 내보내기 위해
public sealed class UpdateKey
{
    public const string ResourceName = "update-key.pub";
    public const string SignatureSuffix = ".sig";

    private readonly List<ECDsa> _keys = new();
    private readonly object _gate = new();   // ECDsa 인스턴스는 여러 요청이 동시에 쓰면 안전하지 않다

    // 화면에 보여 줄 공개키 지문 (공개키 SPKI 의 SHA-256 앞 16자리, 여러 개면 쉼표로)
    public string Id { get; }

    public UpdateKey(string pem)
    {
        foreach (Match m in Regex.Matches(pem, "-----BEGIN PUBLIC KEY-----.+?-----END PUBLIC KEY-----", RegexOptions.Singleline))
        {
            var key = ECDsa.Create();
            key.ImportFromPem(m.Value);
            _keys.Add(key);
        }
        if (_keys.Count == 0) throw new ArgumentException("공개키(PEM)가 없습니다.", nameof(pem));
        Id = string.Join(", ", _keys.Select(k => Convert.ToHexString(SHA256.HashData(k.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant()));
    }

    // 빌드에 넣은 공개키. 저장소에 update-key.pub 가 없으면(키를 아직 만들지 않음) null — 서명 확인을 하지 않는다
    public static UpdateKey? FromAssembly()
    {
        using var stream = typeof(UpdateKey).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pem = reader.ReadToEnd();
        return pem.Contains("PUBLIC KEY", StringComparison.Ordinal) ? new UpdateKey(pem) : null;
    }

    public bool Verify(byte[] data, byte[]? signature)
    {
        if (signature == null || signature.Length == 0) return false;
        lock (_gate)
        {
            if (TryVerify(data, signature)) return true;
            // 서명을 텍스트(base64)로 옮겨 붙인 경우도 받아 준다
            try
            {
                var text = Encoding.ASCII.GetString(signature).Trim();
                return text.Length > 0 && TryVerify(data, Convert.FromBase64String(text));
            }
            catch (FormatException) { return false; }
        }
    }

    private bool TryVerify(byte[] data, byte[] signature)
    {
        foreach (var key in _keys)
        {
            try
            {
                if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (CryptographicException) { /* 형식이 맞지 않는 서명 — 다음 키 */ }
        }
        return false;
    }
}
