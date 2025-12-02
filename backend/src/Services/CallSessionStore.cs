using System.Collections.Concurrent;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class CallSessionStore
{
    private readonly ConcurrentDictionary<Guid, CallSession> _sessions = new();

    public CallSession Create(
        string startedByDemoUserId,
        string acsGroupId,
        IReadOnlyList<CallParticipant> participants,
        string? callConnectionId = null)
    {
        var session = new CallSession(
            Guid.NewGuid(),
            acsGroupId,
            DateTime.UtcNow,
            startedByDemoUserId,
            Status: "Active",
            participants,
            callConnectionId);

        _sessions[session.Id] = session;
        return session;
    }

    public CallSession? Get(Guid id)
    {
        return _sessions.TryGetValue(id, out var session) ? session : null;
    }

    public CallSession? AddParticipants(Guid callSessionId, IReadOnlyCollection<CallParticipant> participants)
    {
        if (!_sessions.TryGetValue(callSessionId, out var existing))
        {
            return null;
        }

        var merged = existing.Participants.ToList();
        var existingDemoUserIds = new HashSet<string>(
            existing.Participants.Select(p => p.DemoUserId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var participant in participants)
        {
            if (existingDemoUserIds.Add(participant.DemoUserId))
            {
                merged.Add(participant);
            }
        }

        var updated = existing with { Participants = merged };
        _sessions[callSessionId] = updated;
        return updated;
    }
}
