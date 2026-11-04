using System.Text.Json;
using System.Text.RegularExpressions;

namespace HappBox;

public sealed class NativeStartReport(string directory, string runId, string appVersion, int windowsBuild, string? sha256, string mode, string protocol)
{
    public string Path { get; } = System.IO.Path.Combine(directory, runId + ".json");
    private readonly DateTime _started = DateTime.UtcNow;
    private string _stage = "preflight";
    private string[] _nativeCodes = [];
    public void RecordNativeCodes(string codes) => _nativeCodes = codes.Split(", ").Where(code => Regex.IsMatch(code, @"\A[A-Z][A-Z0-9_]{1,63}\z")).Take(64).ToArray();
    public bool Persisted { get; private set; }
    public void Enter(string stage) { _stage = stage; Save("running", null); }
    public void Complete() { _stage = "ready"; Save("connected", null); }
    public void Fail(Exception error) => Save("failed", error);
    private void Save(string status, Exception? error)
    {
        string? code = null;
        for (var current = error; current is not null; current = current.InnerException)
        {
            var match = Regex.Match(current.Message, @"\b(?:NATIVE|TUN|WINTUN|REALITY|CONTROL|IOR)_[A-Z0-9_]{1,50}\b");
            if (match.Success) { code = match.Value; break; }
        }
        if (error is not null) code ??= "NATIVE_START_FAILED";
        // No exception messages, profiles, URLs or credentials enter the report.
        var json = JsonSerializer.Serialize(new { schema = 1, runId, startedUtc = _started, updatedUtc = DateTime.UtcNow,
            appVersion, windowsBuild, sha256, mode, protocol, stage = _stage, status, code,
            nativeCodes = _nativeCodes, errorType = error?.GetType().Name, hresult = error?.HResult.ToString("X8"),
            innerErrorType = error?.InnerException?.GetType().Name, innerHresult = error?.InnerException?.HResult.ToString("X8") }, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = Path + ".tmp";
            PrivateRuntimeFile.Write(temporary, json);
            File.Move(temporary, Path, overwrite: true);
            Persisted = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Persisted = false; }
    }
}
