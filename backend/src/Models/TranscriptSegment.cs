namespace CallTranscription.Functions.Models;

public record TranscriptSegment(
    Guid Id,
    Guid CallSessionId,
    string Text,
    string? SpeakerAcsIdentity,
    string? SpeakerDemoUserId,
    string? SpeakerDisplayName,
    double? OffsetSeconds,
    double? DurationSeconds,
    DateTimeOffset CreatedAtUtc,
    string? Source = null,
    double? Confidence = null,
    string? Sentiment = null,
    string? Language = null,
    string? ResultStatus = null);
