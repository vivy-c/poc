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

    public CallSession? FindByAcsGroupId(string acsGroupId)
    {
        return _sessions.Values.FirstOrDefault(
            s => string.Equals(s.AcsGroupId, acsGroupId, StringComparison.OrdinalIgnoreCase));
    }

    public CallSession? FindByCallConnectionId(string callConnectionId)
    {
        return _sessions.Values.FirstOrDefault(
            s => string.Equals(s.CallConnectionId, callConnectionId, StringComparison.OrdinalIgnoreCase));
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

    public CallSession? SetCallConnection(Guid callSessionId, string callConnectionId)
    {
        return Update(callSessionId, existing => existing with { CallConnectionId = callConnectionId });
    }

    public CallSession? UpdateStatus(Guid callSessionId, string status, DateTime? endedAtUtc = null)
    {
        return Update(callSessionId, existing => existing with
        {
            Status = status,
            EndedAtUtc = endedAtUtc ?? existing.EndedAtUtc
        });
    }

    public CallSession? MarkTranscriptionStarted(Guid callSessionId)
    {
        return Update(callSessionId, existing => existing with
        {
            TranscriptionStartedAtUtc = existing.TranscriptionStartedAtUtc ?? DateTime.UtcNow
        });
    }

    private CallSession? Update(Guid callSessionId, Func<CallSession, CallSession> updater)
    {
        if (!_sessions.TryGetValue(callSessionId, out var existing))
        {
            return null;
        }

        var updated = updater(existing);
        _sessions[callSessionId] = updated;
        return updated;
    }
}
