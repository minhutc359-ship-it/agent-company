using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AICompanyOS.Runtime;

public static class RuntimeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Hash<T>(T value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));
    public static string TextHash(string value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class Identifiers
{
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 100 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static void Require(string value)
    {
        if (!IsValid(value)) throw new ArgumentException("Invalid identifier.");
    }
}

internal static class LocalFiles
{
    // Reject child links as well as traversal: a local import must never redirect runtime writes.
    public static string Child(string root, params string[] parts)
    {
        var path = root;
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains('/') || part.Contains('\\')) throw new ArgumentException("Invalid path component.");
            path = Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new InvalidDataException("Symbolic links inside the data directory are not supported.");
        }
        return path;
    }

    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record StorageProfile(string Backend, string? Generation = null, string? SourceBackend = null,
    string? SourceDigest = null, DateTimeOffset? ActivatedAt = null);
public sealed record CompanyIntegrity(string CompanyId, int EventCount, string Digest, string? LastHash, int LegacyEvents);
public sealed record StorageManifest(string Backend, CompanyIntegrity[] Companies, string Digest);
public sealed record MigrationRequest(string Backend, string ExpectedDigest);
public sealed record MigrationResult(string From, string To, string Digest, int EventCount, bool Changed);

internal interface IEventJournal : IDisposable
{
    string Backend { get; }
    string[] Companies();
    Task<List<DomainEvent>> ReadAsync(string companyId, CancellationToken ct);
    Task AppendAsync(string companyId, int expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken ct);
}

internal static class EventIntegrity
{
    private static string LegacyLink(string? previous, DomainEvent item) => RuntimeJson.Hash(new { previous, legacy = RuntimeJson.Hash(item) });
    public static string Hash(DomainEvent item) => RuntimeJson.Hash(new
    { item.Seq, item.EventId, item.RunId, item.Type, item.CreatedAt, item.Data, item.CompanyId, item.PrevHash });

    public static void Validate(string companyId, IReadOnlyList<DomainEvent> events)
    {
        string? previous = null;
        var hashed = false;
        var ids = new HashSet<string>();
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.Seq != index + 1 || !Identifiers.IsValid(item.EventId) || !ids.Add(item.EventId) || !Identifiers.IsValid(item.RunId) ||
                item.Type is not { Length: > 0 and <= 100 } || item.Data.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid event sequence, identity or aggregate.");
            if (item.ContentSha256 is null)
            {
                if (hashed || item.CompanyId is not null || item.PrevHash is not null)
                    throw new InvalidDataException("Unhashed event after the integrity boundary.");
                previous = LegacyLink(previous, item);
                continue; // Initial runtime logs are accepted as an explicitly reported legacy prefix.
            }
            hashed = true;
            if (item.CompanyId != companyId || item.PrevHash != previous || item.ContentSha256 != Hash(item))
                throw new InvalidDataException("Event integrity verification failed.");
            previous = item.ContentSha256;
        }
    }

    public static DomainEvent[] Seal(string companyId, string runId, IReadOnlyList<DomainEvent> existing, IReadOnlyList<DomainEvent> additions)
    {
        var result = new List<DomainEvent>();
        string? previous = null;
        foreach (var item in existing) previous = item.ContentSha256 ?? LegacyLink(previous, item);
        foreach (var item in additions)
        {
            var next = item with { Seq = existing.Count + result.Count + 1, RunId = runId, CompanyId = companyId, PrevHash = previous, ContentSha256 = null };
            next = next with { ContentSha256 = Hash(next) };
            result.Add(next);
            previous = next.ContentSha256;
        }
        return result.ToArray();
    }
}

