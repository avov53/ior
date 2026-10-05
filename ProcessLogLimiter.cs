namespace HappBox;

/// <summary>Bounds routine per-packet INFO output without hiding warnings or errors.</summary>
public sealed class ProcessLogLimiter
{
    private readonly Func<long> _unixSeconds;
    private readonly object _gate = new();
    private long _window = -1;
    private int _routineShown;
    private int _routineSuppressed;
    private int _familyShown;
    private int _familySuppressed;
    private string? _familyExample;

    public ProcessLogLimiter(Func<long>? unixSeconds = null) =>
        _unixSeconds = unixSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public bool TryAccept(string line, out string? summary)
    {
        lock (_gate)
        {
            var window = _unixSeconds();
            summary = null;
            if (window != _window)
            {
                if (_routineSuppressed > 0)
                    summary = $"ior: скрыто {_routineSuppressed} повторяющихся INFO-записей о пакетах за предыдущую секунду";
                if(_familySuppressed>0)summary=(summary is null?"":summary+"; ")+$"ior-core: ADDRESS_FAMILY_UNAVAILABLE · suppressed={_familySuppressed} · first={_familyExample}";
                _window = window;
                _routineShown = 0;
                _routineSuppressed = 0;
                _familyShown=0;_familySuppressed=0;_familyExample=null;
            }

            if(line.StartsWith("ior-core:",StringComparison.Ordinal) && (line.Contains("TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED",StringComparison.Ordinal) || line.Contains("TUNNEL_IPV4_LOCAL_ADDRESS_REQUIRED",StringComparison.Ordinal)))
            {
                _familyExample??=line;
                if(++_familyShown<=3)return true;
                _familySuppressed++;return false;
            }

            if (!IsRoutinePacketLine(line)) return true;
            if (++_routineShown <= 60) return true;
            _routineSuppressed++;
            return false;
        }
    }

    private static bool IsRoutinePacketLine(string line) =>
        line.StartsWith("ior-core: ",StringComparison.Ordinal) &&
        (line.Contains("LOCAL_FLOW_ACCEPTED",StringComparison.Ordinal) || line.Contains("TCP_RELAY_READY",StringComparison.Ordinal) ||
         line.Contains("TCP_RELAY_CLOSED",StringComparison.Ordinal) || line.Contains("ROUTE_SELECTED",StringComparison.Ordinal)) ||
        line.Contains(" INFO ", StringComparison.Ordinal) &&
        (line.Contains("inbound/tun[", StringComparison.Ordinal) ||
         line.Contains("outbound/direct[", StringComparison.Ordinal)) &&
        line.Contains(" packet connection ", StringComparison.Ordinal);
}
