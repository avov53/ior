namespace HappBox;

public enum TunLoopObservation { None, UnconfirmedBurst, SocketExhausted, ConfirmedRouteLoop }

/// <summary>Detects a burst of direct socket failures without stopping healthy high-volume traffic.</summary>
public sealed class TunLoopGuard
{
    private readonly Queue<DateTime> _bindFailures = new();
    private readonly Queue<(DateTime Time, string Destination)> _directConnections = new();
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate)
        {
            _bindFailures.Clear();
            _directConnections.Clear();
        }
    }

    public TunLoopObservation Record(string line, DateTime utcNow)
    {
        if (!line.Contains("outbound/direct[direct]", StringComparison.OrdinalIgnoreCase))
            return TunLoopObservation.None;

        lock (_gate)
        {
            // Exhausted socket buffers are a concrete system failure. A burst of
            // ordinary connections alone is not proof that packets loop into TUN.
            if (line.Contains("lacked sufficient buffer space", StringComparison.OrdinalIgnoreCase))
                return TunLoopObservation.SocketExhausted;
            if (line.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
                _bindFailures.Enqueue(utcNow);
            while (_bindFailures.Count > 0 && utcNow - _bindFailures.Peek() > TimeSpan.FromSeconds(5))
                _bindFailures.Dequeue();

            const string marker = "outbound connection to ";
            var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                var destination = line[(start + marker.Length)..].Trim();
                _directConnections.Enqueue((utcNow, destination));
            }
            while (_directConnections.Count > 0 && utcNow - _directConnections.Peek().Time > TimeSpan.FromSeconds(5))
                _directConnections.Dequeue();

            if (_bindFailures.Count >= 12) return TunLoopObservation.SocketExhausted;
            if (start < 0 || _directConnections.Count < 48) return TunLoopObservation.None;
            var latest = _directConnections.Last().Destination;
            return _directConnections.Count(item => item.Destination.Equals(latest, StringComparison.OrdinalIgnoreCase)) == 48
                ? TunLoopObservation.UnconfirmedBurst : TunLoopObservation.None;
        }
    }
}
