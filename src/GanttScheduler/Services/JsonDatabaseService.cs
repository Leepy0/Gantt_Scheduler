using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GanttScheduler.Data;
using GanttScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace GanttScheduler.Services;

// SQLite 데이터의 이식 가능한 JSON 스냅샷. DB 파일 자체의 VACUUM 백업과 별개로 테이블별 JSON을 제공한다.
// 복원은 현재 스키마가 정확히 일치할 때만 허용하고, 단일 SQLite 트랜잭션 안에서 전체 테이블을 교체한다.
public sealed class JsonDatabaseService
{
    private const int FormatVersion = 1;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;

    public JsonDatabaseService(IServiceScopeFactory scopes, IConfiguration config)
    {
        _scopes = scopes;
        _config = config;
    }

    public async Task<byte[]> ExportAsync(CancellationToken ct)
    {
        if (DbSetup.UseSqlServer(_config))
            throw new RuleException("테이블별 JSON 내보내기는 SQLite 운영 DB에서만 지원합니다.");

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await db.Database.OpenConnectionAsync(ct);
        try
        {
            var tables = await ReadTablesAsync(connection, null, ct);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var metadata = new List<JsonTableInfo>();
                foreach (var table in tables)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = "tables/" + Convert.ToHexString(Encoding.UTF8.GetBytes(table.Name)).ToLowerInvariant() + ".json";
                    var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                    long rows;
                    await using (var stream = entry.Open())
                    {
                        rows = await WriteTableAsync(connection, null, table, stream, ct);
                    }
                    metadata.Add(new JsonTableInfo(table.Name, file, table.Columns.Select(c => c.Name).ToArray(),
                        table.Columns.Select(c => c.Type).ToArray(), rows));
                }

                var manifest = new JsonManifest(FormatVersion, DateTime.UtcNow, "SQLite", metadata);
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct);
            }
            return output.ToArray();
        }
        finally
        {
            if (!wasOpen) await db.Database.CloseConnectionAsync();
        }
    }

    public async Task RestoreAsync(Stream archiveStream, CancellationToken ct)
    {
        if (DbSetup.UseSqlServer(_config))
            throw new RuleException("테이블별 JSON 복원은 SQLite 운영 DB에서만 지원합니다.");

        using var archiveData = new MemoryStream();
        await CopyBoundedAsync(archiveStream, archiveData, MaxArchiveBytes, ct);
        archiveData.Position = 0;
        using var zip = new ZipArchive(archiveData, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is < 1 or > 500)
            throw new RuleException("JSON 백업 파일의 항목 수가 올바르지 않습니다.");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new RuleException("manifest.json 이 없는 JSON 백업입니다.");
        if (manifestEntry.Length > 4 * 1024 * 1024)
            throw new RuleException("JSON 백업의 manifest 파일이 너무 큽니다.");
        JsonManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<JsonManifest>(stream, JsonOptions, ct)
                ?? throw new RuleException("JSON 백업 정보를 읽을 수 없습니다.");
        if (manifest.FormatVersion != FormatVersion || !string.Equals(manifest.Provider, "SQLite", StringComparison.Ordinal))
            throw new RuleException("지원하지 않는 JSON 백업 형식입니다.");
        if (manifest.Tables.Count > 250 || manifest.Tables.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Tables.Count)
            throw new RuleException("JSON 백업의 테이블 목록이 올바르지 않습니다.");
        long expanded = manifestEntry.Length;
        foreach (var table in manifest.Tables)
        {
            var entry = zip.GetEntry(table.File) ?? throw new RuleException($"JSON 백업에 테이블 파일이 없습니다: {table.Name}");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxArchiveBytes) throw new RuleException("압축을 푼 JSON 백업이 허용 크기를 초과합니다.");
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await db.Database.OpenConnectionAsync(ct);
        var foreignKeysDisabled = false;
        try
        {
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys = OFF;", ct);
            foreignKeysDisabled = true;
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var current = await ReadTablesAsync(connection, transaction, ct);
            var currentNames = current.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var backupNames = manifest.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (!currentNames.SequenceEqual(backupNames, StringComparer.Ordinal))
                throw new RuleException("JSON 백업의 테이블 구성이 현재 DB와 다릅니다. 같은 앱 버전에서 만든 백업인지 확인하세요.");

            foreach (var table in manifest.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var schema = current.Single(t => t.Name == table.Name);
                if (!schema.Columns.Select(c => c.Name).SequenceEqual(table.Columns, StringComparer.Ordinal)
                    || !schema.Columns.Select(c => c.Type).SequenceEqual(table.Types, StringComparer.OrdinalIgnoreCase))
                    throw new RuleException($"테이블 구조가 달라 복원할 수 없습니다: {table.Name}");

                await ExecuteAsync(connection, transaction, $"DELETE FROM {Quote(table.Name)};", ct);
                var entry = zip.GetEntry(table.File)!;
                await using var stream = entry.Open();
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new RuleException($"테이블 JSON 형식이 올바르지 않습니다: {table.Name}");
                long count = 0;
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) throw new RuleException($"행 형식이 올바르지 않습니다: {table.Name}");
                    var values = row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    if (values.Count != schema.Columns.Count || schema.Columns.Any(c => !values.ContainsKey(c.Name)))
                        throw new RuleException($"열 구성이 올바르지 않습니다: {table.Name}");
                    await InsertRowAsync(connection, transaction, schema, values, ct);
                    count++;
                }
                if (count != table.Rows)
                    throw new RuleException($"행 개수가 백업 정보와 다릅니다: {table.Name}");
            }

            // 외래 키가 깨진 JSON을 복원하지 않는다. FK가 꺼져 있는 동안에도 명시적으로 전체 검사를 수행한다.
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await check.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    throw new RuleException("복원 데이터의 관계(FK)가 올바르지 않아 복원을 취소했습니다.");
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (foreignKeysDisabled)
            {
                try { await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", CancellationToken.None); }
                catch { /* 연결 종료 시 SQLite가 기본 설정으로 되돌린다 */ }
            }
            if (!wasOpen) await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<List<JsonTableSchema>> ReadTablesAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        }

        var result = new List<JsonTableSchema>();
        foreach (var name in names)
        {
            var columns = new List<JsonColumn>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({Quote(name)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(new JsonColumn(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
            result.Add(new JsonTableSchema(name, columns));
        }
        return result;
    }

    private static async Task<long> WriteTableAsync(DbConnection connection, DbTransaction? transaction, JsonTableSchema table, Stream output, CancellationToken ct)
    {
        long count = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT * FROM {Quote(table.Name)};";
        await using var reader = await command.ExecuteReaderAsync(ct);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        while (await reader.ReadAsync(ct))
        {
            writer.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                writer.WritePropertyName(reader.GetName(i));
                if (reader.IsDBNull(i)) writer.WriteNullValue();
                else if (reader.GetValue(i) is byte[] bytes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("$blob", Convert.ToBase64String(bytes));
                    writer.WriteEndObject();
                }
                else JsonSerializer.Serialize(writer, reader.GetValue(i), JsonOptions);
            }
            writer.WriteEndObject();
            count++;
        }
        writer.WriteEndArray();
        await writer.FlushAsync(ct);
        return count;
    }

    private static async Task InsertRowAsync(DbConnection connection, DbTransaction transaction, JsonTableSchema table,
        IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        var columns = table.Columns;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Quote(table.Name)} ({string.Join(", ", columns.Select(c => Quote(c.Name)))}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))});";
        for (var i = 0; i < columns.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = ToDbValue(values[columns[i].Name]);
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    private static object ToDbValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt64(out var n) => n,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Object when value.TryGetProperty("$blob", out var blob) && blob.ValueKind == JsonValueKind.String
            => Convert.FromBase64String(blob.GetString()!),
        _ => throw new RuleException("JSON 백업에 지원하지 않는 값 형식이 있습니다.")
    };

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new RuleException("JSON 백업 파일이 허용 크기(512MB)를 초과합니다.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record JsonColumn(string Name, string Type);
    private sealed record JsonTableSchema(string Name, List<JsonColumn> Columns);
    public sealed record JsonTableInfo(string Name, string File, string[] Columns, string[] Types, long Rows);
    public sealed record JsonManifest(int FormatVersion, DateTime ExportedAtUtc, string Provider, List<JsonTableInfo> Tables);
}
