namespace CallTranscription.Functions.Models;

public record TranscriptSegment(
    Guid Id,
    Guid CallSessionId,
    string Text,
    string? SpeakerDemoUserId,
    string? SpeakerDisplayName,
    double? OffsetSeconds,
    double? DurationSeconds,
    DateTimeOffset CreatedAtUtc,
    string? Source = null);
