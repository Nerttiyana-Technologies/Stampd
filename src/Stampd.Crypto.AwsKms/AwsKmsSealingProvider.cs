using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.AwsKms;

/// <summary>
/// Signs documents using a key held in AWS Key Management Service. The private key never
/// leaves KMS — we hash locally and submit the digest to the KMS Sign API.
/// </summary>
/// <remarks>
/// <para>
/// KMS asymmetric signing supports RSA_2048 / RSA_3072 / RSA_4096 (and ECC keys, which we
/// don't yet handle here — Adobe AATL practical compatibility is RSA-only for the moment).
/// We send <c>MessageType.DIGEST</c> with PKCS#1 v1.5 padding to match the CMS signatures
/// Adobe accepts.
/// </para>
/// <para>
/// KMS doesn't store the X.509 cert wrapper alongside the key. Adopters supply the matching
/// public cert via <see cref="AwsKmsSealingOptions.CertificatePath"/>; this is typically a
/// cert issued by a CA after submitting a CSR generated from the KMS public key (via
/// <c>aws kms get-public-key</c>).
/// </para>
/// </remarks>
public sealed class AwsKmsSealingProvider : ICryptographicSealingProvider, IDisposable
{
    private readonly AwsKmsSealingOptions _options;
    private readonly IAmazonKeyManagementService _kms;
    private readonly bool _ownsClient;
    private readonly Lazy<X509Certificate2> _certificate;

    public AwsKmsSealingProvider(
        AwsKmsSealingOptions options,
        IAmazonKeyManagementService? kmsClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;

        if (kmsClient is null)
        {
            _kms = options.Region is null
                ? new AmazonKeyManagementServiceClient()
                : new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(options.Region));
            _ownsClient = true;
        }
        else
        {
            _kms = kmsClient;
            _ownsClient = false;
        }

        _certificate = new Lazy<X509Certificate2>(LoadCertificate);
    }

    /// <inheritdoc />
    public string Name => "AwsKms";

    /// <inheritdoc />
    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_certificate.Value);

    /// <inheritdoc />
    public async Task<byte[]> SignAsync(
        byte[] data,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // KMS Sign with MessageType=DIGEST takes a pre-computed hash. PdfSharp + BouncyCastle
        // hand us the bytes to sign and expect raw signature output; KMS speaks the same
        // protocol with the RSASSA_PKCS1_V1_5_* algorithms.
        var digest = ComputeDigest(data, hashAlgorithm);

        var request = new SignRequest
        {
            KeyId = _options.KeyId,
            Message = new MemoryStream(digest),
            MessageType = MessageType.DIGEST,
            SigningAlgorithm = MapToKmsSigningAlgorithm(hashAlgorithm),
        };

        var response = await _kms.SignAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Signature.ToArray();
    }

    private X509Certificate2 LoadCertificate()
    {
        var path = _options.CertificatePath!;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"AWS KMS signing certificate not found at '{path}'. " +
                "Provide the public X.509 cert matching the KMS key via AwsKmsSealingOptions.CertificatePath.",
                path);
        }

#pragma warning disable SYSLIB0057 // X509Certificate2 ctor — accept .pem, .cer, .crt by content sniffing.
        return new X509Certificate2(path);
#pragma warning restore SYSLIB0057
    }

    private static byte[] ComputeDigest(byte[] data, HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256) return SHA256.HashData(data);
        if (algorithm == HashAlgorithmName.SHA384) return SHA384.HashData(data);
        if (algorithm == HashAlgorithmName.SHA512) return SHA512.HashData(data);
        throw new NotSupportedException($"Unsupported hash algorithm '{algorithm.Name}'.");
    }

    private static SigningAlgorithmSpec MapToKmsSigningAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256) return SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_256;
        if (algorithm == HashAlgorithmName.SHA384) return SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_384;
        if (algorithm == HashAlgorithmName.SHA512) return SigningAlgorithmSpec.RSASSA_PKCS1_V1_5_SHA_512;
        throw new NotSupportedException($"Unsupported hash algorithm '{algorithm.Name}'.");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _kms.Dispose();
        }
    }
}
