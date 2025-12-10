namespace CallTranscription.Functions.Models;

public record CallSummary(
    Guid Id,
    Guid CallSessionId,
    string Summary,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> ActionItems,
    DateTimeOffset GeneratedAtUtc,
    string Source = "fallback");
