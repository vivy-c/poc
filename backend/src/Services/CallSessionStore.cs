using System.Collections.Concurrent;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class CallSessionStore
{
    private readonly ConcurrentDictionary<Guid, CallSession> _sessions = new();

    public CallSession Create(string startedByDemoUserId, string acsGroupId, IReadOnlyList<CallParticipant> participants)
    {
        var session = new CallSession(
            Guid.NewGuid(),
            acsGroupId,
            DateTime.UtcNow,
            startedByDemoUserId,
            Status: "Active",
            participants);

        _sessions[session.Id] = session;
        return session;
    }

    public CallSession? Get(Guid id)
    {
        return _sessions.TryGetValue(id, out var session) ? session : null;
    }
}
