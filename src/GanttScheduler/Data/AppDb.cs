using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GanttScheduler.Data;

public class AppDb : DbContext
{
    public const string Schema = "pm";

    // version: 상황판의 데이터 버전(싱글턴). 없으면(시험·admin 명령) 세지 않는다
    public AppDb(DbContextOptions<AppDb> options, GanttScheduler.Services.DataVersion? version = null) : base(options) => _version = version;

    private readonly GanttScheduler.Services.DataVersion? _version;

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserAddress> UserAddresses => Set<UserAddress>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<StageTemplate> StageTemplates => Set<StageTemplate>();
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();
    public DbSet<ContainerType> ContainerTypes => Set<ContainerType>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectEquipment> ProjectEquipments => Set<ProjectEquipment>();
    public DbSet<ProjectContainer> ProjectContainers => Set<ProjectContainer>();
    public DbSet<Stage> Stages => Set<Stage>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<TemplateCheckItem> TemplateCheckItems => Set<TemplateCheckItem>();
    public DbSet<CheckItem> CheckItems => Set<CheckItem>();
    public DbSet<WorkLink> WorkLinks => Set<WorkLink>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<StageLink> StageLinks => Set<StageLink>();
    public DbSet<ProjectVisit> ProjectVisits => Set<ProjectVisit>();
    public DbSet<ProjectWatch> ProjectWatches => Set<ProjectWatch>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ActivityMention> ActivityMentions => Set<ActivityMention>();
    public DbSet<Baseline> Baselines => Set<Baseline>();
    public DbSet<BaselineItem> BaselineItems => Set<BaselineItem>();
    public DbSet<DeletedProjectLog> DeletedProjectLogs => Set<DeletedProjectLog>();
    public DbSet<TemplateWork> TemplateWorks => Set<TemplateWork>();
    public DbSet<ProjectSnapshot> ProjectSnapshots => Set<ProjectSnapshot>();
    public DbSet<DelayReason> DelayReasons => Set<DelayReason>();
    public DbSet<MasterChange> MasterChanges => Set<MasterChange>();

    // 휴지통에 있지 않은 과제와 그 활동. 지운 과제는 모든 조회에서 이것으로 뺀다 — 전역 필터(HasQueryFilter)는 섀도 빌드의 가짜 EF 에 없어
    // 조회마다 명시적으로 고른다. 휴지통·영구 삭제·이전 작업만 Projects 를 그대로 쓴다
    public IQueryable<Project> LiveProjects => Projects.Where(p => p.DeletedAt == null);
    public IQueryable<Activity> LiveActivities => Activities.Where(a => a.Project!.DeletedAt == null);

    // 저장 직전·직후에 끼어드는 일 — 요청 범위의 활동 기록(Services/Notifications.cs 의 ActivityLog)이 등록해,
    // 활동과 그 알림을 같은 SaveChanges 로 저장한다. 어느 서비스가 저장하든 빠지지 않게 저장 한 곳에서 부른다
    private readonly List<ISaveHook> _hooks = new();

