using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Stampd.Tools.VaultByokImport;

/// <summary>
/// Native .NET port of <c>internal/scripts/openbao-byok-import.py</c>. Imports an existing
/// RSA private key from a local PKCS#12 file into HashiCorp Vault / OpenBao's Transit
/// secrets engine using the BYOK wrapping ceremony.
/// </summary>
/// <remarks>
/// <para>
/// Why a native port? The Python script works fine but introduces a Python toolchain
/// dependency for adopters — install <c>cryptography</c> + <c>requests</c>, manage a venv
/// or break system packages, deal with PEP-668 surprises. A C# port runs anywhere a
/// .NET 10 SDK is installed, which is already a prerequisite for building Stampd.
/// </para>
/// <para>
/// Ceremony (per Vault docs):
/// </para>
/// <list type="number">
///   <item>Fetch Vault's wrapping public key (RSA-4096) from <c>/v1/transit/wrapping_key</c>.</item>
///   <item>Generate an ephemeral AES-256 key.</item>
///   <item>Export the target private key to PKCS#8 DER.</item>
///   <item>Wrap the target key with the AES key via AES-KWP (RFC 5649).</item>
///   <item>Wrap the AES key with Vault's public key via RSA-OAEP-SHA256.</item>
///   <item>POST <c>wrapped_aes || wrapped_target</c> (base64) to <c>/v1/transit/keys/{name}/import</c>.</item>
/// </list>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        _ = args;

        var vaultAddr = (Env("VAULT_ADDR", "http://127.0.0.1:8200") ?? string.Empty).TrimEnd('/');
        var vaultToken = Env("VAULT_TOKEN")
            ?? Fail("required environment variable not set: VAULT_TOKEN");
        var pfxPath = ExpandHome(Env("STAMPD_PFX",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".stampd", "spike-signer.pfx"))!);
        var pfxPassword = Env("STAMPD_PFX_PASSWORD", "stampd-spike")!;
        var transitMount = (Env("STAMPD_TRANSIT_MOUNT", "transit") ?? "transit").Trim('/');
        var targetKey = Env("STAMPD_TRANSIT_KEY", "stampd-byok-signer")!;

        if (!File.Exists(pfxPath))
        {
            Console.Error.WriteLine($"PKCS#12 file not found: {pfxPath}");
            return 1;
        }

        Console.WriteLine($"[1/7] Loading private key from {pfxPath}");

        // macOS-specific gotcha: when .NET loads a PFX without explicit storage flags,
        // the private key gets parked in the system Keychain with non-exportable
        // attributes, and ExportPkcs8PrivateKey throws "The key does not permit being
        // exported." Exportable unlocks the export call at step [4/7]. EphemeralKeySet
        // would be cleaner but isn't supported by X509CertificateLoader on macOS.
        // PFXs are written to a temp file by the Keychain importer and cleaned up when
        // the X509Certificate2 is disposed.
        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            pfxPassword,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
        using var rsa = pfx.GetRSAPrivateKey()
            ?? Fail<RSA>("PKCS#12 does not contain an RSA private key (ECDSA / DSA not supported).");

        var bits = rsa.KeySize;
        if (bits is not (2048 or 3072 or 4096))
        {
            Console.Error.WriteLine($"unsupported RSA key size: {bits}");
            return 1;
        }

        var transitType = $"rsa-{bits.ToString(CultureInfo.InvariantCulture)}";
        Console.WriteLine($"       loaded RSA-{bits} private key");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);

        Console.WriteLine($"[2/7] Fetching wrapping key from {vaultAddr}/v1/{transitMount}/wrapping_key");
        var wrappingPem = await FetchWrappingKeyPemAsync(http, $"{vaultAddr}/v1/{transitMount}/wrapping_key")
            .ConfigureAwait(false);
        using var wrappingRsa = LoadRsaPublicKeyFromPem(wrappingPem);
        Console.WriteLine($"       wrapping key is RSA-{wrappingRsa.KeySize}");

        Console.WriteLine("[3/7] Generating ephemeral AES-256 key");
        var ephemeralAes = RandomNumberGenerator.GetBytes(32);

        Console.WriteLine("[4/7] Exporting target private key to PKCS#8 DER");
        var targetPkcs8 = rsa.ExportPkcs8PrivateKey();

        Console.WriteLine("[5/7] Wrapping target key with AES-KWP (RFC 5649)");
        var wrappedTarget = AesKeyWrapWithPadding(ephemeralAes, targetPkcs8);

        Console.WriteLine("[6/7] Wrapping AES key with RSA-OAEP-SHA256");
        var wrappedAes = wrappingRsa.Encrypt(ephemeralAes, RSAEncryptionPadding.OaepSHA256);

        var ciphertext = new byte[wrappedAes.Length + wrappedTarget.Length];
        Buffer.BlockCopy(wrappedAes, 0, ciphertext, 0, wrappedAes.Length);
        Buffer.BlockCopy(wrappedTarget, 0, ciphertext, wrappedAes.Length, wrappedTarget.Length);
        var ciphertextB64 = Convert.ToBase64String(ciphertext);

        var importUrl = $"{vaultAddr}/v1/{transitMount}/keys/{targetKey}/import";
        Console.WriteLine($"[7/7] POSTing to {importUrl}");

        var body = new
        {
            ciphertext = ciphertextB64,
            type = transitType,
            hash_function = "SHA256",
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.PostAsync(importUrl, content).ConfigureAwait(false);
        if (response.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NoContent))
        {
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.Error.WriteLine(
                $"import failed: HTTP {(int)response.StatusCode}\n{responseBody}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Import succeeded.");
        Console.WriteLine();
        Console.WriteLine("Now update src/Stampd.WebApi/appsettings.Development.json:");
        Console.WriteLine($"    \"Stampd:Sealing:Vault:KeyName\": \"{targetKey}\"");
        Console.WriteLine();
        Console.WriteLine("Then restart the WebApi and run the /api/sign curl. Adobe should accept");
        Console.WriteLine("the resulting PDF because the cert at CertificatePath and the Vault key");
        Console.WriteLine($"named '{targetKey}' now share the same RSA keypair.");
        return 0;
    }

    private static async Task<string> FetchWrappingKeyPemAsync(HttpClient http, string url)
    {
        using var resp = await http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Fail<object>($"failed to fetch wrapping key: HTTP {(int)resp.StatusCode}\n{body}");
        }

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("public_key").GetString()
            ?? Fail<string>("wrapping key response had no public_key field");
    }

    private static RSA LoadRsaPublicKeyFromPem(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    /// <summary>
    /// AES Key Wrap with Padding (RFC 5649), wrapping <paramref name="plaintext"/> with
    /// <paramref name="aesKey"/>. .NET BCL does not ship RFC 5649 directly, so we use
    /// BouncyCastle's <see cref="Rfc5649WrapEngine"/>.
    /// </summary>
    private static byte[] AesKeyWrapWithPadding(byte[] aesKey, byte[] plaintext)
    {
        var engine = new Rfc5649WrapEngine(new AesEngine());
        engine.Init(forWrapping: true, new KeyParameter(aesKey));
        return engine.Wrap(plaintext, 0, plaintext.Length);
    }

    private static string? Env(string key, string? fallback = null)
        => Environment.GetEnvironmentVariable(key) ?? fallback;

    private static string ExpandHome(string path)
        => path.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/', '\\'))
            : path;

    private static T Fail<T>(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(2);
        throw new InvalidOperationException(message); // unreachable
    }

    private static string Fail(string message) => Fail<string>(message);
}
