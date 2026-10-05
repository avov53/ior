using System.Diagnostics;
using System.Text.Json;
namespace HappBox;

/// <summary>Explicit, elevated qualification; persisted profiles are read only.</summary>
public static class NativeRealityBenchmark
{
    public static async Task RunAsync(string id,string baseline)
    {
        var directory=NativeTunValidator.DirectoryFor(id);Directory.CreateDirectory(directory);
        var samples=new List<object>();var failures=new List<object>();
        var ready=new Dictionary<string,List<long>>{{"control",[]},{"candidate",[]}};
        async Task Save()=>await File.WriteAllTextAsync(Path.Combine(directory,"reality-comparison.json"),JsonSerializer.Serialize(new {
            scope="20 TUN cycles in an already elevated process; current host probes for both core binaries; excludes UAC interaction and browser rendering",
            samples,failures,targetConfirmed=TargetMet(ready["candidate"],failures.Count),
            status=TargetMet(ready["candidate"],failures.Count)?"startup criterion passed; application acceleration unconfirmed":"цель до 3 секунд / ускорение приложений не подтверждено"
        },new JsonSerializerOptions{WriteIndented=true}));
        try {
            if(!NativeTunValidator.IsAdministrator())throw new IOException("ADMINISTRATOR_REQUIRED");
            var state=JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ior","state.json")))??throw new IOException("STATE_UNAVAILABLE");
            var profile=state.Profiles.First(p=>p.Protocol==ProxyProtocol.Vless&&p.RealityEnabled);
            state.SelectedCore=CoreKind.IorCore;state.ExperimentalCoreTesting=true;state.TunEnabled=true;state.RealityUdp443Enabled=false;
            var binary=Path.Combine(AppContext.BaseDirectory,"core","ior-core","ior-core.exe");
            for(var run=0;run<10;run++)foreach(var variant in run%2==0?new[]{"control","candidate"}:new[]{"candidate","control"}) {
                var preparation=Stopwatch.StartNew();await WindowsNetworkCommand.WarmupAsync();var workerMs=preparation.ElapsedMilliseconds;
                var cycle=Path.Combine(directory,$"{variant}-{run}");
                try {
                    await NativeTunValidator.ValidateAsync(state,profile,variant=="control"?baseline:binary,cycle,Environment.ProcessPath!,
                        measured:(stage,ms,warm)=>{samples.Add(new {run,variant,stage,ms,warm});if(stage=="ready")ready[variant].Add(ms+workerMs);},
                        performance:true,fastStartProbeOnly:true);
                } catch(Exception error){failures.Add(NativeTunBenchmark.DescribeFailure(run,"Reality",variant,error));await Save();if(error.Message.StartsWith("STOP_ACTIVE_VPN")||error.Message.Contains("ROLLBACK"))throw;}
                await Save();
            }
        } finally {await Save();await WindowsNetworkCommand.ShutdownAsync();}
    }
    public static bool TargetMet(IEnumerable<long> milliseconds,int failures)=>failures==0&&milliseconds.Count()==10&&milliseconds.Count(ms=>ms<=3000)>=9;
}