    public void AddSaveHook(ISaveHook hook)
    {
        if (!_hooks.Contains(hook)) _hooks.Add(hook);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var h in _hooks) await h.BeforeSaveAsync(cancellationToken);
        // 과제 그래프가 바뀌는 저장인지 저장 전에 본다(저장하면 상태가 Unchanged 로 돌아간다)
        var touched = _version != null && TouchesProjectGraph();
        var n = await base.SaveChangesAsync(cancellationToken);
        // 저장 뒤에야 Id 가 정해지는 것(새 과제의 활동)은 한 번 더 저장한다
        foreach (var h in _hooks)
            if (await h.AfterSaveAsync(cancellationToken)) n += await base.SaveChangesAsync(cancellationToken);
        // 저장이 성공한 뒤에만 올린다 — 실패한 저장(409 등)에 상황판이 괜히 다시 받지 않게
        if (touched) _version!.Bump();
        return n;
    }

    // 과제·단계·작업·체크·의존·과제 간 연결·첨부·활동 중 하나라도 추가·수정·삭제되는가.
    // 마지막 접속 시각(UserAddress)·방문·알림 읽음 같은 개인 기록은 상황판에 보이지 않으므로 세지 않는다
    private bool TouchesProjectGraph()
    {
        ChangeTracker.DetectChanges();
        foreach (var e in ChangeTracker.Entries())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && GanttScheduler.Services.DataVersion.Watches(e.Entity))
                return true;
        return false;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // 시각은 모두 UTC 로 저장하고, 읽을 때 Kind 를 UTC 로 지정한다 (JSON 에 'Z' 가 붙도록)
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    // 문자열 열 길이는 Domain/Limits 의 상수를 쓴다 — 서비스 검사·화면 maxlength 와 같은 값이어야 SQL Server 에서만 나는 길이 초과(500)가 없다
    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQL Server 에서는 기존 DB 안에 별도 스키마로 둔다. SQLite 는 스키마 개념이 없다
        if (Database.IsSqlServer()) b.HasDefaultSchema(Schema);

        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Name).HasMaxLength(Limits.UserName).IsRequired();
            e.Property(x => x.Email).HasMaxLength(Limits.Email);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.OrgUnit).WithMany().HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrgUnit>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.OrgName).IsRequired();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserAddress>(e =>
        {
            e.Property(x => x.Ip).HasMaxLength(Limits.Ip).IsRequired();
            e.HasIndex(x => x.Ip).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Addresses).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.LocationName).IsRequired();
            e.Property(x => x.Country).HasMaxLength(Limits.Country);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.CategoryName).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<StageTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.StageName).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Category).WithMany(c => c.StageTemplates).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TemplateCheckItem>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(Limits.CheckText).IsRequired();
            e.HasOne(x => x.StageTemplate).WithMany(t => t.CheckItems).HasForeignKey(x => x.StageTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EquipmentType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.EquipmentName).IsRequired();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ContainerType>(e => e.Property(x => x.Name).HasMaxLength(Limits.ContainerName).IsRequired());
        b.Entity<Vendor>(e => e.Property(x => x.Name).HasMaxLength(Limits.VendorName).IsRequired());

        b.Entity<Holiday>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.HolidayName).IsRequired();
            e.HasIndex(x => x.Date).IsUnique();
        });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Limits.ProjectTitle).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Vendor).HasMaxLength(Limits.VendorName);
            e.Property(x => x.Description).HasMaxLength(Limits.Description);
            // 버전은 동시 수정 감지에 쓴다 — 읽은 뒤 다른 사람이 먼저 저장했으면 저장이 실패한다
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Corporation).WithMany().HasForeignKey(x => x.CorporationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
            // 휴지통: 지운 사람(사용자 FK 는 Restrict), 사유. 보관 기간 정리는 DeletedAt 으로 고른다
            e.HasOne(x => x.DeletedBy).WithMany().HasForeignKey(x => x.DeletedById).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DeleteReason).HasMaxLength(Limits.Reason);
            e.HasIndex(x => x.DeletedAt);
        });

        b.Entity<ProjectEquipment>(e =>
        {
            e.Property(x => x.Note).HasMaxLength(Limits.ItemNote);
            e.HasOne(x => x.Project).WithMany(p => p.Equipments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.EquipmentType).WithMany().HasForeignKey(x => x.EquipmentTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectContainer>(e =>
        {
            e.Property(x => x.Note).HasMaxLength(Limits.ItemNote);
            e.HasOne(x => x.Project).WithMany(p => p.Containers).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ContainerType).WithMany().HasForeignKey(x => x.ContainerTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Stage>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Limits.StageName).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Project).WithMany(p => p.Stages).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckItem>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(Limits.CheckText).IsRequired();
            e.HasOne(x => x.Stage).WithMany(s => s.Checks).HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DoneBy).WithMany().HasForeignKey(x => x.DoneById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Limits.WorkTitle).IsRequired();
            e.Property(x => x.Note).HasMaxLength(Limits.WorkNote);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Stage).WithMany(s => s.Works).HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CoOwner).WithMany().HasForeignKey(x => x.CoOwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkLink>(e =>
        {
            // 작업 쪽 FK 는 Restrict — SQL Server 가 한 테이블로 가는 cascade 경로를 둘 이상 허용하지 않는다.
            // 작업·단계를 지울 때는 서비스에서 관련 의존 관계를 먼저 지운다
            e.HasOne(x => x.Project).WithMany(p => p.Links).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Predecessor).WithMany().HasForeignKey(x => x.PredecessorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Successor).WithMany().HasForeignKey(x => x.SuccessorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PredecessorId, x.SuccessorId }).IsUnique();
        });

        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(Limits.FileName).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(Limits.StoredName).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(Limits.ContentType).IsRequired();
            e.HasOne(x => x.Project).WithMany(p => p.Attachments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Activity>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Text).HasMaxLength(Limits.ActivityText).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(Limits.Reason);
            // 댓글을 고치면 첫 원문(최대 1000자)을 OldValue 에 남긴다
            e.Property(x => x.OldValue).HasMaxLength(Limits.ActivityOldValue);
            e.Property(x => x.NewValue).HasMaxLength(Limits.ActivityValue);
            e.HasOne(x => x.Project).WithMany(p => p.Activities).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.At);
            // 과제 하나의 활동을 최근 순으로 나눠 읽는다(상세 '더 보기'·변경 이력의 과제 조건). ProjectId 만의 FK 인덱스를 대신한다
            e.HasIndex(x => new { x.ProjectId, x.At });
        });

        // ── 과제 간 단계 의존 ──
        b.Entity<StageLink>(e =>
        {
            // 두 FK 모두 Restrict — 같은 단계 테이블로 가는 cascade 경로가 둘이 되면 SQL Server 가 거절한다.
            // 단계·과제를 지울 때는 서비스(ProjectService.RemoveStageLinksAsync)가 관련 연결을 먼저 지운다
            e.HasOne(x => x.PredecessorStage).WithMany().HasForeignKey(x => x.PredecessorStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SuccessorStage).WithMany().HasForeignKey(x => x.SuccessorStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PredecessorStageId, x.SuccessorStageId }).IsUnique();
            e.HasIndex(x => x.SuccessorStageId);
        });

        // ── 협업·새 소식·알림 ──
        // 사용자 FK 는 모두 Restrict(사용자는 지우지 않고 사용 중지한다). 과제·활동 쪽만 cascade 로 함께 지운다
        b.Entity<ProjectVisit>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ProjectId }).IsUnique();
        });

        b.Entity<ProjectWatch>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ProjectId }).IsUnique();
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            // 과제 → 활동 → 알림 한 길로만 cascade 한다(과제로 바로 잇는 FK 를 두면 SQL Server 가 두 경로를 거절한다)
            e.HasOne(x => x.Activity).WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Key).IsUnique();
            // 읽지 않은 수(헤더 종, 1분마다)와 최근 순 목록
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<ActivityMention>(e =>
        {
            e.HasOne(x => x.Activity).WithMany(a => a.Mentions).HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ActivityId, x.UserId }).IsUnique();
        });

        // ── 기준선 차수 이력·휴지통 정리 기록 ──
        b.Entity<Baseline>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(Limits.Reason);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ProjectId, x.No }).IsUnique();
        });

        b.Entity<BaselineItem>(e =>
        {
            // 단계·작업 Id 는 FK 없는 참조 — 단계·작업을 지워도 그 차수의 기록은 남는다(과제 → 차수 → 항목 한 길로만 cascade)
            e.HasOne(x => x.Baseline).WithMany(x => x.Items).HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeletedProjectLog>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Limits.ProjectTitle).IsRequired();
            e.Property(x => x.DeletedBy).HasMaxLength(Limits.UserName);
            e.Property(x => x.Reason).HasMaxLength(Limits.Reason);
            e.Property(x => x.PurgedBy).HasMaxLength(Limits.UserName).IsRequired();
            e.HasIndex(x => x.PurgedAt);
        });

        // ── 유형 템플릿 작업 ──
        b.Entity<TemplateWork>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Limits.WorkTitle).IsRequired();
            e.HasOne(x => x.StageTemplate).WithMany(t => t.Works).HasForeignKey(x => x.StageTemplateId).OnDelete(DeleteBehavior.Cascade);
            // 같은 표를 가리키는 선행은 cascade 를 걸 수 없다(SQL Server 다중 경로). 지울 때는 서비스가 먼저 끊는다.
            // NoAction: 단계 템플릿 삭제로 함께 지워지는 행끼리의 참조는 문장 끝에 검사되므로 막히지 않는다
            e.HasOne(x => x.PredecessorTemplateWork).WithMany().HasForeignKey(x => x.PredecessorTemplateWorkId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── 분석: 일별 진척 스냅샷·지연 사유 분류 ──
        b.Entity<ProjectSnapshot>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CurrentStage).HasMaxLength(Limits.StageName);
            // 과제 → 스냅샷 한 길로만 cascade (영구 삭제하면 함께 지운다). 유형·법인·담당·조직은 그날 값을 적은 FK 없는 참조
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);
        });

        b.Entity<DelayReason>(e => e.Property(x => x.Name).HasMaxLength(Limits.DelayReasonName).IsRequired());

        // ── 과제 종료일 (완료·중단 연도 조건) ──
        b.Entity<Project>(e => e.HasIndex(x => x.ClosedOn));

        // ── 관리 변경 이력 ──
        b.Entity<MasterChange>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(Limits.MasterKind).IsRequired();
            e.Property(x => x.Text).HasMaxLength(Limits.ActivityText).IsRequired();
            e.Property(x => x.OldValue).HasMaxLength(Limits.ActivityOldValue);
            e.Property(x => x.NewValue).HasMaxLength(Limits.ActivityOldValue);
            // 사용자는 지우지 않고(병합만 예외 — 병합이 먼저 옮긴다) Restrict
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            // 최근 순 목록·기간 조건
            e.HasIndex(x => x.At);
        });
    }
}

// 저장 직전·직후에 할 일 (AppDb.SaveChangesAsync 가 부른다)
public interface ISaveHook
{
    Task BeforeSaveAsync(CancellationToken ct);
    // true 면 바뀐 것이 있어 한 번 더 저장한다
    Task<bool> AfterSaveAsync(CancellationToken ct);
}

public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
}
