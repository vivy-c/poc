using System.Collections.Concurrent;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class CallSummaryStore
{
    private readonly ConcurrentDictionary<Guid, CallSummary> _summaries = new();

    public CallSummary? GetByCallSession(Guid callSessionId)
    {
        return _summaries.TryGetValue(callSessionId, out var summary) ? summary : null;
    }

    public CallSummary Save(CallSummary summary)
    {
        _summaries[summary.CallSessionId] = summary;
        return summary;
    }
}
