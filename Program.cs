using System.Windows.Forms;

namespace HappBox;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length==3&&args[0]=="--native-reality-benchmark"&&Guid.TryParseExact(args[1],"N",out _)) {
            NativeRealityBenchmark.RunAsync(args[1],args[2]).GetAwaiter().GetResult();return;
        }
        if(args.Length is 2 or 3 && args[0]=="--native-tun-fast-check" && Guid.TryParseExact(args[1],"N",out _))
        {var repetitions=args.Length==3&&int.TryParse(args[2],out var count)?Math.Clamp(count,1,5):1;NativeTunValidator.RunFastChecksPackagedAsync(args[1],repetitions).GetAwaiter().GetResult();return;}
        if(args.Length is 2 or 3 && args[0]=="--native-tun-benchmark" && Guid.TryParseExact(args[1],"N",out _)) {
            NativeTunBenchmark.RunPackagedAsync(args[1],args.Length==3?args[2]:null).GetAwaiter().GetResult();return;
        }
        if (args.Length == 2 && args[0] == "--network-recover" && Guid.TryParseExact(args[1], "N", out _))
        {
            WindowsNetworkCommand.WorkerEnabled=false;
            NetworkRecovery.RunPackagedAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 2 && args[0] == "--native-tun-validate" && Guid.TryParseExact(args[1], "N", out _))
        {
            NativeTunValidator.RunPackagedAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--network-watchdog")
        {
            WindowsNetworkCommand.WorkerEnabled=false;
            try { NetworkWatchdog.RunAsync().GetAwaiter().GetResult(); }
            catch { Environment.ExitCode = 2; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var instance = new SingleInstanceCoordinator();
        if (!instance.IsPrimary)
        {
            instance.SignalPrimary();
            return;
        }

        using var form = new MainForm();
        _ = WindowsNetworkCommand.WarmupAsync().ContinueWith(task=>{_ = task.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        instance.Listen(() =>
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(new Action(form.RestoreFromTray));
        });
        try {Application.Run(form);} finally {WindowsNetworkCommand.ShutdownAsync().GetAwaiter().GetResult();}
    }
}
