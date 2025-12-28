using Azure.Communication;
using Azure.Communication.Identity;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Options;

namespace CallTranscription.Functions.Services;

public class AcsIdentityService
{
    private readonly CommunicationIdentityClient _identityClient;

    public AcsIdentityService(IOptions<AcsOptions> options)
    {
        var connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ACS connection string is missing (ACS__ConnectionString).");
        }

        _identityClient = new CommunicationIdentityClient(connectionString);
    }

    public async Task<string> EnsureIdentityAsync(DemoUser demoUser, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(demoUser.AcsIdentity))
        {
            return demoUser.AcsIdentity;
        }

        var identity = await _identityClient.CreateUserAsync(cancellationToken);
        return identity.Value.Id;
    }

    public async Task<(string Token, DateTimeOffset ExpiresOn)> IssueVoipTokenAsync(string acsIdentity, CancellationToken cancellationToken = default)
    {
        var tokenResponse = await _identityClient.GetTokenAsync(
            new CommunicationUserIdentifier(acsIdentity),
            scopes: new[] { CommunicationTokenScope.VoIP },
            cancellationToken);

        return (tokenResponse.Value.Token, tokenResponse.Value.ExpiresOn);
    }
}
