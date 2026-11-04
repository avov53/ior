using System.Windows.Forms;

namespace HappBox;

internal static class Program
{
    internal static bool UpdateRestart {get;private set;}
    internal static bool WindowsAutostart {get;private set;}
    [STAThread]
    private static void Main(string[] args)
    {
        UpdateRestart=args.Contains("--after-update",StringComparer.Ordinal);
        WindowsAutostart=args.Contains("--autostart",StringComparer.Ordinal);
        if(args.Length is 2 or 3&&args[0]=="--hysteria-upload-check"){NativeHysteriaUploadComparison.RunAsync(args[1],args.Length==3?args[2]:null).GetAwaiter().GetResult();return;}
        if(ResourceDiagnosticsDomains(args) is { } domains)
            Environment.SetEnvironmentVariable("IOR_RESOURCE_DIAGNOSTICS",domains);
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
    internal static string? ResourceDiagnosticsDomains(string[] args)
    {
        const string prefix="--resource-diagnostics=";
        var options=args.Where(value=>value.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
        if(options.Length!=1)return null;
        var domains=options[0][prefix.Length..].Split(',').Select(value=>value.Trim().TrimEnd('.').ToLowerInvariant()).ToArray();
        if(domains.Length is <1 or >8)return null;
        foreach(var name in domains){
            if(name.Length>253 || !name.Contains('.') || System.Net.IPAddress.TryParse(name,out _))return null;
            if(name.Split('.').Any(label=>label.Length is <1 or >63 || label.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-')))return null;
        }
        return string.Join(',',domains);
    }
}
