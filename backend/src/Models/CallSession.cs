namespace CallTranscription.Functions.Models;

public record CallSession(
    Guid Id,
    string AcsGroupId,
    DateTime StartedAtUtc,
    string StartedByDemoUserId,
    string Status,
    IReadOnlyList<CallParticipant> Participants);
