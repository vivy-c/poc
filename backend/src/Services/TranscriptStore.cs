using System.Collections.Concurrent;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class TranscriptStore
{
    private readonly ConcurrentDictionary<Guid, List<TranscriptSegment>> _segmentsByCall = new();

    public IReadOnlyList<TranscriptSegment> Add(Guid callSessionId, IEnumerable<TranscriptSegment> segments)
    {
        var list = _segmentsByCall.GetOrAdd(callSessionId, _ => new List<TranscriptSegment>());
        lock (list)
        {
            list.AddRange(segments);
            return list
                .OrderBy(s => s.OffsetSeconds ?? 0)
                .ThenBy(s => s.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<TranscriptSegment> GetByCallSession(Guid callSessionId)
    {
        if (!_segmentsByCall.TryGetValue(callSessionId, out var list))
        {
            return Array.Empty<TranscriptSegment>();
        }

        lock (list)
        {
            return list
                .OrderBy(s => s.OffsetSeconds ?? 0)
                .ThenBy(s => s.CreatedAtUtc)
                .ToList();
        }
    }
}
