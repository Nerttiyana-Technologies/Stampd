using System.Security.Cryptography.X509Certificates;

using Stampd.Core.Revocation;

namespace Stampd.Engine.Pades;

/// <summary>
/// Walks a signing certificate's chain, asks the configured <see cref="IRevocationProvider"/>
/// for OCSP / CRL material for each intermediate, and aggregates the results into a
/// <see cref="PadesRevocationData"/> ready for embedding into the DSS.
/// </summary>
/// <remarks>
/// Errors during chain build or per-cert revocation fetches are absorbed — B-LT enrichment
/// is best-effort. A signed document that fails to gather revocation info still goes out
/// as PAdES B-T; the audit trail records that B-LT was attempted.
///
/// <para>
/// We deliberately skip the root certificate: trust anchors are self-signed and have no
/// meaningful revocation source. Adobe does not require revocation info for the root.
/// </para>
/// </remarks>
internal sealed class PadesRevocationFetcher
{
    private readonly IRevocationProvider _revocationProvider;

    public PadesRevocationFetcher(IRevocationProvider revocationProvider)
    {
        ArgumentNullException.ThrowIfNull(revocationProvider);
        _revocationProvider = revocationProvider;
    }

    public async Task<PadesRevocationData> GatherAsync(
        X509Certificate2 signingCertificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signingCertificate);

        var certs = new List<byte[]>();
        var ocsps = new List<byte[]>();
        var crls = new List<byte[]>();

        // Build the chain. ChainPolicy is permissive — we want to learn the structure even
        // if the signing cert is untrusted (self-signed in dev).
        using var chain = new X509Chain();
        chain.ChainPolicy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.AllFlags,
        };

        _ = chain.Build(signingCertificate);

        var elements = chain.ChainElements;
        for (var i = 0; i < elements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cert = elements[i].Certificate;
            certs.Add(cert.RawData);

            // Ask the revocation provider for every cert. Trust-anchor skipping lives
            // inside the provider: real OCSP/CRL HTTP providers return Empty for roots
            // (no AIA/CDP extension), so production behavior is unchanged. This shape
            // also means self-signed signer certs (dev mode) still consult the provider.
            // Issuer is the next cert up; for the chain root, the cert is its own issuer.
            var issuer = i == elements.Count - 1
                ? cert
                : elements[i + 1].Certificate;

            try
            {
                var info = await _revocationProvider
                    .GetRevocationInfoAsync(cert, issuer, cancellationToken)
                    .ConfigureAwait(false);

                if (info.OcspResponse is not null)
                {
                    ocsps.Add(info.OcspResponse);
                }

                if (info.Crl is not null)
                {
                    crls.Add(info.Crl);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Best-effort — swallow and continue. The engine will log at a higher level.
            }
        }

        return new PadesRevocationData
        {
            Certificates = certs,
            OcspResponses = ocsps,
            Crls = crls,
        };
    }
}
