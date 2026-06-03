using System.Security.Cryptography.X509Certificates;

using Stampd.Core.Revocation;

namespace Stampd.Revocation.Http;

/// <summary>
/// Tries each inner provider in order, merging the first non-empty results. Default usage:
/// OCSP first, CRL as fallback. Production B-LT pipelines should embed BOTH when available
/// because some Adobe versions prefer one and some prefer the other.
/// </summary>
public sealed class CompositeRevocationProvider : IRevocationProvider
{
    private readonly IReadOnlyList<IRevocationProvider> _inner;
    private readonly bool _continueOnSuccess;

    /// <summary>
    /// Creates a composite that walks <paramref name="providers"/> in order.
    /// </summary>
    /// <param name="providers">Inner providers to consult in priority order.</param>
    /// <param name="continueOnSuccess">
    /// When <see langword="true"/>, every provider is asked and results are merged
    /// (OCSP from one + CRL from another). When <see langword="false"/>, the walk stops
    /// at the first provider that returns any non-empty info. Defaults to
    /// <see langword="true"/> — Adobe historically accepts either OCSP or CRL, but some
    /// validators prefer one over the other, so embedding both is the safer default.
    /// </param>
    public CompositeRevocationProvider(
        IReadOnlyList<IRevocationProvider> providers,
        bool continueOnSuccess = true)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException(
                "CompositeRevocationProvider requires at least one inner provider.",
                nameof(providers));
        }

        _inner = providers;
        _continueOnSuccess = continueOnSuccess;
    }

    /// <inheritdoc />
    public string Name => $"Composite({string.Join("+", _inner.Select(p => p.Name))})";

    /// <inheritdoc />
    public async Task<CertificateRevocationInfo> GetRevocationInfoAsync(
        X509Certificate2 certificate,
        X509Certificate2 issuer,
        CancellationToken cancellationToken = default)
    {
        byte[]? ocsp = null;
        byte[]? crl = null;

        foreach (var provider in _inner)
        {
            var info = await provider
                .GetRevocationInfoAsync(certificate, issuer, cancellationToken)
                .ConfigureAwait(false);

            ocsp ??= info.OcspResponse;
            crl ??= info.Crl;

            var haveAnything = ocsp is not null || crl is not null;
            if (haveAnything && !_continueOnSuccess)
            {
                break;
            }

            // Short-circuit when we have both kinds even in continue-on-success mode.
            if (ocsp is not null && crl is not null)
            {
                break;
            }
        }

        return new CertificateRevocationInfo(ocsp, crl);
    }
}
