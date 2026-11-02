using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record BackupRequest(string Passphrase);
public sealed record ValidateBackupRequest(string ArchiveBase64, string Passphrase);
public sealed record BackupBundle(int Format, DateTimeOffset CreatedAt, Dictionary<string, DomainEvent[]> Companies, string Digest);
public sealed record BackupEnvelope(int Format, int Iterations, byte[] Salt, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
public sealed record BackupValidation(int CompanyLogs, int EventCount, string Digest);

public static class BackupCodec
{
    private const int Iterations = 600_000;
    private const int MaxArchiveBytes = 16 * 1024 * 1024;
    public static byte[] Encrypt(BackupBundle bundle, string passphrase)
    {
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(bundle, RuntimeJson.Options);
        try
        {
            if (plaintext.Length > MaxArchiveBytes / 2) throw new ArgumentException("This backup exceeds the milestone size limit (8 MiB before encryption).");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, "ai-company-os/backup/v1"u8);
            return JsonSerializer.SerializeToUtf8Bytes(new BackupEnvelope(1, Iterations, salt, nonce, tag, ciphertext), RuntimeJson.Options);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static BackupBundle Decrypt(byte[] archive, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (archive.Length > MaxArchiveBytes) throw new InvalidDataException("Backup exceeds the size limit.");
        try
        {
            var envelope = JsonSerializer.Deserialize<BackupEnvelope>(archive, RuntimeJson.Options) ?? throw new InvalidDataException("Invalid backup.");
            if (envelope.Format != 1 || envelope.Iterations != Iterations || envelope.Salt?.Length != 16 ||
                envelope.Nonce?.Length != 12 || envelope.Tag?.Length != 16 || envelope.Ciphertext is null)
                throw new InvalidDataException("Unsupported backup format or encryption parameters.");
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, envelope.Salt, Iterations, HashAlgorithmName.SHA256, 32);
            var plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, "ai-company-os/backup/v1"u8);
                var bundle = JsonSerializer.Deserialize<BackupBundle>(plaintext, RuntimeJson.Options) ?? throw new InvalidDataException("Empty backup.");
                if (bundle.Format != 1 || bundle.Companies is null || bundle.Digest != Digest(bundle.Companies)) throw new InvalidDataException("Backup checksum mismatch.");
                foreach (var (company, events) in bundle.Companies)
                {
                    Identifiers.Require(company);
                    if (events is null) throw new InvalidDataException("Missing events.");
                    EventIntegrity.Validate(company, events);
                }
                return bundle;
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        { throw new InvalidDataException("Backup could not be authenticated or decoded."); }
    }
    public static string Digest(Dictionary<string, DomainEvent[]> companies) => RuntimeJson.Hash(companies.OrderBy(c => c.Key, StringComparer.Ordinal)
        .Select(c => new { companyId = c.Key, digest = RuntimeJson.Hash(c.Value) }).ToArray());
    private static void ValidatePassphrase(string passphrase)
    {
        if (passphrase is not { Length: >= 12 and <= 256 }) throw new ArgumentException("Use a passphrase between 12 and 256 characters.");
    }
}

public sealed partial class FileEventStore
{
    public async Task<byte[]> ExportBackupAsync(string passphrase, CancellationToken ct)
    {
        Dictionary<string, DomainEvent[]> companies = new();
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var company in _journal.Companies()) companies[company] = (await _journal.ReadAsync(company, ct)).ToArray();
        }
        finally { _gate.Release(); }
        return BackupCodec.Encrypt(new(1, DateTimeOffset.UtcNow, companies, BackupCodec.Digest(companies)), passphrase);
    }

    public async Task<BackupValidation> RestoreIntoEmptyAsync(byte[] archive, string passphrase, CancellationToken ct)
    {
        var bundle = BackupCodec.Decrypt(archive, passphrase);
        await _gate.WaitAsync(ct);
        try
        {
            if (_journal.Companies().Length != 0) throw new WorkspaceConflictException("Restore requires an empty data directory. Existing workspaces are never overwritten.");
            var profile = new StorageProfile(Backend, "restore-" + Guid.NewGuid().ToString("N"), "encrypted-backup", bundle.Digest, DateTimeOffset.UtcNow);
            IEventJournal? target = OpenJournal(profile);
            try
            {
                foreach (var (company, events) in bundle.Companies) await target.AppendAsync(company, 0, events, ct);
                var restored = new Dictionary<string, DomainEvent[]>();
                foreach (var company in target.Companies()) restored[company] = (await target.ReadAsync(company, ct)).ToArray();
                if (BackupCodec.Digest(restored) != bundle.Digest) throw new InvalidDataException("Restored checksum mismatch.");
                await LocalFiles.WriteAtomicAsync(LocalFiles.Child(_root, "storage.json"), JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions), ct);
                var previous = _journal;
                _journal = target;
                _profile = profile;
                target = null;
                previous.Dispose();
                return new(bundle.Companies.Count, bundle.Companies.Values.Sum(e => e.Length), bundle.Digest);
            }
            finally { target?.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
