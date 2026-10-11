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
    CommentChanged = 19,    // 댓글 수정·삭제 기록 (NewValue = 고친 댓글의 활동 Id)
    ProjectDeleted = 20,    // 휴지통으로 보냄 (영구 삭제 전까지 활동이 남는다)
    ProjectRestored = 21,   // 휴지통에서 되살림
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
    // 메일 요약을 받을 주소. 관리자만 입력한다 — IP 로 식별하므로 본인이 바꾸게 하면 남의 알림을 가로챌 수 있다
    public string? Email { get; set; }
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
    // 과제를 등록할 때 이 단계에 함께 만들 작업(템플릿 작업). 등록 시점에 복사한다 — 이후 템플릿을 고쳐도 기존 과제는 그대로(규칙 9)
    public List<TemplateWork> Works { get; set; } = new();
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
    // 휴지통: 지운 과제는 표시만 하고 모든 조회에서 뺀다(AppDb.LiveProjects). 보관 기간(Trash:KeepDays)이 지나면 영구 삭제한다
    public DateTime? DeletedAt { get; set; }
    public int? DeletedById { get; set; }
    public AppUser? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }

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
    // 댓글만: 작성자가 고친 시각(첫 원문은 OldValue 에 남긴다)과 지운 시각(본문은 자리 표시로 바꾸고 행은 남긴다)
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public List<ActivityMention> Mentions { get; set; } = new();
    // 일정 변경 사유의 분류(DelayReason.Id, 선택). FK 없는 느슨한 참조 — 분류를 사용 중지해도 옛 기록은 그대로 남는다
    public int? ReasonCode { get; set; }
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

// ── 협업·새 소식·알림 ─────────────────────────────────────────────────────

// 사용자가 과제 상세를 마지막으로 연 시각. 그 뒤에 다른 사람이 남긴 활동이 '새 활동'이다(Rules.NewSince)
public class ProjectVisit
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public DateTime LastSeenAt { get; set; }
}

// 관심 과제 — 담당이 아니어도 내 할 일의 '새 소식'과 알림을 받는다
public class ProjectWatch
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public DateTime CreatedAt { get; set; }
}

// 화면 내 알림. 활동을 저장할 때 같은 SaveChanges 로 만든다(ActivityLog). 한 저장에서 한 사람에게는 과제마다 한 건 —
// 여러 활동이 겹치면 가장 직접적인 이유(Kind)와 대표 활동 하나를 남기고 '외 n건'으로 묶는다.
// ProjectId 는 느슨한 참조(FK 없음)다 — 과제를 지우면 활동 → 알림 cascade 로 함께 지워진다(과제로 가는 cascade 경로를 하나로 둔다)
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int? ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public int ProjectId { get; set; }
    public string Kind { get; set; } = "";          // mention·work·project·watch (NotifyKinds)
    public string Text { get; set; } = "";
    public string Key { get; set; } = "";           // 같은 사건을 두 번 알리지 않게 하는 열쇠(유니크)
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? EmailedAt { get; set; }        // 메일 요약에 실어 보낸 시각 — 다음 요약에 다시 싣지 않는다
}

// 댓글에서 알릴 사람으로 고른 사용자(멘션). 이름은 공백이 있을 수 있어 본문을 파싱하지 않고 고른 Id 를 따로 남긴다
public class ActivityMention
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
}

// ── 휴지통·기준선 차수 이력 ───────────────────────────────────────────────

// 기준선 한 차수. 확정할 때마다 새 차수를 더한다(덮어쓰지 않는다). Stage.BaselineDate·WorkItem.Baseline*·Project.BaselineAt 은
// '최신 차수'의 사본으로 그대로 둔다 — 기존 화면·내보내기·리드타임이 그 열을 읽는다
public class Baseline
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int No { get; set; }                     // 1부터 과제마다 차례로
    public DateTime At { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string? Reason { get; set; }
    public List<BaselineItem> Items { get; set; } = new();
}

