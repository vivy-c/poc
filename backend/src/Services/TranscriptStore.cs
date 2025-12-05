using System.Collections.Concurrent;
using System.Globalization;
using CallTranscription.Functions.Models;

namespace CallTranscription.Functions.Services;

public class TranscriptStore
{
    private sealed class TranscriptCollection
    {
        public List<TranscriptSegment> Segments { get; } = new();
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<Guid, TranscriptCollection> _segmentsByCall = new();

    public IReadOnlyList<TranscriptSegment> Add(Guid callSessionId, IEnumerable<TranscriptSegment> segments)
    {
        var collection = _segmentsByCall.GetOrAdd(callSessionId, _ => new TranscriptCollection());
        lock (collection)
        {
            foreach (var segment in segments)
            {
                var key = BuildKey(segment);
                if (!collection.Keys.Add(key))
                {
                    continue;
                }

                collection.Segments.Add(segment);
            }

            return collection.Segments
                .OrderBy(s => s.OffsetSeconds ?? 0)
                .ThenBy(s => s.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<TranscriptSegment> GetByCallSession(Guid callSessionId)
    {
        if (!_segmentsByCall.TryGetValue(callSessionId, out var collection))
        {
            return Array.Empty<TranscriptSegment>();
        }

        lock (collection)
        {
            return collection.Segments
                .OrderBy(s => s.OffsetSeconds ?? 0)
                .ThenBy(s => s.CreatedAtUtc)
                .ToList();
        }
    }

    private static string BuildKey(TranscriptSegment segment)
    {
        var speaker = segment.SpeakerAcsIdentity
            ?? segment.SpeakerDemoUserId
            ?? segment.SpeakerDisplayName
            ?? "unknown";

        var offset = segment.OffsetSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "0";
        var duration = segment.DurationSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "0";
        var text = segment.Text?.Trim() ?? string.Empty;

        return $"{speaker}|{offset}|{duration}|{text}".ToLowerInvariant();
    }
}
