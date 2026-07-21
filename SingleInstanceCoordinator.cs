using System.Security.Cryptography;
using System.Text;

namespace HappBox;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly EventWaitHandle _stopEvent = new(false, EventResetMode.ManualReset);
    private readonly string _eventName;
    private bool _ownsMutex;

    public bool IsPrimary { get; }

    public SingleInstanceCoordinator()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ?? AppContext.BaseDirectory).ToUpperInvariant();
        var identity = Environment.UserName + "|" + executable;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var mutexName = $"Local\\ior-{suffix}-mutex";
        _eventName = $"Local\\ior-{suffix}-activate";
        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsPrimary = createdNew;
        _ownsMutex = createdNew;
        if (IsPrimary)
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
    }

    public void SignalPrimary()
    {
        if (IsPrimary) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activation = EventWaitHandle.OpenExisting(_eventName);
                activation.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Listen(Action activate)
    {
        if (!IsPrimary || _activationEvent is null) return;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activationEvent, _stopEvent };
            while (WaitHandle.WaitAny(handles) == 0) activate();
        });
    }

    public void Dispose()
    {
        _stopEvent.Set();
        _activationEvent?.Dispose();
        _stopEvent.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
