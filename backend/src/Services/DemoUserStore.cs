using System.Collections.Concurrent;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class DemoUserStore
{
    private readonly ConcurrentDictionary<string, DemoUser> _demoUsers = new(StringComparer.OrdinalIgnoreCase);

    public DemoUserStore()
    {
        SeedDefaults();
    }

    public IReadOnlyCollection<DemoUser> All => _demoUsers.Values;

    public DemoUser? GetById(string demoUserId)
    {
        return _demoUsers.TryGetValue(demoUserId, out var user) ? user : null;
    }

    public DemoUser AssignAcsIdentity(string demoUserId, string acsIdentity)
    {
        return _demoUsers.AddOrUpdate(
            demoUserId,
            key => throw new InvalidOperationException($"Unknown demo user '{key}'"),
            (_, existing) => existing with { AcsIdentity = acsIdentity });
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
}
