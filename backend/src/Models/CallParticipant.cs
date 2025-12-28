namespace CallTranscription.Functions.Models;

public record CallParticipant(Guid Id, string DemoUserId, string DisplayName, string AcsIdentity);
