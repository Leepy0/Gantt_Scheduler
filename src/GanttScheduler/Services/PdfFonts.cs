namespace GanttScheduler.Services;

// PDF 에 쓸 한글 TTF 글꼴 찾기. 내보내기(PdfExporter)와 서버 상태 점검(OpsChecks)이 같은 규칙을 쓴다 —
// 글꼴이 없으면 예전에는 내보내기를 누를 때에야 드러났다. PdfExporter 는 섀도 빌드에서 빠지므로 탐색은 이 파일에 둔다
public static class PdfFonts
{
    // 설정(Pdf:Font / Pdf:FontBold) → Windows 맑은 고딕 → Linux 나눔고딕 순서. 없으면 null
    public static (string? Regular, string? Bold) Find(IConfiguration cfg)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var regular = FirstExisting(cfg["Pdf:Font"],
            string.IsNullOrEmpty(windows) ? null : Path.Combine(windows, "malgun.ttf"),
            "/usr/share/fonts/truetype/nanum/NanumGothic.ttf",
            "/usr/share/fonts/truetype/nanum/NanumBarunGothic.ttf");
        var bold = FirstExisting(cfg["Pdf:FontBold"],
            string.IsNullOrEmpty(windows) ? null : Path.Combine(windows, "malgunbd.ttf"),
            "/usr/share/fonts/truetype/nanum/NanumGothicBold.ttf",
            "/usr/share/fonts/truetype/nanum/NanumBarunGothicBold.ttf");
        return (regular, bold);
    }

    private static string? FirstExisting(params string?[] paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
}
