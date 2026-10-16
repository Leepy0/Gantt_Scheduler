using System.Text.RegularExpressions;

namespace GanttScheduler.Domain;

// 댓글(진행 내용) 본문 안의 이미지 참조. 이미지는 첨부(Attachment)로 저장하고 본문에는 '[이미지:첨부Id]' 글자만 둔다 —
// 본문은 그대로 글자라서 검색·알림·메일·보고서에 그대로 실리고, 화면(App.richText)만 그 자리에 그림을 그린다
public static class CommentText
{
    public static readonly Regex ImageTag = new(@"\[이미지:(\d+)\]", RegexOptions.CultureInvariant);

    public static string Image(int attachmentId) => $"[이미지:{attachmentId}]";

    // 본문이 가리키는 첨부 Id (중복 제거, 나온 차례)
    public static List<int> ImageIds(string? text)
    {
        var ids = new List<int>();
        if (string.IsNullOrEmpty(text)) return ids;
        foreach (Match m in ImageTag.Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    // 그림을 그릴 수 없는 곳(알림 한 줄·메일·보고서)에 쓰는 글: 참조를 '(이미지)'로 바꾼다
    public static string Plain(string? text) => string.IsNullOrEmpty(text) ? "" : ImageTag.Replace(text, "(이미지)");
}
