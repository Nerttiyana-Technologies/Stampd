using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Stampd.Core.Sealing;

namespace Stampd.Timestamp.Rfc3161;

/// <summary>
/// Wraps an ordered list of <see cref="ITimestampAuthorityProvider"/> instances and tries
/// them sequentially until one succeeds. Use this to give a deployment a primary TSA plus
/// one or more failovers — e.g. a free public TSA as primary with a paid commercial TSA
/// as backstop, or a customer's internal AD CS as primary with DigiCert as failover.
/// </summary>
/// <remarks>
/// <para>
/// Failure modes that trigger a fallback to the next provider:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="HttpRequestException"/> — TSA unreachable, DNS failure, TCP refused, TLS error</description></item>
///   <item><description><see cref="TaskCanceledException"/> / <see cref="OperationCanceledException"/> — request timeout (network hang)</description></item>
///   <item><description><see cref="InvalidOperationException"/> — TSA returned a non-granted status</description></item>
///   <item><description>Any other unexpected exception — to maximise demo/prod uptime when a fallback is available</description></item>
/// </list>
/// <para>
/// The cancellation token is honoured at each provider boundary: if the caller cancels
/// mid-failover, we stop trying further providers immediately and surface the cancellation
/// — the only exception type that never triggers a fallback.
/// </para>
/// <para>
/// Logging is done through source-generated <see cref="LoggerMessage"/> delegates
/// (<see cref="FailoverLogs"/>) so allocations are kept to zero on the hot path. Pass an
/// <see cref="ILogger{TCategoryName}"/> to surface per-attempt failures + the final
/// aggregate; the provider works fine without one.
/// </para>
/// </remarks>
public sealed class FailoverTimestampAuthorityProvider : ITimestampAuthorityProvider
{
    private readonly IReadOnlyList<ITimestampAuthorityProvider> _providers;
    private readonly ILogger<FailoverTimestampAuthorityProvider> _logger;

    public FailoverTimestampAuthorityProvider(
        IReadOnlyList<ITimestampAuthorityProvider> providers,
        ILogger<FailoverTimestampAuthorityProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException(
                "Failover provider requires at least one wrapped provider.",
                nameof(providers));
        }
        _providers = providers;
        _logger = logger ?? NullLogger<FailoverTimestampAuthorityProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => string.Join(
        " → ",
        _providers.Select(p => p.Name));

    /// <inheritdoc />
    public async Task<byte[]> RequestTimestampAsync(
        byte[] dataToTimestamp,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<TsaAttemptFailure>(capacity: _providers.Count);

        for (var i = 0; i < _providers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var provider = _providers[i];
            var index = i + 1;
            var total = _providers.Count;
            try
            {
                FailoverLogs.AttemptingRequest(_logger, index, total, provider.Name);
                var token = await provider
                    .RequestTimestampAsync(dataToTimestamp, hashAlgorithm, cancellationToken)
                    .ConfigureAwait(false);
                if (i > 0)
                {
                    FailoverLogs.SucceededAfterFailover(_logger, index, total, provider.Name, i);
                }
                else
                {
                    FailoverLogs.SucceededOnPrimary(_logger, index, total, provider.Name, token.Length);
                }
                return token;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                FailoverLogs.CallerCancelled(_logger, index, total, provider.Name);
                throw;
            }
            catch (Exception ex)
            {
                // Anything else: classify, record, log with stack trace, and try
                // the next provider. We deliberately catch the base Exception so
                // an unexpected failure mode in one TSA doesn't deny service when
                // a working fallback is configured. Caller cancellation is
                // filtered out above so we never mask user intent.
                var reason = ClassifyFailure(ex);
                failures.Add(new TsaAttemptFailure(provider.Name, reason, ex.GetType().Name + ": " + ex.Message));
                FailoverLogs.AttemptFailed(_logger, ex, index, total, provider.Name, reason);
            }
        }

        // All providers exhausted. Build + log + throw the aggregated exception
        // so the operator has a single line that says exactly which TSA failed
        // for which reason — drops cleanly into a log file or stderr.
        var summary = string.Join("  |  ",
            failures.Select(f => $"{f.ProviderName} → {f.Reason}: {Truncate(f.Detail, 200)}"));
        FailoverLogs.AllProvidersExhausted(_logger, _providers.Count, summary);
        throw new InvalidOperationException(
            $"All {_providers.Count.ToString(CultureInfo.InvariantCulture)} TSA providers failed.  |  {summary}");
    }

    /// <summary>
    /// Picks a short, human-readable label for a given failure so the aggregated
    /// error message at the end reads at a glance during a demo.
    /// </summary>
    private static string ClassifyFailure(Exception ex) => ex switch
    {
        TaskCanceledException     => "request timed out",
        OperationCanceledException => "operation cancelled",
        HttpRequestException      => "HTTP error",
        InvalidOperationException => "TSA refused",
        _                         => ex.GetType().Name,
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private readonly record struct TsaAttemptFailure(string ProviderName, string Reason, string Detail);
}

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> delegates for
/// <see cref="FailoverTimestampAuthorityProvider"/>. Defined here so the analyzer-mandated
/// CA1848 pattern is satisfied without scattering generated infrastructure across the file.
/// </summary>
internal static partial class FailoverLogs
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "[TSA {Index}/{Total}: {ProviderName}] attempting timestamp request")]
    public static partial void AttemptingRequest(
        ILogger logger,
        int index,
        int total,
        string providerName);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "[TSA {Index}/{Total}: {ProviderName}] succeeded on primary ({TokenBytes} bytes)")]
    public static partial void SucceededOnPrimary(
        ILogger logger,
        int index,
        int total,
        string providerName,
        int tokenBytes);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "[TSA {Index}/{Total}: {ProviderName}] succeeded after {FailedCount} earlier provider(s) failed — failover worked")]
    public static partial void SucceededAfterFailover(
        ILogger logger,
        int index,
        int total,
        string providerName,
        int failedCount);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "[TSA {Index}/{Total}: {ProviderName}] failed: {Reason}. Will try next provider if available.")]
    public static partial void AttemptFailed(
        ILogger logger,
        Exception ex,
        int index,
        int total,
        string providerName,
        string reason);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "[TSA {Index}/{Total}: {ProviderName}] aborted — caller cancelled the signing operation")]
    public static partial void CallerCancelled(
        ILogger logger,
        int index,
        int total,
        string providerName);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "All {ProviderCount} TSA providers failed.  |  {Summary}")]
    public static partial void AllProvidersExhausted(
        ILogger logger,
        int providerCount,
        string summary);
}
