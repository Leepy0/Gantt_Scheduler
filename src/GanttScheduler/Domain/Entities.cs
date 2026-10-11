namespace GanttScheduler.Domain;

// 과제 유형의 성격 — 등록 화면에서 어떤 내역(기기/용기)을 입력받을지 결정
public enum CategoryKind { General = 0, Automation = 1, Container = 2 }

// 과제 상태 — Done 은 모든 단계 완료 시 자동 전환
public enum ProjectState { Active = 0, Hold = 1, Dropped = 2, Done = 3 }

// 단계 성격 — 검수/입고는 입고 추천일 계산에 사용
public enum StageKind { Normal = 0, Inspection = 1, Inbound = 2 }

public enum ActivityType
{
    Comment = 0,
    Created = 1,
    Edited = 2,
    TargetChanged = 3,
    StageCompleted = 4,
    StageReopened = 5,
    StageAdded = 6,
    StageRemoved = 7,
    StateChanged = 8,
    WorkAdded = 9,
    WorkEdited = 10,
    WorkPlanChanged = 11,
    WorkRemoved = 12,
    BaselineSet = 13,
    AttachmentAdded = 14,
    AttachmentRemoved = 15,
    Cloned = 16,
    CheckChanged = 17,
    LinkChanged = 18,
}

// 조직: 1 그룹 → 2 파트. 사용자는 파트(없으면 그룹)에 속한다
public class OrgUnit
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public OrgUnit? Parent { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

// 사용자 — 로그인 없이 접속 IP 로 식별. 주소가 없는 사용자는 담당자로만 등록된 상태
public class AppUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? OrgUnitId { get; set; }
    public OrgUnit? OrgUnit { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public List<UserAddress> Addresses { get; set; } = new();
}

// 사용자 접속 IP — 한 사람이 여러 PC/IP 를 쓸 수 있다
public class UserAddress
{
    public int Id { get; set; }
    public string Ip { get; set; } = "";
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public DateTime LastSeenAt { get; set; }
}

// 위치 계층: 1 법인 → 2 제품군 → 3 라인 → 4 층 (단일 트리 테이블)
public class Location
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public Location? Parent { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }      // 법인(1레벨)만 사용
    public int OceanLeadDays { get; set; }    // 법인(1레벨)만 사용 — 해상 운송 소요일
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public CategoryKind Kind { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<StageTemplate> StageTemplates { get; set; } = new();
}

// 유형별 기본 단계 — 과제 생성 시점에 Stage 로 복사(스냅샷)된다
public class StageTemplate
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public string Name { get; set; } = "";
    public StageKind Kind { get; set; }
    public int SortOrder { get; set; }
    public List<TemplateCheckItem> CheckItems { get; set; } = new();
    // 기본 기간(근무일): 앞 단계 목표일(첫 단계는 착수일)부터 이 단계 목표일까지. 등록 화면의 목표일 초안에만 쓰고
    // 과제에는 복사하지 않는다(초안은 제안일 뿐 목표일은 사람이 정한다 — 규칙 1). 없으면 초안을 비운다
    public int? DefaultWorkDays { get; set; }
}

// 단계 템플릿의 기본 체크리스트 — 과제 생성 시 CheckItem 으로 복사된다
public class TemplateCheckItem
{
    public int Id { get; set; }
    public int StageTemplateId { get; set; }
    public StageTemplate? StageTemplate { get; set; }
    public string Text { get; set; } = "";
    public int SortOrder { get; set; }
}

// 물류 기기 종류: 대분류(AGV, AMR …) → 하위 타입(ANT, Belt …)
public class EquipmentType
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public EquipmentType? Parent { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ContainerType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Vendor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Holiday
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

// 과제
public class Project
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public int CorporationId { get; set; }          // 1레벨 Location
    public Location? Corporation { get; set; }
    public int LocationId { get; set; }             // 선택한 가장 깊은 Location
    public Location? Location { get; set; }
    public int OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public ProjectState State { get; set; }
    public DateOnly StartDate { get; set; }
    public string? Vendor { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }                // 동시 수정 감지용 — 과제 정보·상태가 바뀔 때마다 증가
    public DateTime? BaselineAt { get; set; }       // 기준선을 확정한 시각 (없으면 미확정)

    public List<Stage> Stages { get; set; } = new();
    public List<WorkLink> Links { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
    public List<ProjectEquipment> Equipments { get; set; } = new();
    public List<ProjectContainer> Containers { get; set; } = new();
    public List<Activity> Activities { get; set; } = new();
}

public class ProjectEquipment
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int EquipmentTypeId { get; set; }
    public EquipmentType? EquipmentType { get; set; }
    public int Quantity { get; set; }
    public string? Note { get; set; }
}

public class ProjectContainer
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int ContainerTypeId { get; set; }
    public ContainerType? ContainerType { get; set; }
    public int Quantity { get; set; }
    public string? Note { get; set; }
}

