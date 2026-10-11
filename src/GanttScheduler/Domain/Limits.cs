namespace GanttScheduler.Domain;

// 입력 길이 상한. DB 열 길이(AppDb 의 HasMaxLength)·서비스 검사·화면 maxlength 가 이 값 하나를 쓴다 —
// 따로 적으면 화면은 받는데 DB 가 거절해(SQL Server 만 길이를 검사한다) 500 이 난다.
// 값을 바꾸면 열 길이가 바뀌므로 SQL Server 마이그레이션이 필요하다
public static class Limits
{
    // 사용자·조직
    public const int UserName = 50;
    public const int OrgName = 50;
    public const int Ip = 45;
    public const int Email = 200;            // 메일 요약 받는 주소

    // 기준 정보
    public const int LocationName = 100;
    public const int Country = 50;
    public const int CategoryName = 50;
    public const int StageName = 50;           // 단계 템플릿과 과제 단계
    public const int CheckText = 200;          // 체크 항목 (템플릿과 과제)
    public const int EquipmentName = 50;
    public const int ContainerName = 50;
    public const int VendorName = 100;         // 협력사 기준 정보와 과제의 협력사
    public const int HolidayName = 50;

    // 과제·작업
    public const int ProjectTitle = 200;
    public const int Description = 2000;
    public const int ItemNote = 200;           // 기기·용기 내역 비고
    public const int WorkTitle = 200;
    public const int WorkNote = 1000;

    // 활동·첨부
    public const int ActivityText = 1000;      // 댓글·이력 문구
    public const int Reason = 500;
    public const int ActivityValue = 100;      // 이전·새 값
    public const int ActivityOldValue = 1000;  // 이전 값 — 고친 댓글의 첫 원문을 담는다(ActivityText 와 같은 길이)
    public const int FileName = 260;           // 첨부 원래 이름 (올릴 때는 FileNameInput 까지)
    public const int FileNameInput = 200;
    public const int StoredName = 100;
    public const int ContentType = 100;
}
