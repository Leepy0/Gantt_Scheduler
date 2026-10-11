// 로컬(오프라인) 검증 전용 — 운영 코드가 아니다.
// NuGet 에 접근할 수 없는 환경에서 앱을 빌드·실행해 보기 위한 최소한의 가짜 EF Core.
// 데이터는 프로세스 메모리에만 있고, 쿼리는 LINQ to Objects 로 돈다. 실제 동작 검증은 CI(진짜 EF) 가 한다.
#nullable enable
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Microsoft.EntityFrameworkCore
{
    public enum DeleteBehavior { Cascade, Restrict, NoAction, SetNull }

    public class DbUpdateException : Exception
    {
        public DbUpdateException(string message) : base(message) { }
    }

    public class DbUpdateConcurrencyException : DbUpdateException
    {
        public DbUpdateConcurrencyException(string message) : base(message) { }
    }

    public class DbContextOptions { }
    public class DbContextOptions<T> : DbContextOptions { }

    public class SqlServerOptions
    {
        public SqlServerOptions MigrationsHistoryTable(string table, string? schema = null) => this;
        public SqlServerOptions EnableRetryOnFailure() => this;
        public SqlServerOptions EnableRetryOnFailure(int maxRetryCount, TimeSpan maxRetryDelay, IEnumerable<int>? errorNumbersToAdd) => this;
    }

    public class DbContextOptionsBuilder
    {
        public DbContextOptionsBuilder UseSqlite(string connectionString) => this;
        public DbContextOptionsBuilder UseSqlServer(string? connectionString, Action<SqlServerOptions>? configure = null) => this;
    }

    public class DatabaseFacade
    {
        public Task MigrateAsync() => Task.CompletedTask;
        public Task<bool> EnsureCreatedAsync() => Task.FromResult(true);
        public Task<bool> EnsureDeletedAsync() => Task.FromResult(true);
        public Task<bool> CanConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    public static class FakeDatabaseExtensions
    {
        public static bool IsSqlServer(this DatabaseFacade db) => false;
        public static bool IsSqlite(this DatabaseFacade db) => true;
        public static string GenerateCreateScript(this DatabaseFacade db) => "fake";
        public static IEnumerable<string> GetMigrations(this DatabaseFacade db) => Array.Empty<string>();
        public static Task<IEnumerable<string>> GetPendingMigrationsAsync(this DatabaseFacade db, CancellationToken ct = default) => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        public static Task<IEnumerable<string>> GetAppliedMigrationsAsync(this DatabaseFacade db, CancellationToken ct = default) => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        public static void SetCommandTimeout(this DatabaseFacade db, TimeSpan? timeout) { }

        // 백업(BackupService)이 쓰는 SQL. 메모리 DB 라 실제 사본은 없고, VACUUM INTO 는 그 자리에 표시 파일만 만든다 —
        // 화면 점검이 백업 목록·크기 표시까지 따라갈 수 있게 하기 위해서다
        public static Task<int> ExecuteSqlRawAsync(this DatabaseFacade db, string sql, IEnumerable<object> parameters, CancellationToken ct = default)
        {
            if (sql.TrimStart().StartsWith("VACUUM INTO", StringComparison.OrdinalIgnoreCase) && parameters.FirstOrDefault() is string path)
                File.WriteAllText(path, "fake sqlite backup (shadow build)");
            return Task.FromResult(0);
        }
    }

    public class DbContext : IDisposable, IAsyncDisposable
    {
        internal static readonly FakeStore Store = new();
        private readonly List<object> _added = new();
        private readonly List<object> _removed = new();
        private static bool _modelBuilt;

        public DbContext(DbContextOptions options)
        {
            Database = new DatabaseFacade();
            if (!_modelBuilt)
            {
                // 모델 구성 코드가 예외 없이 도는지만 확인
                _modelBuilt = true;
                ConfigureConventions(new ModelConfigurationBuilder());
                OnModelCreating(new ModelBuilder());
            }
        }

        public DatabaseFacade Database { get; }

        public DbSet<T> Set<T>() where T : class => new(this);

        internal void TrackAdd(object e) => _added.Add(e);
        internal void TrackRemove(object e) => _removed.Add(e);

        protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
        protected virtual void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) { }

        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(SaveChanges());

        public int SaveChanges()
        {
            Store.Commit(_added, _removed);
            _added.Clear();
            _removed.Clear();
            return 1;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }

    public class DbSet<T> : IQueryable<T> where T : class
    {
        private readonly DbContext _ctx;
        private readonly IQueryable<T> _q;

        internal DbSet(DbContext ctx)
        {
            _ctx = ctx;
            _q = DbContext.Store.Snapshot<T>().AsQueryable();
        }

        public Type ElementType => typeof(T);
        public Expression Expression => _q.Expression;
        public IQueryProvider Provider => _q.Provider;
        public IEnumerator<T> GetEnumerator() => _q.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _q.GetEnumerator();

        public void Add(T entity) => _ctx.TrackAdd(entity);
        public void AddRange(params T[] entities) { foreach (var e in entities) _ctx.TrackAdd(e); }
        public void AddRange(IEnumerable<T> entities) { foreach (var e in entities.ToList()) _ctx.TrackAdd(e); }
        public void Remove(T entity) => _ctx.TrackRemove(entity);
        public void RemoveRange(IEnumerable<T> entities) { foreach (var e in entities.ToList()) _ctx.TrackRemove(e); }
    }

    internal sealed class Includable<TEntity, TProperty> : IIncludableQueryable<TEntity, TProperty>
    {
        private readonly IQueryable<TEntity> _q;
        public Includable(IQueryable<TEntity> q) => _q = q;
        public Type ElementType => _q.ElementType;
        public Expression Expression => _q.Expression;
        public IQueryProvider Provider => _q.Provider;
        public IEnumerator<TEntity> GetEnumerator() => _q.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _q.GetEnumerator();
    }

    public static class EntityFrameworkQueryableExtensions
    {
        public static IIncludableQueryable<TEntity, TProperty> Include<TEntity, TProperty>(
            this IQueryable<TEntity> source, Expression<Func<TEntity, TProperty>> path) where TEntity : class
            => new Includable<TEntity, TProperty>(source);

        public static IIncludableQueryable<TEntity, TProperty> ThenInclude<TEntity, TPrevious, TProperty>(
            this IIncludableQueryable<TEntity, IEnumerable<TPrevious>> source, Expression<Func<TPrevious, TProperty>> path) where TEntity : class
            => new Includable<TEntity, TProperty>(source);

        public static IIncludableQueryable<TEntity, TProperty> ThenInclude<TEntity, TPrevious, TProperty>(
            this IIncludableQueryable<TEntity, TPrevious> source, Expression<Func<TPrevious, TProperty>> path) where TEntity : class
            => new Includable<TEntity, TProperty>(source);

        public static IQueryable<T> AsNoTracking<T>(this IQueryable<T> source) where T : class => source;
        public static IQueryable<T> AsSplitQuery<T>(this IQueryable<T> source) where T : class => source;

        public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct = default) => Task.FromResult(source.ToList());
        public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default) => Task.FromResult(source.FirstOrDefault());
        public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.FromResult(source.FirstOrDefault(predicate));
        public static Task<T> FirstAsync<T>(this IQueryable<T> source, CancellationToken ct = default) => Task.FromResult(source.First());
        public static Task<T> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.FromResult(source.First(predicate));
        public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken ct = default) => Task.FromResult(source.Any());
        public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.FromResult(source.Any(predicate));
        public static Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken ct = default) => Task.FromResult(source.Count());
        public static Task<int> CountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.FromResult(source.Count(predicate));
    }

    // ── 모델 구성 API: 호출만 받아 주는 빈 껍데기 ──────────────────────────
    public class ModelBuilder
    {
        public ModelBuilder HasDefaultSchema(string schema) => this;
        public EntityTypeBuilder<T> Entity<T>() where T : class => new();
        public ModelBuilder Entity<T>(Action<EntityTypeBuilder<T>> build) where T : class { build(new()); return this; }
    }

    public class EntityTypeBuilder<T> where T : class
    {
        public EntityTypeBuilder<T> ToTable(string name) => this;
        public PropertyBuilder<P> Property<P>(Expression<Func<T, P>> property) => new();
        public IndexBuilder HasIndex(Expression<Func<T, object?>> index) => new();
        public ReferenceNavigationBuilder<T, R> HasOne<R>(Expression<Func<T, R?>>? navigation = null) where R : class => new();
    }

    public class PropertyBuilder<P>
    {
        public PropertyBuilder<P> HasMaxLength(int length) => this;
        public PropertyBuilder<P> IsRequired(bool required = true) => this;
        public PropertyBuilder<P> HasConversion<TProvider>() => this;
        public PropertyBuilder<P> IsConcurrencyToken(bool token = true) => this;
    }

    public class IndexBuilder
    {
        public IndexBuilder IsUnique(bool unique = true) => this;
    }

    public class ReferenceNavigationBuilder<T, R> where T : class where R : class
    {
        public ReferenceCollectionBuilder<R, T> WithMany(Expression<Func<R, IEnumerable<T>?>>? navigation = null) => new();
    }

    public class ReferenceCollectionBuilder<R, T> where T : class where R : class
    {
        public ReferenceCollectionBuilder<R, T> HasForeignKey(Expression<Func<T, object?>> key) => this;
        public ReferenceCollectionBuilder<R, T> OnDelete(DeleteBehavior behavior) => this;
    }

    public class ModelConfigurationBuilder
    {
        public PropertiesConfigurationBuilder<P> Properties<P>() => new();
    }

    public class PropertiesConfigurationBuilder<P>
    {
        public PropertiesConfigurationBuilder<P> HaveConversion<TConverter>() => this;
    }

    // ── 메모리 저장소 ────────────────────────────────────────────────────
    internal sealed class FakeStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Type, List<object>> _tables = new();
        private readonly Dictionary<Type, int> _nextId = new();
        private readonly Dictionary<Type, Meta> _meta = new();

        private sealed class Meta
        {
            public PropertyInfo Id = null!;
            public List<(PropertyInfo Nav, PropertyInfo Fk)> Refs = new();
            public List<(PropertyInfo Coll, Type Child)> Colls = new();
        }

        private static bool IsEntity(Type t) =>
            t.IsClass && t.Namespace == "GanttScheduler.Domain" && t.GetProperty("Id")?.PropertyType == typeof(int);

        private Meta MetaOf(Type t)
        {
            if (_meta.TryGetValue(t, out var m)) return m;
            m = new Meta { Id = t.GetProperty("Id")! };
            foreach (var p in t.GetProperties())
            {
                if (IsEntity(p.PropertyType))
                {
                    var fk = t.GetProperty(p.Name + "Id");
                    if (fk != null) m.Refs.Add((p, fk));
                }
                else if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                         && IsEntity(p.PropertyType.GetGenericArguments()[0]))
                {
                    m.Colls.Add((p, p.PropertyType.GetGenericArguments()[0]));
                }
            }
            _meta[t] = m;
            return m;
        }

        // 자식 타입에서 부모를 가리키는 FK 속성
        private PropertyInfo? ChildFk(Type child, Type parent)
        {
            var refs = MetaOf(child).Refs.Where(r => r.Nav.PropertyType == parent).ToList();
            if (refs.Count == 1) return refs[0].Fk;
            return refs.FirstOrDefault(r => r.Nav.Name == parent.Name).Fk;
        }

        private List<object> Table(Type t)
        {
            if (!_tables.TryGetValue(t, out var list)) _tables[t] = list = new List<object>();
            return list;
        }

        private static int IdOf(object e) => (int)e.GetType().GetProperty("Id")!.GetValue(e)!;

        public List<T> Snapshot<T>() where T : class
        {
            lock (_gate) return Table(typeof(T)).Cast<T>().ToList();
        }

        public void Commit(List<object> added, List<object> removed)
        {
            lock (_gate)
            {
                // 지운 엔티티는 부모 컬렉션에 아직 매달려 있어도 다시 살리지 않는다
                var gone = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var e in removed) RemoveCascade(e, gone);

                // 새 엔티티 수집: 명시적으로 Add 한 것 + 기존/새 엔티티의 내비게이션에 매달린 것
                var stored = new HashSet<object>(_tables.Values.SelectMany(x => x), ReferenceEqualityComparer.Instance);
                var queue = new Queue<object>(added);
                foreach (var e in stored.ToList()) EnqueueRelated(e, queue);
                var fresh = new List<object>();
                while (queue.Count > 0)
                {
                    var e = queue.Dequeue();
                    if (gone.Contains(e)) continue;
                    if (!stored.Add(e)) continue;
                    fresh.Add(e);
                    EnqueueRelated(e, queue);
                }

                foreach (var e in fresh)
                {
                    var t = e.GetType();
                    var meta = MetaOf(t);
                    if ((int)meta.Id.GetValue(e)! == 0)
                    {
                        _nextId[t] = _nextId.GetValueOrDefault(t) + 1;
                        meta.Id.SetValue(e, _nextId[t]);
                    }
                    Table(t).Add(e);
                }

                // FK 채우기: 컬렉션 소속(자식 FK 가 비어 있을 때만)과 참조 내비게이션에서
                var freshSet = new HashSet<object>(fresh, ReferenceEqualityComparer.Instance);
                foreach (var e in stored)
                {
                    var meta = MetaOf(e.GetType());
                    foreach (var (coll, childType) in meta.Colls)
                    {
                        var fk = ChildFk(childType, e.GetType());
                        if (fk == null || coll.GetValue(e) is not IEnumerable children) continue;
                        foreach (var c in children)
                            if (fk.GetValue(c) is not int v || v == 0) fk.SetValue(c, IdOf(e));
                    }
                    foreach (var (nav, fk) in meta.Refs)
                    {
                        var target = nav.GetValue(e);
                        // 이번에 새로 추가된 엔티티(예: 아직 Id 가 없던 새 담당자)를 가리키면 FK 가 있어도 그 Id 로 맞춘다 — 진짜 EF 의 내비게이션 고정과 같게
                        if (target != null && (fk.GetValue(e) is not int v || v == 0 || freshSet.Contains(target))) fk.SetValue(e, IdOf(target));
                    }
                }

                // 내비게이션 정리: 참조는 FK 로 다시 찾고, 컬렉션은 FK 기준으로 다시 채운다
                foreach (var e in stored)
                {
                    var meta = MetaOf(e.GetType());
                    foreach (var (nav, fk) in meta.Refs)
                    {
                        var id = fk.GetValue(e) as int?;
                        nav.SetValue(e, id == null ? null : Table(nav.PropertyType).FirstOrDefault(x => IdOf(x) == id.Value));
                    }
                    foreach (var (coll, childType) in meta.Colls)
                    {
                        var fk = ChildFk(childType, e.GetType());
                        if (fk == null || coll.GetValue(e) is not IList list) continue;
                        var id = IdOf(e);
                        var members = Table(childType).Where(c => fk.GetValue(c) is int v && v == id).ToList();
                        list.Clear();
                        foreach (var c in members) list.Add(c);
                    }
                }
            }
        }

        private void EnqueueRelated(object e, Queue<object> queue)
        {
            var meta = MetaOf(e.GetType());
            foreach (var (coll, _) in meta.Colls)
                if (coll.GetValue(e) is IEnumerable children)
                    foreach (var c in children) queue.Enqueue(c);
            foreach (var (nav, _) in meta.Refs)
                if (nav.GetValue(e) is { } target) queue.Enqueue(target);
        }

        private void RemoveCascade(object e, HashSet<object> gone)
        {
            if (!gone.Add(e)) return;
            Table(e.GetType()).Remove(e);
            foreach (var (coll, _) in MetaOf(e.GetType()).Colls)
                if (coll.GetValue(e) is IEnumerable children)
                    foreach (var c in children.Cast<object>().ToList()) RemoveCascade(c, gone);
        }
    }
}

namespace Microsoft.EntityFrameworkCore.Query
{
    public interface IIncludableQueryable<out TEntity, out TProperty> : IQueryable<TEntity> { }
}

namespace Microsoft.EntityFrameworkCore.Storage.ValueConversion
{
    public class ValueConverter<TModel, TProvider>
    {
        public ValueConverter(Expression<Func<TModel, TProvider>> toProvider, Expression<Func<TProvider, TModel>> fromProvider) { }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class FakeEfServiceExtensions
    {
        public static IServiceCollection AddDbContext<T>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
            where T : DbContext
        {
            configure(new DbContextOptionsBuilder());
            services.AddScoped(sp => (T)Activator.CreateInstance(typeof(T), new DbContextOptions<T>())!);
            return services;
        }
    }
}
