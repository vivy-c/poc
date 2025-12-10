using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using Azure.Communication.CallAutomation;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace CallTranscription.Functions.Services;

/// <summary>
/// Lightweight WebSocket listener that receives ACS transcription streaming data and persists segments.
/// </summary>
public class WebSocketTranscriptionService : IHostedService
{
    private readonly SpeechOptions _speechOptions;
    private readonly CallSessionStore _callSessionStore;
    private readonly TranscriptStore _transcriptStore;
    private readonly ILogger<WebSocketTranscriptionService> _logger;
    private WebApplication? _app;
    private Task? _runTask;

    public WebSocketTranscriptionService(
        IOptions<SpeechOptions> speechOptions,
        CallSessionStore callSessionStore,
        TranscriptStore transcriptStore,
        ILoggerFactory loggerFactory)
    {
        _speechOptions = speechOptions.Value;
        _callSessionStore = callSessionStore;
        _transcriptStore = transcriptStore;
        _logger = loggerFactory.CreateLogger<WebSocketTranscriptionService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_speechOptions.TransportUri))
        {
            _logger.LogInformation("Speech transport URI not set; WebSocket transcription listener will not start.");
            return Task.CompletedTask;
        }

        if (!Uri.TryCreate(_speechOptions.TransportUri, UriKind.Absolute, out var transportUri))
        {
            _logger.LogWarning("Invalid Speech__TransportUri '{TransportUri}'; WebSocket transcription listener will not start.", _speechOptions.TransportUri);
            return Task.CompletedTask;
        }

        var listenPort = _speechOptions.WebSocketPort ?? (transportUri.IsDefaultPort ? 8090 : transportUri.Port);
        if (listenPort <= 0)
        {
            listenPort = 8090;
        }

        var listenUrl = $"http://0.0.0.0:{listenPort}";
        var path = string.IsNullOrWhiteSpace(transportUri.AbsolutePath) ? "/ws" : transportUri.AbsolutePath;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { "--urls", listenUrl }
        });

        var app = builder.Build();
        app.UseWebSockets();

        app.Map(path, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleWebSocketAsync(webSocket, cancellationToken);
        });

        _app = app;
        _runTask = app.RunAsync(cancellationToken);

        _logger.LogInformation(
            "WebSocket transcription listener started on {ListenUrl}{Path} (external transport URI={TransportUri})",
            listenUrl,
            path,
            _speechOptions.TransportUri);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
        }

        if (_runTask is not null)
        {
            await _runTask;
        }
    }

    private async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        string? currentCallConnectionId = null;
        Guid? currentCallSessionId = null;
        CallSession? currentSession = null;

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (TryParseMetadata(message, out var metadata))
            {
                currentCallConnectionId = metadata.CallConnectionId;
                currentCallSessionId = await ResolveCallSessionIdAsync(metadata.CallConnectionId, cancellationToken);
                currentSession = currentCallSessionId is null
                    ? null
                    : await _callSessionStore.GetAsync(currentCallSessionId.Value, cancellationToken);
                _logger.LogInformation(
                    "Transcription stream metadata received (callConnectionId={CallConnectionId}, locale={Locale}, session={CallSessionId})",
                    metadata.CallConnectionId,
                    metadata.Locale,
                    currentCallSessionId);
                continue;
            }

            if (TryParseData(message, out var transcriptionData))
            {
                if (currentCallSessionId is null)
                {
                    currentCallSessionId = await ResolveCallSessionIdAsync(currentCallConnectionId, cancellationToken);
                }

                if (currentCallSessionId is null)
                {
                    _logger.LogWarning("TranscriptionData received but call session could not be resolved (callConnectionId={CallConnectionId})", currentCallConnectionId);
                    continue;
                }

                if (currentSession is null || currentSession.Id != currentCallSessionId.Value)
                {
                    currentSession = await _callSessionStore.GetAsync(currentCallSessionId.Value, cancellationToken);
                }

                var speakerAcsIdentity = transcriptionData.ParticipantRawId;
                string? speakerDemoUserId = null;
                string? speakerDisplayName = transcriptionData.ParticipantRawId;

                if (!string.IsNullOrWhiteSpace(speakerAcsIdentity)
                    && currentSession?.Participants is { Count: > 0 })
                {
                    var participant = currentSession.Participants.FirstOrDefault(p =>
                        string.Equals(p.AcsIdentity, speakerAcsIdentity, StringComparison.OrdinalIgnoreCase));
                    if (participant is not null)
                    {
                        speakerDemoUserId = participant.DemoUserId;
                        speakerDisplayName = participant.DisplayName;
                        speakerAcsIdentity = participant.AcsIdentity;
                    }
                }

                var segment = new TranscriptSegment(
                    Guid.NewGuid(),
                    currentCallSessionId.Value,
                    transcriptionData.Text ?? string.Empty,
                    speakerAcsIdentity,
                    speakerDemoUserId,
                    speakerDisplayName,
                    transcriptionData.OffsetSeconds,
                    transcriptionData.DurationSeconds,
                    DateTimeOffset.UtcNow,
                    Source: "websocket",
                    Confidence: transcriptionData.Confidence,
                    Sentiment: transcriptionData.Sentiment,
                    Language: transcriptionData.LanguageIdentified,
                    ResultStatus: transcriptionData.ResultStatus ?? transcriptionData.ResultState);

                _transcriptStore.Add(currentCallSessionId.Value, new[] { segment });
            }
        }
    }

    private async Task<Guid?> ResolveCallSessionIdAsync(string? callConnectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callConnectionId))
        {
            return null;
        }

        var session = await _callSessionStore.FindByCallConnectionIdAsync(callConnectionId, cancellationToken);
        return session?.Id;
    }

    private static bool TryParseMetadata(string message, out StreamingMetadata metadata)
    {
        metadata = default!;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement))
            {
                return false;
            }

            var kind = kindElement.GetString();
            if (!string.Equals(kind, "TranscriptionMetadata", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("transcriptionMetadata", out var metaElement))
            {
                return false;
            }

            var callConnectionId = metaElement.TryGetProperty("callConnectionId", out var ccid) ? ccid.GetString() : null;
            var locale = metaElement.TryGetProperty("locale", out var loc) ? loc.GetString() : null;
            metadata = new StreamingMetadata(callConnectionId, locale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseData(string message, out StreamingTranscriptionData data)
    {
        data = default!;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement))
            {
                return false;
            }

            var kind = kindElement.GetString();
            if (!string.Equals(kind, "TranscriptionData", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("transcriptionData", out var dataElement))
            {
                return false;
            }

            string? participant = null;
            if (dataElement.TryGetProperty("participantRawID", out var participantElement))
            {
                participant = participantElement.GetString();
            }
            else if (dataElement.TryGetProperty("participantRawId", out var participantElementAlt))
            {
                participant = participantElementAlt.GetString();
            }

            var text = dataElement.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
            var confidence = dataElement.TryGetProperty("confidence", out var confEl) && confEl.TryGetDouble(out var conf) ? conf : (double?)null;
            var offsetSeconds = dataElement.TryGetProperty("offset", out var offEl) && offEl.TryGetInt64(out var off) ? off / 10_000_000d : (double?)null;
            var durationSeconds = dataElement.TryGetProperty("duration", out var durEl) && durEl.TryGetInt64(out var dur) ? dur / 10_000_000d : (double?)null;
            var resultStatus = dataElement.TryGetProperty("resultStatus", out var rsEl) ? rsEl.GetString() : null;
            var language = dataElement.TryGetProperty("languageIdentified", out var langEl) ? langEl.GetString() : null;
            var sentiment = dataElement.TryGetProperty("sentimentAnalysisResult", out var sentEl) && sentEl.ValueKind == JsonValueKind.Object
                && sentEl.TryGetProperty("sentiment", out var sentimentEl)
                    ? sentimentEl.GetString()
                    : null;

            data = new StreamingTranscriptionData(text, participant, offsetSeconds, durationSeconds, confidence, sentiment, language, resultStatus);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record StreamingMetadata(string? CallConnectionId, string? Locale);

    private sealed record StreamingTranscriptionData(
        string? Text,
        string? ParticipantRawId,
        double? OffsetSeconds,
        double? DurationSeconds,
        double? Confidence,
        string? Sentiment,
        string? LanguageIdentified,
        string? ResultStatus,
        string? ResultState = null);
}
