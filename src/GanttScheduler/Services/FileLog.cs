using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace GanttScheduler.Services;

// 서버 상태 화면에 보이는 최근 경고·오류 한 건. RequestId 는 화면의 '오류 번호'(HttpContext.TraceIdentifier)와 같다
public record LogEntry(DateTime At, string Level, string Category, string Message, string? RequestId, string? Path);

// 파일 로그: <Logs:Path>/yyyy-MM-dd.log (한국 날짜), Logs:KeepDays 일이 지난 파일은 지운다.
// 서비스로 돌 때 로그는 이벤트 뷰어에만 남아 원격 데스크톱 없이는 볼 수 없었다 — 서버 상태 화면과 파일로 보이게 한다.
// 요청을 늦추지 않도록 큐에 넣고 백그라운드에서 쓴다. 최근 경고·오류 200건은 메모리(링 버퍼)에도 둔다.
// 공유 프레임워크(Microsoft.Extensions.Logging)만 써서 섀도 빌드에서도 그대로 컴파일된다
public sealed class FileLog : ILoggerProvider, ISupportExternalScope
{
    public const int RecentMax = 200;
    public const string FilePattern = "????-??-??.log";

    private readonly Channel<(DateOnly Day, string Line)> _queue =
        Channel.CreateUnbounded<(DateOnly, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Task _writer;
    private IExternalScopeProvider? _scopes;
    private long _pending;
    private DateOnly _prunedFor;

    public FileLog(string dir, int keepDays, Func<DateTime>? utcNow = null)
    {
        Dir = dir;
        KeepDays = Math.Clamp(keepDays, 1, 3650);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Dir { get; }
    public int KeepDays { get; }
    // 마지막 쓰기 실패 (폴더 권한·디스크 가득 참). 로그를 로그로 남길 수 없으니 서버 상태 화면이 이것을 보여 준다
    public string? LastError { get; private set; }

    // 상대 경로는 앱 폴더 기준
    public static FileLog From(IConfiguration cfg, string contentRoot)
    {
        var path = cfg["Logs:Path"] is { Length: > 0 } p ? p : "App_Data/logs";
        return new FileLog(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path), cfg.GetValue("Logs:KeepDays", 30));
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    // 최근 경고·오류 (새것부터)
    public IReadOnlyList<LogEntry> Recent() => _recent.Reverse().ToList();

    // 오늘 파일 경로 (한국 날짜)
    public string PathOf(DateOnly day) => Path.Combine(Dir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? ex)
    {
        var utc = _utcNow();
        var kst = KoreaClock.ToKst(utc);
        string? requestId = null, path = null;
        // 호스팅이 요청마다 여는 범위(RequestId·RequestPath)에서 요청 번호를 꺼낸다 — 화면의 오류 번호와 같은 값이다
        _scopes?.ForEachScope((scope, _) =>
        {
            if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            foreach (var kv in pairs)
            {
                if (kv.Key == "RequestId" && requestId == null) requestId = kv.Value?.ToString();
                else if (kv.Key == "RequestPath" && path == null) path = kv.Value?.ToString();
            }
        }, (object?)null);

        var sb = new StringBuilder();
        sb.Append(kst.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append('[').Append(Short(level)).Append("] ").Append(category);
        if (eventId.Id != 0) sb.Append('[').Append(eventId.Id).Append(']');
        sb.Append(": ").Append(message);
        if (requestId != null) sb.Append(" (요청 ").Append(requestId).Append(path == null ? "" : " " + path).Append(')');
        if (ex != null) sb.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));

        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite((DateOnly.FromDateTime(kst), sb.ToString()))) Interlocked.Decrement(ref _pending);

        if (level >= LogLevel.Warning)
        {
            var text = ex == null ? message : message + " — " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            _recent.Enqueue(new LogEntry(utc, level >= LogLevel.Error ? "error" : "warn", category, text, requestId, path));
            while (_recent.Count > RecentMax && _recent.TryDequeue(out _)) { }
        }
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        _ => "CRIT",
    };

    private async Task WriteLoopAsync()
    {
        var batch = new List<(DateOnly Day, string Line)>();
        while (await _queue.Reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < 1000 && _queue.Reader.TryRead(out var item)) batch.Add(item);
            foreach (var group in batch.GroupBy(b => b.Day))
            {
                try
                {
                    if (group.Key != _prunedFor)
                    {
                        _prunedFor = group.Key;
                        Prune(group.Key);
                    }
                    Directory.CreateDirectory(Dir);
                    // 상태 화면이 읽는 중에도 쓸 수 있게(그리고 보존 정리가 지울 수 있게) 공유해서 연다
                    await using var fs = new FileStream(PathOf(group.Key), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    await using var w = new StreamWriter(fs, new UTF8Encoding(false));
                    foreach (var (_, line) in group) await w.WriteLineAsync(line);
                    LastError = null;
                }
                catch (Exception ex) { LastError = ex.Message; }
            }
            Interlocked.Add(ref _pending, -batch.Count);
        }
    }

    // 보존 기간이 지난 파일을 지운다 (이름의 날짜 기준). 날짜가 바뀐 뒤 처음 쓸 때 돈다
    public int Prune(DateOnly today)
    {
        var removed = 0;
        if (!Directory.Exists(Dir)) return 0;
        var cutoff = today.AddDays(-KeepDays);
        foreach (var file in Directory.GetFiles(Dir, FilePattern))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day >= cutoff) continue;
            try { File.Delete(file); removed++; }
            catch (Exception) { /* 다음 날 다시 */ }
        }
        return removed;
    }

    // 큐에 쌓인 줄을 다 쓸 때까지 기다린다 (시험·진단 내려받기 직전)
    public bool Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        // 서버가 내려갈 때 남은 줄을 쓰고 끝낸다 (너무 오래 붙잡지는 않는다)
        try { _writer.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* 무시 */ }
    }

    private sealed class Logger : ILogger
    {
        private readonly FileLog _owner;
        private readonly string _category;

        public Logger(FileLog owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(logLevel, _category, eventId, formatter(state, exception), exception);
        }
    }
}
