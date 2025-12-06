using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Services;

public class CallSessionStore
{
    private const string SessionsPartitionKey = "call-sessions";
    private const string ParticipantsTableName = "CallParticipants";
    private const string SessionsTableName = "CallSessions";

    private readonly ConcurrentDictionary<Guid, CallSession> _sessions = new();
    private readonly TableClient? _sessionsTable;
    private readonly TableClient? _participantsTable;
    private readonly ILogger _logger;

    public CallSessionStore(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CallSessionStore>();
        var connectionString = configuration.GetConnectionString("Storage")
            ?? configuration["Storage__ConnectionString"]
            ?? configuration["AzureWebJobsStorage"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Storage connection string missing; call sessions will not persist across restarts.");
            return;
        }

        try
        {
            _sessionsTable = new TableClient(connectionString, SessionsTableName);
            _participantsTable = new TableClient(connectionString, ParticipantsTableName);
            _sessionsTable.CreateIfNotExists();
            _participantsTable.CreateIfNotExists();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize storage tables for call sessions; falling back to in-memory only.");
        }
    }

    public async Task<CallSession> CreateAsync(
        string startedByDemoUserId,
        string acsGroupId,
        IReadOnlyList<CallParticipant> participants,
        string? callConnectionId = null,
        CancellationToken cancellationToken = default)
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
        await PersistSessionAsync(session, cancellationToken);
        await PersistParticipantsAsync(session.Id, participants, cancellationToken);
        return session;
    }

    public async Task<CallSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var loaded = await LoadSessionAsync(id, cancellationToken);
        return loaded;
    }

    public async Task<CallSession?> FindByAcsGroupIdAsync(string acsGroupId, CancellationToken cancellationToken = default)
    {
        var cached = _sessions.Values.FirstOrDefault(
            s => string.Equals(s.AcsGroupId, acsGroupId, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            return cached;
        }

        if (_sessionsTable is null)
        {
            return null;
        }

        try
        {
            var filter = $"PartitionKey eq '{SessionsPartitionKey}' and AcsGroupId eq '{acsGroupId}'";
            var entity = _sessionsTable.Query<TableEntity>(filter, cancellationToken: cancellationToken).FirstOrDefault();
            return entity is null ? null : await LoadSessionAsync(Guid.Parse(entity.RowKey), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query session by ACS group id {AcsGroupId}", acsGroupId);
            return null;
        }
    }

    public async Task<CallSession?> FindByCallConnectionIdAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        var cached = _sessions.Values.FirstOrDefault(
            s => string.Equals(s.CallConnectionId, callConnectionId, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            return cached;
        }

        if (_sessionsTable is null)
        {
            return null;
        }

        try
        {
            var filter = $"PartitionKey eq '{SessionsPartitionKey}' and CallConnectionId eq '{callConnectionId}'";
            var entity = _sessionsTable.Query<TableEntity>(filter, cancellationToken: cancellationToken).FirstOrDefault();
            return entity is null ? null : await LoadSessionAsync(Guid.Parse(entity.RowKey), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query session by call connection id {CallConnectionId}", callConnectionId);
            return null;
        }
    }

    public async Task<CallSession?> FindPendingConnectionAsync(CancellationToken cancellationToken = default)
    {
        var cached = _sessions.Values.FirstOrDefault(
            s => string.IsNullOrWhiteSpace(s.CallConnectionId)
                && (string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Status, "Connecting", StringComparison.OrdinalIgnoreCase)));
        if (cached is not null)
        {
            return cached;
        }

        if (_sessionsTable is null)
        {
            return null;
        }

        try
        {
            var filter = $"PartitionKey eq '{SessionsPartitionKey}' and (Status eq 'Active' or Status eq 'Connecting')";
            var entity = _sessionsTable.Query<TableEntity>(filter, cancellationToken: cancellationToken).FirstOrDefault();
            return entity is null ? null : await LoadSessionAsync(Guid.Parse(entity.RowKey), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query pending connection session");
            return null;
        }
    }

    public async Task<CallSession?> AddParticipantsAsync(
        Guid callSessionId,
        IReadOnlyCollection<CallParticipant> participants,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(callSessionId, cancellationToken);
        if (existing is null)
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
        await PersistParticipantsAsync(callSessionId, merged, cancellationToken);
        await PersistSessionAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<CallSession?> SetCallConnectionAsync(Guid callSessionId, string callConnectionId, CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(callSessionId, existing => existing with { CallConnectionId = callConnectionId }, cancellationToken);
    }

    public async Task<CallSession?> UpdateStatusAsync(Guid callSessionId, string status, DateTime? endedAtUtc = null, CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(callSessionId, existing => existing with
        {
            Status = status,
            EndedAtUtc = endedAtUtc ?? existing.EndedAtUtc
        }, cancellationToken);
    }

    public async Task<CallSession?> MarkTranscriptionStartedAsync(Guid callSessionId, CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(callSessionId, existing => existing with
        {
            TranscriptionStartedAtUtc = existing.TranscriptionStartedAtUtc ?? DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task<CallSession?> UpdateAsync(Guid callSessionId, Func<CallSession, CallSession> updater, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(callSessionId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var updated = updater(existing);
        _sessions[callSessionId] = updated;
        await PersistSessionAsync(updated, cancellationToken);
        return updated;
    }

    private async Task PersistSessionAsync(CallSession session, CancellationToken cancellationToken)
    {
        if (_sessionsTable is null)
        {
            return;
        }

        try
        {
            var entity = new TableEntity(SessionsPartitionKey, session.Id.ToString())
            {
                { "AcsGroupId", session.AcsGroupId },
                { "StartedAtUtc", session.StartedAtUtc },
                { "StartedByDemoUserId", session.StartedByDemoUserId },
                { "Status", session.Status },
                { "CallConnectionId", session.CallConnectionId ?? string.Empty },
                { "EndedAtUtc", session.EndedAtUtc },
                { "TranscriptionStartedAtUtc", session.TranscriptionStartedAtUtc }
            };

            await _sessionsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist call session {CallSessionId}", session.Id);
        }
    }

    private async Task PersistParticipantsAsync(Guid callSessionId, IReadOnlyCollection<CallParticipant> participants, CancellationToken cancellationToken)
    {
        if (_participantsTable is null)
        {
            return;
        }

        try
        {
            foreach (var participant in participants)
            {
                var entity = new TableEntity(callSessionId.ToString(), participant.Id.ToString())
                {
                    { "DemoUserId", participant.DemoUserId },
                    { "DisplayName", participant.DisplayName },
                    { "AcsIdentity", participant.AcsIdentity }
                };

                await _participantsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist participants for call {CallSessionId}", callSessionId);
        }
    }

    private async Task<CallSession?> LoadSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_sessionsTable is null)
        {
            return null;
        }

        try
        {
            var entity = await _sessionsTable.GetEntityIfExistsAsync<TableEntity>(SessionsPartitionKey, id.ToString(), cancellationToken: cancellationToken);
            if (!entity.HasValue)
            {
                return null;
            }

            var sessionEntity = entity.Value;
            var participants = await LoadParticipantsAsync(id, cancellationToken);
            var session = new CallSession(
                id,
                sessionEntity.GetString("AcsGroupId") ?? string.Empty,
                sessionEntity.GetDateTime("StartedAtUtc")?.ToUniversalTime() ?? DateTime.UtcNow,
                sessionEntity.GetString("StartedByDemoUserId") ?? string.Empty,
                sessionEntity.GetString("Status") ?? "Active",
                participants,
                sessionEntity.GetString("CallConnectionId"),
                sessionEntity.GetDateTime("EndedAtUtc")?.ToUniversalTime(),
                sessionEntity.GetDateTime("TranscriptionStartedAtUtc")?.ToUniversalTime());

            _sessions[id] = session;
            return session;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load call session {CallSessionId} from storage", id);
            return null;
        }
    }

    private async Task<IReadOnlyList<CallParticipant>> LoadParticipantsAsync(Guid callSessionId, CancellationToken cancellationToken)
    {
        if (_participantsTable is null)
        {
            return Array.Empty<CallParticipant>();
        }

        try
        {
            var query = _participantsTable.QueryAsync<TableEntity>(p => p.PartitionKey == callSessionId.ToString(), cancellationToken: cancellationToken);
            var list = new List<CallParticipant>();
            await foreach (var entity in query)
            {
                var id = Guid.TryParse(entity.RowKey, out var parsed) ? parsed : Guid.NewGuid();
                var demoUserId = entity.GetString("DemoUserId") ?? string.Empty;
                var displayName = entity.GetString("DisplayName") ?? demoUserId;
                var acsIdentity = entity.GetString("AcsIdentity") ?? string.Empty;
                list.Add(new CallParticipant(id, demoUserId, displayName, acsIdentity));
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load participants for call {CallSessionId}", callSessionId);
            return Array.Empty<CallParticipant>();
        }
    }

    public async Task<IReadOnlyList<CallSession>> FindStaleActiveSessionsAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        if (_sessionsTable is null)
        {
            return Array.Empty<CallSession>();
        }

        try
        {
            var timestampFilter = olderThanUtc.ToString("o");
            var filter = $"PartitionKey eq '{SessionsPartitionKey}' and (Status eq 'Active' or Status eq 'Connecting') and StartedAtUtc lt datetime'{timestampFilter}'";
            var results = new List<CallSession>();
            var query = _sessionsTable.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken);
            await foreach (var entity in query)
            {
                if (!Guid.TryParse(entity.RowKey, out var id))
                {
                    continue;
                }
                var participants = await LoadParticipantsAsync(id, cancellationToken);
                results.Add(new CallSession(
                    id,
                    entity.GetString("AcsGroupId") ?? string.Empty,
                    entity.GetDateTime("StartedAtUtc")?.ToUniversalTime() ?? DateTime.UtcNow,
                    entity.GetString("StartedByDemoUserId") ?? string.Empty,
                    entity.GetString("Status") ?? "Active",
                    participants,
                    entity.GetString("CallConnectionId"),
                    entity.GetDateTime("EndedAtUtc")?.ToUniversalTime(),
                    entity.GetDateTime("TranscriptionStartedAtUtc")?.ToUniversalTime()));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query stale active sessions");
            return Array.Empty<CallSession>();
        }
    }
}
