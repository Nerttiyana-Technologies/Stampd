using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

using Stampd.ByoCertDemo.Models;

namespace Stampd.ByoCertDemo.Services;

/// <summary>
/// Persists uploaded customer certificates encrypted at rest. The PFX bytes
/// and the PFX password are encrypted as a single blob via ASP.NET Core's
/// IDataProtector (AES-256-GCM under the hood, with a managed key ring). The
/// metadata (subject, issuer, expiry, thumbprint) lives next to the encrypted
/// blob as plaintext JSON because it's safe to enumerate without leaking
/// secret material, and the customer-facing list needs it cheaply.
/// </summary>
/// <remarks>
/// Storage path defaults to <c>~/.stampd/byo-cert-demo/vault/</c>. The Data
/// Protection key ring sits alongside at <c>../keys/</c>. Both directories
/// are created on first use with restrictive permissions (owner-only).
///
/// This is demo storage, not production. For a real customer-facing app you
/// would route the same IDataProtector calls through a KMS / Vault / Azure KV
/// — the encryption story stays the same, only the key root changes.
/// </remarks>
public sealed class CertVaultService
{
    private const string ProtectorPurpose = "Stampd.ByoCertDemo.CertVault.v1";

    private readonly IDataProtector _protector;
    private readonly string _vaultDir;
    private readonly ILogger<CertVaultService> _logger;

    public CertVaultService(IDataProtectionProvider protectionProvider, IConfiguration config, ILogger<CertVaultService> logger)
    {
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;

        var configured = config["Stampd:ByoCertDemo:VaultPath"];
        var baseDir = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".stampd", "byo-cert-demo");
        _vaultDir = Path.Combine(baseDir, "vault");
        Directory.CreateDirectory(_vaultDir);
        TrySetOwnerOnlyPermissions(_vaultDir);
    }

    /// <summary>
    /// Encrypt + persist a PFX. Returns the new cert id (a GUID slug). The
    /// supplied PFX bytes are loaded once to extract metadata, then the
    /// in-memory copy is overwritten with zeros before the method returns.
    /// </summary>
    public async Task<string> SaveAsync(byte[] pfxBytes, string password, string? friendlyName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pfxBytes);

        // Validate by loading once — also gives us the metadata to persist.
        using var cert = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password ?? string.Empty,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        var summary = CertSummary.From(cert);
        var id = Guid.NewGuid().ToString("N");
        var metaPath = Path.Combine(_vaultDir, id + ".meta");
        var certPath = Path.Combine(_vaultDir, id + ".cert");

        // Plaintext metadata for the list view (no secrets inside).
        var meta = new StoredCertMeta(
            Id: id,
            FriendlyName: string.IsNullOrWhiteSpace(friendlyName) ? summary.Subject : friendlyName!.Trim(),
            Subject: summary.Subject,
            Issuer: summary.Issuer,
            SerialNumber: summary.SerialNumber,
            Thumbprint: summary.Thumbprint,
            NotBeforeUtc: summary.NotBeforeUtc,
            NotAfterUtc: summary.NotAfterUtc,
            KeyAlgorithm: summary.KeyAlgorithm,
            KeySize: summary.KeySize,
            IsSelfSigned: summary.IsSelfSigned,
            UploadedAtUtc: DateTimeOffset.UtcNow);
        var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
        await File.WriteAllTextAsync(metaPath, metaJson, ct).ConfigureAwait(false);

        // Encrypted PFX + password as a single blob.
        var blob = new EncryptedCertBlob(pfxBytes, password ?? string.Empty);
        var blobJson = JsonSerializer.SerializeToUtf8Bytes(blob, JsonOpts);
        var encrypted = _protector.Protect(blobJson);
        await File.WriteAllBytesAsync(certPath, encrypted, ct).ConfigureAwait(false);

        // Best-effort zero of the in-memory PFX buffer. Caller-supplied buffer,
        // so we can't guarantee it isn't referenced elsewhere — but where it
        // came from (a Razor InputFile stream) it isn't, so this is helpful.
        Array.Clear(pfxBytes);

        _logger.LogInformation("Saved cert {Id} (subject={Subject})", id, summary.Subject);
        return id;
    }

    /// <summary>
    /// Decrypt and load a stored cert by id. Returns null if the id is unknown
    /// or the on-disk files are corrupt. The returned cert is yours to dispose.
    /// </summary>
    public X509Certificate2? Load(string id)
    {
        var certPath = Path.Combine(_vaultDir, id + ".cert");
        if (!File.Exists(certPath)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(certPath);
            var blobBytes = _protector.Unprotect(encrypted);
            var blob = JsonSerializer.Deserialize<EncryptedCertBlob>(blobBytes, JsonOpts)
                       ?? throw new InvalidOperationException("Decrypted vault blob was empty.");

            return X509CertificateLoader.LoadPkcs12(
                blob.PfxBytes,
                blob.Password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cert {Id} from vault", id);
            return null;
        }
    }

    /// <summary>
    /// List every cert in the vault, newest-first. Reads the .meta sidecars
    /// only — never touches the encrypted .cert blob, so this is fast.
    /// </summary>
    public IReadOnlyList<StoredCertMeta> List()
    {
        var results = new List<StoredCertMeta>();
        if (!Directory.Exists(_vaultDir)) return results;

        foreach (var path in Directory.EnumerateFiles(_vaultDir, "*.meta"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var meta = JsonSerializer.Deserialize<StoredCertMeta>(json, JsonOpts);
                if (meta is not null) results.Add(meta);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable meta file {Path}", path);
            }
        }
        return results.OrderByDescending(m => m.UploadedAtUtc).ToList();
    }

    /// <summary>
    /// Permanently delete a cert by id. Idempotent — no-op if already gone.
    /// </summary>
    public void Delete(string id)
    {
        var metaPath = Path.Combine(_vaultDir, id + ".meta");
        var certPath = Path.Combine(_vaultDir, id + ".cert");
        if (File.Exists(metaPath)) File.Delete(metaPath);
        if (File.Exists(certPath)) File.Delete(certPath);
        _logger.LogInformation("Deleted cert {Id}", id);
    }

    private static void TrySetOwnerOnlyPermissions(string dir)
    {
        // Best-effort: Unix-style chmod 700. On Windows this is a no-op (NTFS
        // ACL inheritance from the user profile already restricts access).
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch
        {
            // Ignore — permission set is a defense-in-depth nicety, not load-bearing.
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    // Internal blob layout — never written to disk in plaintext.
    private sealed record EncryptedCertBlob(byte[] PfxBytes, string Password);
}

/// <summary>
/// Plaintext metadata sidecar. Safe to leave on disk in the clear — no key
/// material, just identifying info shown in the vault list.
/// </summary>
public sealed record StoredCertMeta(
    string Id,
    string FriendlyName,
    string Subject,
    string Issuer,
    string SerialNumber,
    string Thumbprint,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    string KeyAlgorithm,
    int KeySize,
    bool IsSelfSigned,
    DateTimeOffset UploadedAtUtc)
{
    public bool IsExpired => NotAfterUtc < DateTime.UtcNow;
    public double DaysUntilExpiry => (NotAfterUtc - DateTime.UtcNow).TotalDays;
}
