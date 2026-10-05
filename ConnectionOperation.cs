namespace HappBox;

/// <summary>UI-thread-owned operation; terminal state remains available to late subscribers.</summary>
public sealed class ConnectionOperation
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Method { get; }
    public string Status { get; private set; } = "running";
    public string Stage { get; private set; } = "applying";
    public object? Result { get; private set; }
    public string? Error { get; private set; }
    public ConnectionOperation(string method) => Method = method;
    public void Advance(string stage) { if (Status == "running") Stage = stage; }
    public void Complete(object? result) { if (Status != "running") return; Result = result; Status = "succeeded"; Stage = "complete"; }
    public void Fail(string error) { if (Status != "running") return; Error = error; Status = "failed"; Stage = "complete"; }
}
