using System.Collections.Concurrent;

namespace Stampd.Identity.Kba;

/// <summary>
/// Dev/test KBA backend that asks a fixed set of harmless questions and accepts any
/// non-empty answer. Useful for exercising the end-to-end flow without standing up a
/// LexisNexis / Experian sandbox account.
/// </summary>
/// <remarks>
/// Never deploy this to production — it does not actually verify identity. The dev-only
/// nature is reflected in the Name "MockKba" written to the audit trail.
/// </remarks>
public sealed class MockKbaProvider : IKbaProvider
{
    private readonly ConcurrentDictionary<string, KbaQuestionSet> _sessions = new();

    /// <inheritdoc />
    public string Name => "MockKba";

    /// <inheritdoc />
    public Task<KbaQuestionSet> StartAsync(KbaSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var sessionId = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var questions = new[]
        {
            new KbaQuestion(
                QuestionId: "q1",
                Prompt: "What is the name of the first street you lived on?",
                Choices: ["Pine Lane", "Oak Street", "Maple Avenue", "None of the above"]),
            new KbaQuestion(
                QuestionId: "q2",
                Prompt: "Which of these cities have you previously held a mortgage in?",
                Choices: ["Portland", "Austin", "Boston", "None of the above"]),
            new KbaQuestion(
                QuestionId: "q3",
                Prompt: "Which of these vehicles have you owned?",
                Choices: ["Subaru Outback", "Toyota Prius", "Honda Civic", "None of the above"]),
        };

        var set = new KbaQuestionSet(sessionId, expires, questions);
        _sessions[sessionId] = set;
        return Task.FromResult(set);
    }

    /// <inheritdoc />
    public Task<KbaResult> ScoreAsync(
        string sessionId,
        IReadOnlyList<KbaAnswer> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(answers);

        if (!_sessions.TryGetValue(sessionId, out var set))
        {
            return Task.FromResult(new KbaResult(
                Passed: false,
                CorrectAnswers: 0,
                TotalQuestions: 0,
                FailureReason: "Session not found or expired."));
        }

        if (set.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return Task.FromResult(new KbaResult(
                Passed: false,
                CorrectAnswers: 0,
                TotalQuestions: set.Questions.Count,
                FailureReason: "Session expired."));
        }

        // Mock scoring: any non-"None of the above" answer counts. Useful so devs can
        // exercise both the pass and fail paths in the UI.
        var correct = answers.Count(a =>
            !string.IsNullOrWhiteSpace(a.ChoiceText)
            && !string.Equals(a.ChoiceText, "None of the above", StringComparison.OrdinalIgnoreCase));

        _sessions.TryRemove(sessionId, out _);
        return Task.FromResult(new KbaResult(
            Passed: correct >= set.Questions.Count - 1,
            CorrectAnswers: correct,
            TotalQuestions: set.Questions.Count));
    }
}
