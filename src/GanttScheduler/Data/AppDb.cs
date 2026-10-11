using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GanttScheduler.Data;

public class AppDb : DbContext
{
    public const string Schema = "pm";

    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

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

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // 시각은 모두 UTC 로 저장하고, 읽을 때 Kind 를 UTC 로 지정한다 (JSON 에 'Z' 가 붙도록)
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQL Server 에서는 기존 DB 안에 별도 스키마로 둔다. SQLite 는 스키마 개념이 없다
        if (Database.IsSqlServer()) b.HasDefaultSchema(Schema);

        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.OrgUnit).WithMany().HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrgUnit>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserAddress>(e =>
        {
            e.Property(x => x.Ip).HasMaxLength(45).IsRequired();
            e.HasIndex(x => x.Ip).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Addresses).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Country).HasMaxLength(50);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<StageTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Category).WithMany(c => c.StageTemplates).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TemplateCheckItem>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.StageTemplate).WithMany(t => t.CheckItems).HasForeignKey(x => x.StageTemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EquipmentType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ContainerType>(e => e.Property(x => x.Name).HasMaxLength(50).IsRequired());
        b.Entity<Vendor>(e => e.Property(x => x.Name).HasMaxLength(100).IsRequired());

        b.Entity<Holiday>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Date).IsUnique();
        });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Vendor).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(2000);
            // 버전은 동시 수정 감지에 쓴다 — 읽은 뒤 다른 사람이 먼저 저장했으면 저장이 실패한다
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Corporation).WithMany().HasForeignKey(x => x.CorporationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectEquipment>(e =>
        {
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne(x => x.Project).WithMany(p => p.Equipments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.EquipmentType).WithMany().HasForeignKey(x => x.EquipmentTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectContainer>(e =>
        {
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasOne(x => x.Project).WithMany(p => p.Containers).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ContainerType).WithMany().HasForeignKey(x => x.ContainerTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Stage>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Project).WithMany(p => p.Stages).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckItem>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Stage).WithMany(s => s.Checks).HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DoneBy).WithMany().HasForeignKey(x => x.DoneById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
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
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Project).WithMany(p => p.Attachments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Activity>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Text).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.OldValue).HasMaxLength(100);
            e.Property(x => x.NewValue).HasMaxLength(100);
            e.HasOne(x => x.Project).WithMany(p => p.Activities).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.At);
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
    }
}

public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
}
