using System.Collections.Concurrent;
using System.Text.Json;

using Stampd.Core.Identity;

namespace Stampd.Identity.Kba;

/// <summary>
/// Adapts <see cref="IKbaProvider"/> to Stampd's <see cref="IIdentityVerificationProvider"/>
/// shape. InitiateAsync produces a verification id that maps to a KBA session id; the user
/// is expected to fetch the question set out-of-band (via a UI / API), then submit answers
/// as a JSON payload to VerifyAsync.
/// </summary>
/// <remarks>
/// <para>
/// The <c>response</c> string passed to <see cref="VerifyAsync"/> is JSON of the shape:
/// </para>
/// <code>
/// [
///   {"QuestionId": "q1", "ChoiceText": "Oak Street"},
///   {"QuestionId": "q2", "ChoiceText": "Austin"},
///   {"QuestionId": "q3", "ChoiceText": "Honda Civic"}
/// ]
/// </code>
/// </remarks>
public sealed class KbaIdentityVerificationProvider : IIdentityVerificationProvider
{
    private readonly IKbaProvider _kba;
    private readonly ConcurrentDictionary<string, KbaSessionMapping> _sessionMap = new();

    public KbaIdentityVerificationProvider(IKbaProvider kba)
    {
        ArgumentNullException.ThrowIfNull(kba);
        _kba = kba;
    }

    /// <inheritdoc />
    public string Name => $"Kba({_kba.Name})";

    /// <inheritdoc />
    public async Task<IdentityVerificationChallenge> InitiateAsync(
        IdentityVerificationSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // The Stampd subject only carries email + display name + optional phone. Real KBA
        // backends need DOB and a home address — adopters wire these into the subject via
        // a custom IIdentityVerificationProvider that gathers the data upstream. For the
        // skeleton, we forward a stub subject so adopters see the shape.
        var kbaSubject = new KbaSubject(
            FullName: subject.DisplayName,
            DateOfBirth: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)),
            LastFourSsn: null,
            HomeAddress: null);

        var questionSet = await _kba.StartAsync(kbaSubject, cancellationToken).ConfigureAwait(false);

        var verificationId = Guid.NewGuid().ToString("N");
        _sessionMap[verificationId] = new KbaSessionMapping(
            KbaSessionId: questionSet.SessionId,
            ExpiresAtUtc: questionSet.ExpiresAtUtc);

        return new IdentityVerificationChallenge(
            verificationId,
            questionSet.ExpiresAtUtc,
            UserVisibleHint:
                $"Answer the {questionSet.Questions.Count} identity-verification questions presented in the signing UI.");
    }

    /// <inheritdoc />
    public async Task<IdentityVerificationResult> VerifyAsync(
        string verificationId,
        string response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        if (!_sessionMap.TryRemove(verificationId, out var mapping))
        {
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Session not found or expired.");
        }

        if (mapping.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Session expired.");
        }

        IReadOnlyList<KbaAnswer> answers;
        try
        {
            answers = JsonSerializer.Deserialize<List<KbaAnswer>>(response)
                ?? throw new InvalidOperationException("KBA answer payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            return new IdentityVerificationResult(
                Succeeded: false,
                FailureReason: $"Malformed answer payload: {ex.Message}");
        }

        var result = await _kba.ScoreAsync(mapping.KbaSessionId, answers, cancellationToken).ConfigureAwait(false);
        return new IdentityVerificationResult(
            Succeeded: result.Passed,
            FailureReason: result.Passed
                ? null
                : $"KBA failed: {result.CorrectAnswers}/{result.TotalQuestions} correct. {result.FailureReason ?? string.Empty}".Trim());
    }

    private sealed record KbaSessionMapping(string KbaSessionId, DateTimeOffset ExpiresAtUtc);
}