// 차수에 고정한 단계 목표일(StageId, End) 또는 작업 목표 일정(WorkItemId, Start~End).
// 단계·작업은 느슨한 참조(FK 없음)다 — 나중에 지워도 그때의 기준선 기록은 남는다
public class BaselineItem
{
    public int Id { get; set; }
    public int BaselineId { get; set; }
    public Baseline? Baseline { get; set; }
    public int? StageId { get; set; }
    public int? WorkItemId { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
}

// 영구 삭제한 과제의 기록 한 줄. 과제·활동은 함께 지워지므로 '누가 지웠고 언제 영구히 사라졌는지'는 여기와 파일 로그에만 남는다.
// 사용자 이름은 글자로 남긴다(FK 없음) — 기록이 다른 행에 묶이지 않게
public class DeletedProjectLog
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string? DeletedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? Reason { get; set; }
    public DateTime PurgedAt { get; set; }
    public string PurgedBy { get; set; } = "";      // 사람 이름, 또는 자동 정리('보관 기간 경과')
}

// 단계 템플릿 아래의 작업 템플릿. 과제 등록 때 단계마다 작업으로 만든다:
// 시작 = 단계 시작 기준일(첫 단계는 착수일, 이후는 앞 단계 목표일 다음 근무일)에서 OffsetWorkDays 근무일 뒤, 기간 DurationWorkDays 근무일.
// 선행은 같은 단계 템플릿 안의 작업만 가리킨다(과제에서는 '끝나면 시작' 연결이 된다)
public class TemplateWork
{
    public int Id { get; set; }
    public int StageTemplateId { get; set; }
    public StageTemplate? StageTemplate { get; set; }
    public string Title { get; set; } = "";
    public int OffsetWorkDays { get; set; }
    public int DurationWorkDays { get; set; } = 1;
    public int? PredecessorTemplateWorkId { get; set; }
    public TemplateWork? PredecessorTemplateWork { get; set; }
    public int SortOrder { get; set; }
}

// ── 분석: 일별 진척 스냅샷·지연 사유 분류 ─────────────────────────────────

// 하루 한 번(SnapshotWorker) 남기는 과제의 그날 모습. 상태·지연은 저장하지 않고 계산하므로(규칙 3) 과거 시점의 지연 건수·진행률은
// 여기에만 남는다. 값은 그날의 Rules/Views 계산 결과 그대로다. 서버가 꺼져 있던 날은 행이 없다(지어내지 않는다).
// 추세 화면·주간 보고 '전주 대비'·신규 지연/지연 해소에만 쓰고 지금 상태 표시에는 쓰지 않는다
public class ProjectSnapshot
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }              // 한국 날짜
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public ProjectState State { get; set; }
    public int Progress { get; set; }
    public int DelayDays { get; set; }
    public int RiskDays { get; set; }
    public bool AtRisk { get; set; }                // 지연 위험(Rules.AtRisk) — 위험 과제 수 추세
    public DateOnly? EndTarget { get; set; }
    public int? SlipDays { get; set; }
    public string? CurrentStage { get; set; }
    // 묶어 보기 조건 — 그날의 값(나중에 유형·법인·담당이 바뀌어도 그날 기준으로 센다). FK 없는 참조
    public int CategoryId { get; set; }
    public int CorporationId { get; set; }
    public int OwnerId { get; set; }
    public int? OrgUnitId { get; set; }             // 담당자의 그날 소속
}

// 지연(일정 변경) 사유 분류 — 기준 정보. 다른 기준 정보처럼 지우지 않고 사용 중지한다(옛 활동의 ReasonCode 가 가리킨다)
public class DelayReason
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

// ── 관리 변경 이력 ────────────────────────────────────────────────────────

// 기준 정보·사용자·설정의 변경 한 줄. 공휴일·해상 운송일·유형 템플릿·관리자 지정은 일정 계산과 권한을 바꾸는데
// 과제 활동(Activity)은 과제에 묶여 있어 담을 곳이 없다. 이력은 추가만 한다(고치거나 지우지 않는다).
// UserId 가 없으면 서버가 스스로 한 일(보존 기간 정리 등)이다. EntityId 는 느슨한 참조 — 지운 항목의 기록도 남는다
public class MasterChange
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    public string Kind { get; set; } = "";          // 엔티티 종류(MasterLog.Kinds — location·holiday·user·ip·update …)
    public int? EntityId { get; set; }
    public string Text { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
