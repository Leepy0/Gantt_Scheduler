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
}

// 사용자 — 로그인 없이 접속 IP 로 식별. 주소가 없는 사용자는 담당자로만 등록된 상태
public class AppUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Dept { get; set; }
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

    public List<Stage> Stages { get; set; } = new();
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
    public List<WorkItem> Works { get; set; } = new();
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
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
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
