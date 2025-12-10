using System.Collections.Concurrent;
using System.Linq;
using Azure.Data.Tables;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Services;

public class DemoUserStore
{
    private const string TableName = "DemoUsers";
    private const string PartitionKey = "demo-users";

    private readonly ConcurrentDictionary<string, DemoUser> _demoUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TableClient? _tableClient;
    private readonly ILogger _logger;

    public DemoUserStore(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<DemoUserStore>();
        _tableClient = CreateTableClient(configuration);
        SeedDefaults();
        LoadFromStorage();
        PersistDefaultsIfNeeded();
    }

    public IReadOnlyCollection<DemoUser> All => _demoUsers.Values.ToList();

    public DemoUser? GetById(string demoUserId)
    {
        return _demoUsers.TryGetValue(demoUserId, out var user) ? user : null;
    }

    public DemoUser AssignAcsIdentity(string demoUserId, string acsIdentity)
    {
        var updated = _demoUsers.AddOrUpdate(
            demoUserId,
            key => throw new InvalidOperationException($"Unknown demo user '{key}'"),
            (_, existing) => existing with { AcsIdentity = acsIdentity });
        Persist(updated);
        return updated;
    }

    private void SeedDefaults()
    {
        var defaults = new[]
        {
            new DemoUser("agent-1", "Avery Chen", "Recruiter"),
            new DemoUser("agent-2", "Jordan Malik", "Hiring Manager"),
            new DemoUser("candidate-1", "Taylor Brooks", "Candidate")
        };

        foreach (var demoUser in defaults)
        {
            _demoUsers.TryAdd(demoUser.Id, demoUser);
        }
    }

    private TableClient? CreateTableClient(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Storage")
            ?? configuration["Storage__ConnectionString"]
            ?? configuration["AzureWebJobsStorage"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Storage connection string missing; demo user identities will not persist across restarts.");
            return null;
        }

        try
        {
            var client = new TableClient(connectionString, TableName);
            client.CreateIfNotExists();
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize table storage for demo users; falling back to in-memory only.");
            return null;
        }
    }

    private void LoadFromStorage()
    {
        if (_tableClient is null)
        {
            return;
        }

        try
        {
            var entities = _tableClient.Query<TableEntity>(e => e.PartitionKey == PartitionKey);
            foreach (var entity in entities)
            {
                var id = entity.RowKey;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                entity.TryGetValue("DisplayName", out var displayNameObj);
                entity.TryGetValue("Role", out var roleObj);
                entity.TryGetValue("AcsIdentity", out var acsIdentityObj);

                var storedDisplayName = displayNameObj as string;
                var storedRole = roleObj as string;
                var storedAcsIdentity = acsIdentityObj as string;

                _demoUsers.AddOrUpdate(
                    id,
                    key => new DemoUser(key, storedDisplayName ?? key, storedRole ?? "Demo", storedAcsIdentity),
                    (_, existing) => existing with
                    {
                        DisplayName = string.IsNullOrWhiteSpace(storedDisplayName) ? existing.DisplayName : storedDisplayName,
                        Role = string.IsNullOrWhiteSpace(storedRole) ? existing.Role : storedRole,
                        AcsIdentity = string.IsNullOrWhiteSpace(storedAcsIdentity) ? existing.AcsIdentity : storedAcsIdentity
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load demo user mappings from table storage; continuing with in-memory defaults.");
        }
    }

    private void Persist(DemoUser demoUser)
    {
        if (_tableClient is null)
        {
            return;
        }

        try
        {
            var entity = new TableEntity(PartitionKey, demoUser.Id)
            {
                { "DisplayName", demoUser.DisplayName },
                { "Role", demoUser.Role },
                { "AcsIdentity", demoUser.AcsIdentity ?? string.Empty }
            };

            _tableClient.UpsertEntity(entity, TableUpdateMode.Replace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist demo user {DemoUserId} to table storage.", demoUser.Id);
        }
    }

    private void PersistDefaultsIfNeeded()
    {
        if (_tableClient is null)
        {
            return;
        }

        foreach (var demoUser in _demoUsers.Values)
        {
            Persist(demoUser);
        }
    }
}
