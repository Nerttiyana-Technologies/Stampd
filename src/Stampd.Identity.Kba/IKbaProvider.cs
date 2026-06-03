namespace Stampd.Identity.Kba;

/// <summary>
/// Backend for knowledge-based authentication — out-of-wallet questions sourced from
/// public records (former addresses, vehicle ownership, mortgage history). Implementations
/// wrap LexisNexis, Experian, Equifax, ID.me, etc. The interface stays narrow so adopters
/// can plug their preferred KBA vendor without changing identity verification code.
/// </summary>
/// <remarks>
/// <para>
/// KBA is a regulated identity-proofing step in many jurisdictions:
/// </para>
/// <list type="bullet">
///   <item>US notarial law (eIDAS-style remote online notarization) often requires KBA.</item>
///   <item>Some healthcare and financial workflows mandate KBA at a configurable strength.</item>
///   <item>EU eIDAS doesn't recognize KBA on its own as a Qualified Electronic Signature
///   identity-proofing step; use it alongside a recognized eID instead.</item>
/// </list>
/// </remarks>
public interface IKbaProvider
{
    /// <summary>Short identifying name recorded into the audit trail.</summary>
    string Name { get; }

    /// <summary>
    /// Fetches a question set for the subject. Implementations typically resolve the
    /// subject to a public-records identity, then pull N questions whose answers are
    /// derivable only from records the subject would know.
    /// </summary>
    Task<KbaQuestionSet> StartAsync(
        KbaSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scores the subject's answers against the question set. Implementations return a
    /// pass/fail decision based on a vendor-defined threshold.
    /// </summary>
    Task<KbaResult> ScoreAsync(
        string sessionId,
        IReadOnlyList<KbaAnswer> answers,
        CancellationToken cancellationToken = default);
}

/// <param name="FullName">Legal name as recorded in public records.</param>
/// <param name="DateOfBirth">DOB used to disambiguate identity matches.</param>
/// <param name="LastFourSsn">
/// Last four of US SSN (or jurisdiction-equivalent identifier). Optional but strongly
/// improves match quality.
/// </param>
/// <param name="HomeAddress">Current home address.</param>
public sealed record KbaSubject(
    string FullName,
    DateOnly DateOfBirth,
    string? LastFourSsn,
    KbaAddress? HomeAddress);

public sealed record KbaAddress(
    string Line1,
    string? Line2,
    string City,
    string Region,
    string PostalCode,
    string CountryCode);

/// <summary>
/// A question set issued at the start of a KBA session.
/// </summary>
public sealed record KbaQuestionSet(
    string SessionId,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<KbaQuestion> Questions);

public sealed record KbaQuestion(
    string QuestionId,
    string Prompt,
    IReadOnlyList<string> Choices);

public sealed record KbaAnswer(string QuestionId, string ChoiceText);

public sealed record KbaResult(
    bool Passed,
    int CorrectAnswers,
    int TotalQuestions,
    string? FailureReason = null);
