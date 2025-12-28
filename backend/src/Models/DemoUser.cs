namespace CallTranscription.Functions.Models;

public record DemoUser(string Id, string DisplayName, string Role, string? AcsIdentity = null);
