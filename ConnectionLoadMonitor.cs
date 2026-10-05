using System.Diagnostics;

namespace HappBox;

/// <summary>Low-cost, process-local measurements for comparing TUN and Proxy runs.</summary>
public sealed class ConnectionLoadMonitor
{
    private readonly Dictionary<int, (TimeSpan Cpu, long Tick)> _previousCpu = [];
    private long _lastTick;
    private long _rawLogLines;
    private long _tunDnsLines;
    private long _directPacketLines;
    private long _udp443RejectedLines;
    private LoadSample _last = new(null, null, 0, 0, 0, "off", false, 0);

    public void RecordProcessLine(string line)
    {
        Interlocked.Increment(ref _rawLogLines);
        if (IsTunDnsLine(line)) Interlocked.Increment(ref _tunDnsLines);
        if (IsVisionUdp443Rejection(line)) Interlocked.Increment(ref _udp443RejectedLines);
        if (line.Contains("outbound/direct[direct]: outbound packet connection", StringComparison.Ordinal) ||
            line.Contains("[tun-in -> direct]", StringComparison.Ordinal) ||
            line.Contains("[tun-in >> direct]", StringComparison.Ordinal))
            Interlocked.Increment(ref _directPacketLines);
    }

    public LoadSample Sample(int? enginePid, string mode, bool systemProxy)
    {
        var tick = Stopwatch.GetTimestamp();
        if (_lastTick != 0 && Stopwatch.GetElapsedTime(_lastTick, tick) < TimeSpan.FromMilliseconds(900)) return _last;
        var seconds = _lastTick == 0 ? 1d : Math.Max(.001, Stopwatch.GetElapsedTime(_lastTick, tick).TotalSeconds);
        _lastTick = tick;
        _last = new LoadSample(
            CpuPercent(Environment.ProcessId, tick),
            enginePid is > 0 ? CpuPercent(enginePid.Value, tick) : null,
            Math.Round(Interlocked.Exchange(ref _rawLogLines, 0) / seconds, 1),
            Math.Round(Interlocked.Exchange(ref _tunDnsLines, 0) / seconds, 1),
            Math.Round(Interlocked.Exchange(ref _directPacketLines, 0) / seconds, 1),
            mode,
            systemProxy,
            Math.Round(Interlocked.Exchange(ref _udp443RejectedLines, 0) / seconds, 1));
        return _last;
    }

    public static bool IsTunDnsLine(string line) =>
        (line.Contains("inbound/tun", StringComparison.Ordinal) &&
         line.Contains("inbound packet connection to ", StringComparison.Ordinal) &&
         line.TrimEnd().EndsWith(":53", StringComparison.Ordinal)) ||
        (line.Contains("proxy/tun: processing from udp:", StringComparison.Ordinal) &&
         line.Contains(" to udp:", StringComparison.Ordinal) &&
         line.TrimEnd().EndsWith(":53", StringComparison.Ordinal));

    public static bool IsVisionUdp443Rejection(string line) =>
        line.Contains("XTLS rejected UDP/443 traffic", StringComparison.OrdinalIgnoreCase);

    private double? CpuPercent(int pid, long tick)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var cpu = process.TotalProcessorTime;
            if (!_previousCpu.TryGetValue(pid, out var previous))
            {
                _previousCpu[pid] = (cpu, tick);
                return null;
            }
            _previousCpu[pid] = (cpu, tick);
            var seconds = Stopwatch.GetElapsedTime(previous.Tick, tick).TotalSeconds;
            return seconds <= 0 ? null : Math.Round(Math.Clamp((cpu - previous.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100), 1);
        }
        catch (ArgumentException) { _previousCpu.Remove(pid); return null; }
        catch (InvalidOperationException) { _previousCpu.Remove(pid); return null; }
    }
}

public sealed record LoadSample(double? AppCpuPercent, double? EngineCpuPercent,
    double LogLinesPerSecond, double TunDnsLinesPerSecond, double DirectPacketLinesPerSecond,
    string Mode, bool SystemProxy, double Udp443RejectedLinesPerSecond);
