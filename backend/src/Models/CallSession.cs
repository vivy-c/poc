namespace CallTranscription.Functions.Models;

public record CallSession(
    Guid Id,
    string AcsGroupId,
    DateTime StartedAtUtc,
    string StartedByDemoUserId,
    string Status,
    IReadOnlyList<CallParticipant> Participants,
    string? CallConnectionId = null,
    DateTime? EndedAtUtc = null,
    DateTime? TranscriptionStartedAtUtc = null)
{
    public string OperationContext => Id.ToString();
}