// 단계 — 목표일/완료일을 가지는 마일스톤. 세부 일정이 필요하면 작업(WorkItem)을 붙인다
public class Stage
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public StageKind Kind { get; set; }
    public int SortOrder { get; set; }
    public DateOnly? TargetDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public DateOnly? BaselineDate { get; set; }     // 기준선 확정 당시의 목표일
    public int Version { get; set; }
    public List<WorkItem> Works { get; set; } = new();
    public List<CheckItem> Checks { get; set; } = new();
}

// 단계 완료 조건 — 모두 체크해야 단계를 완료할 수 있다
public class CheckItem
{
    public int Id { get; set; }
    public int StageId { get; set; }
    public Stage? Stage { get; set; }
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
    public int? DoneById { get; set; }
    public AppUser? DoneBy { get; set; }
    public DateTime? DoneAt { get; set; }
    public int SortOrder { get; set; }
}

// 작업 — 단계 아래 세부 일정. 상태는 저장하지 않고 실적 일자에서 계산한다
public class WorkItem
{
    public int Id { get; set; }
    public int StageId { get; set; }
    public Stage? Stage { get; set; }
    public string Title { get; set; } = "";
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public int? CoOwnerId { get; set; }
    public AppUser? CoOwner { get; set; }
    public int Progress { get; set; }               // 0~100, 5 단위
    public DateOnly PlanStart { get; set; }
    public DateOnly PlanEnd { get; set; }
    public DateOnly? ActualStart { get; set; }
    public DateOnly? ActualEnd { get; set; }
    public DateOnly? BaselineStart { get; set; }    // 기준선 확정 당시의 목표 일정
    public DateOnly? BaselineEnd { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}

// 작업 의존 관계. 유형(LinkType)에 따라 후행 작업이 선행 작업의 시작·종료 + LagDays(근무일) 이후에 시작하거나 끝나야 한다.
// 판정은 Rules.EarliestStart 하나를 거친다 (규칙 11)
public class WorkLink
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int PredecessorId { get; set; }
    public WorkItem? Predecessor { get; set; }
    public int SuccessorId { get; set; }
    public WorkItem? Successor { get; set; }
    public int LagDays { get; set; }                // 근무일 기준 간격
    public LinkType Type { get; set; }              // 0(FS)이 기본 — 유형이 생기기 전의 연결은 그대로 '끝나면 시작'
}

// 첨부 파일 — 실제 파일은 디스크(Storage:Path)에 StoredName 으로 저장한다
public class Attachment
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? StageId { get; set; }               // 느슨한 참조(FK 없음)
    public string FileName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public int UploadedById { get; set; }
    public AppUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

// 활동 — 댓글과 변경 이력을 한 스트림으로 저장
public class Activity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? StageId { get; set; }               // 느슨한 참조(FK 없음) — 삭제된 단계의 이력도 남긴다
    public int? WorkItemId { get; set; }            // 느슨한 참조(FK 없음)
    public ActivityType Type { get; set; }
    public string Text { get; set; } = "";
    public string? Reason { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public Guid? BatchId { get; set; }              // 연쇄 이동처럼 한 번에 일어난 변경 묶음
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public DateTime At { get; set; }
}

// ── 의존 표현 확장: 작업 의존 유형, 과제 간 단계 의존 ─────────────────────

// 작업 의존 유형. 숫자로 저장한다 — 유형 열을 더할 때 기존 연결이 0(FS)으로 읽히게(문자열이면 빈 값이 된다)
public enum LinkType
{
    FS = 0,     // 끝나면 시작: 후행은 선행 종료 다음 근무일 + 간격 이후에 시작
    SS = 1,     // 같이 시작: 후행은 선행 시작일 + 간격 이후에 시작
    FF = 2,     // 같이 끝: 후행은 선행 종료일 + 간격 이후에 끝
}

// 과제 간 단계 의존: 후행 단계의 목표일이 선행 단계(다른 과제)의 완료일(없으면 목표일) 다음 근무일 + LagDays 이후여야 한다.
// 다른 과제 담당자의 일정을 몰래 바꾸지 않도록 자동으로 밀지 않고 위반만 계산한다(규칙 11).
// 두 FK 는 Restrict 다 — 단계·과제를 지울 때 서비스가 먼저 지운다(한 테이블로 가는 cascade 경로가 둘이면 SQL Server 가 거절한다)
public class StageLink
{
    public int Id { get; set; }
    public int PredecessorStageId { get; set; }
    public Stage? PredecessorStage { get; set; }
    public int SuccessorStageId { get; set; }
    public Stage? SuccessorStage { get; set; }
    public int LagDays { get; set; }                // 근무일 기준 간격
    public DateTime CreatedAt { get; set; }
}
