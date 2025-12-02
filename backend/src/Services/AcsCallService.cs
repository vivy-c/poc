using Azure.Communication;
using Azure.Communication.CallAutomation;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CallParticipantModel = CallTranscription.Functions.Models.CallParticipant;

namespace CallTranscription.Functions.Services;

public class AcsCallService
{
    private readonly CallAutomationClient _callAutomationClient;
    private readonly ILogger _logger;

    public AcsCallService(IOptions<AcsOptions> options, ILoggerFactory loggerFactory)
    {
        var connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ACS connection string is missing (ACS__ConnectionString).");
        }

        _callAutomationClient = new CallAutomationClient(connectionString);
        _logger = loggerFactory.CreateLogger<AcsCallService>();
    }

    public async Task<bool> TryAddParticipantAsync(
        string? callConnectionId,
        CallParticipantModel participant,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(callConnectionId))
        {
            _logger.LogWarning(
                "Call connection id missing; skipping ACS participant add for {ParticipantId}",
                participant.DemoUserId);
            return false;
        }

        try
        {
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);
            var addOptions = new AddParticipantOptions(new CallInvite(new CommunicationUserIdentifier(participant.AcsIdentity)))
            {
                OperationContext = participant.Id.ToString()
            };

            await callConnection.AddParticipantAsync(addOptions, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ACS add participant failed for {ParticipantId} on call {CallConnectionId}",
                participant.DemoUserId,
                callConnectionId);
            return false;
        }
    }
}
