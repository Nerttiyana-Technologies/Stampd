using Microsoft.Extensions.Logging;

using Stampd.Core.Sealing;
using Stampd.Timestamp.Rfc3161;

namespace Stampd.WebApi.Services;

/// <summary>
/// Wraps a primary <see cref="ITimestampAuthorityProvider"/> with a
/// <see cref="FailoverTimestampAuthorityProvider"/> that falls back to DigiCert's
/// free public TSA when the primary throws, times out, or refuses. Used by
/// <see cref="Program"/> when <c>Stampd:Tsa:EnableDigiCertFailover=true</c>.
/// </summary>
/// <remarks>
/// The factory exists so the failover-wrap registration in Program.cs can stay a
/// small DI-resolved factory call instead of a sprawling lambda. It also gives
/// us a single home for the DigiCert <see cref="HttpClient"/> name + the
/// logger plumbing.
/// </remarks>
internal sealed class FailoverTsaWrapperFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FailoverTimestampAuthorityProvider> _failoverLogger;

    public FailoverTsaWrapperFactory(
        IHttpClientFactory httpClientFactory,
        ILogger<FailoverTimestampAuthorityProvider> failoverLogger)
    {
        _httpClientFactory = httpClientFactory;
        _failoverLogger = failoverLogger;
    }

    /// <summary>
    /// Build a failover chain that tries <paramref name="primary"/> first and
    /// DigiCert if the primary fails. Returns a new
    /// <see cref="ITimestampAuthorityProvider"/> the engine can hold as a
    /// singleton.
    /// </summary>
    public ITimestampAuthorityProvider WrapWithDigiCert(ITimestampAuthorityProvider primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var digicertClient = _httpClientFactory.CreateClient("stampd-tsa-failover-digicert");
        var digicert = Rfc3161ServiceCollectionExtensions.CreateDigiCertProvider(digicertClient);
        return new FailoverTimestampAuthorityProvider(
            new[] { primary, digicert },
            _failoverLogger);
    }
}