internal sealed class FileJournal(string root) : IEventJournal
{
    public string Backend => "file";
    public void Dispose() { }
    private string PathFor(string company)
    {
        Identifiers.Require(company);
        return LocalFiles.Child(root, "companies", company, "events.jsonl");
    }
    public string[] Companies()
    {
        var path = LocalFiles.Child(root, "companies");
        return !Directory.Exists(path) ? [] : Directory.GetDirectories(path).Select(Path.GetFileName)
            .Where(id => Identifiers.IsValid(id) && File.Exists(PathFor(id!))).Cast<string>().Order(StringComparer.Ordinal).ToArray();
    }
    public async Task<List<DomainEvent>> ReadAsync(string companyId, CancellationToken ct)
    {
        var path = PathFor(companyId);
        if (!File.Exists(path)) return [];
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var validLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        // Only a non-terminated tail can be recovered. Corruption inside a committed record fails closed.
        if (validLength != bytes.Length)
        {
            await LocalFiles.WriteAtomicAsync(path + ".torn-" + Guid.NewGuid().ToString("N"), bytes[validLength..], ct);
            await LocalFiles.WriteAtomicAsync(path, bytes[..validLength], ct);
        }
        var result = new List<DomainEvent>();
        try
        {
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, validLength).Split('\n'))
            {
                if (line.Length == 0) continue;
                result.Add(JsonSerializer.Deserialize<DomainEvent>(line, RuntimeJson.Options) ?? throw new InvalidDataException("Empty event."));
            }
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed committed event log.", error); }
        EventIntegrity.Validate(companyId, result);
        return result;
    }
    public async Task AppendAsync(string companyId, int expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken ct)
    {
        var existing = await ReadAsync(companyId, ct);
        if (existing.Count != expectedVersion) throw new InvalidDataException("Event version conflict.");
        existing.AddRange(events);
        EventIntegrity.Validate(companyId, existing);
        // Logical append-only history; atomic physical replacement commits the entire command batch.
        var text = string.Join('\n', existing.Select(e => JsonSerializer.Serialize(e, RuntimeJson.Options))) + "\n";
        await LocalFiles.WriteAtomicAsync(PathFor(companyId), Encoding.UTF8.GetBytes(text), ct);
    }
}

internal sealed class SqliteJournal : IEventJournal
{
    private readonly SqliteConnection _connection;
    public string Backend => "sqlite";
    public SqliteJournal(string root)
    {
        Directory.CreateDirectory(root);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = LocalFiles.Child(root, "events.sqlite"), Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) { _connection.Dispose(); throw new InvalidDataException("Unsupported SQLite schema version."); }
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS events (company_id TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL CHECK(json_valid(body)), PRIMARY KEY(company_id,seq));
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    public void Dispose() => _connection.Dispose();
    public string[] Companies()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT company_id FROM events ORDER BY company_id";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
    public async Task<List<DomainEvent>> ReadAsync(string companyId, CancellationToken ct)
    {
        Identifiers.Require(companyId);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT seq,body FROM events WHERE company_id=$company ORDER BY seq";
        command.Parameters.AddWithValue("$company", companyId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<DomainEvent>();
        try
        {
            while (await reader.ReadAsync(ct))
            {
                var item = JsonSerializer.Deserialize<DomainEvent>(reader.GetString(1), RuntimeJson.Options) ?? throw new InvalidDataException("Empty event.");
                if (reader.GetInt64(0) != item.Seq) throw new InvalidDataException("SQLite row sequence does not match the event.");
                result.Add(item);
            }
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed SQLite event.", error); }
        EventIntegrity.Validate(companyId, result);
        return result;
    }
    public async Task AppendAsync(string companyId, int expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken ct)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM events WHERE company_id=$company";
        command.Parameters.AddWithValue("$company", companyId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != expectedVersion) throw new InvalidDataException("Event version conflict.");
        command.CommandText = "INSERT INTO events(company_id,seq,body) VALUES($company,$seq,$body)";
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        var body = command.Parameters.Add("$body", SqliteType.Text);
        foreach (var item in events)
        {
            seq.Value = item.Seq;
            body.Value = JsonSerializer.Serialize(item, RuntimeJson.Options);
            await command.ExecuteNonQueryAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
