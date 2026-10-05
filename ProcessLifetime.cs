using System.Diagnostics;
namespace HappBox;
internal static class ProcessLifetime
{
    public static bool IsRunning(Process? process)
    {
        if(process is null)return false;
        try {return !process.HasExited;}
        catch(InvalidOperationException) {return false;}
    }
}
