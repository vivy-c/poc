namespace CallTranscription.Functions.Common;

public class SpeechOptions
{
    public string? Key { get; set; }
    public string? Region { get; set; }
    public string? Endpoint { get; set; }
    public string? Locale { get; set; }
    public string? TransportUri { get; set; }
    public string? SpeechRecognitionModelEndpointId { get; set; }
    public bool StartTranscriptionOnConnect { get; set; }
    public bool EnableIntermediateResults { get; set; } = true;
    public bool EnableSentimentAnalysis { get; set; }
    public bool EnablePiiRedaction { get; set; }
    public string? PiiRedactionType { get; set; } = "MaskWithCharacter";
    public string? SummarizationLocale { get; set; }
    public bool EnableSummarization { get; set; }
    public string? LocalesCsv { get; set; }
    public List<string>? Locales { get; set; }
    public int? WebSocketPort { get; set; }
}
