using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class WindowsTransientFilesTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"ior-retention-test-"+Guid.NewGuid().ToString("N"));
    private string App=>Path.Combine(root,"app");private string Runtime=>Path.Combine(root,"runtime");
    private readonly DateTime now=DateTime.UtcNow;
    private string Create(string folder,string name,int hours)
    {Directory.CreateDirectory(folder);var p=Path.Combine(folder,name);File.WriteAllText(p,"test");File.SetLastWriteTimeUtc(p,now.AddHours(-hours));return p;}
    [Fact]public void RemovesOnlyExpiredRecognizedFiles()
    {
        var old=Create(Runtime,"config.xray.generated.json",25);var fresh=Create(Runtime,"config.generated.json",23);
        var state=Create(App,"state.json",100);var unknown=Create(App,"personal-config.json",100);
        var backup=Create(App,"ag-dns-backup-"+new string('a',32)+".json",25);
        var report=Create(Path.Combine(App,"diagnostics","native-start"),new string('b',32)+".json",25);
        Assert.Equal(3,WindowsTransientFiles.Prune(App,Runtime,now,false,false));Assert.False(File.Exists(old));Assert.False(File.Exists(backup));Assert.False(File.Exists(report));
        Assert.True(File.Exists(state));Assert.True(File.Exists(fresh));Assert.True(File.Exists(unknown));
    }
    [Fact]public void ActiveCoreKeepsConfigAndReport()
    {
        var config=Create(Runtime,"config.xray.generated.json",100);
        var report=Create(Path.Combine(App,"diagnostics","native-start"),new string('a',32)+".json",100);
        WindowsTransientFiles.Prune(App,Runtime,now,true,false);Assert.True(File.Exists(config));Assert.True(File.Exists(report));
    }
    [Fact]public void RecoveryKeepsJournalAndBackupsRegardlessOfAge()
    {
        var journal=Create(App,"ag-dns-recovery.json",100);var routes=Create(App,"xray-tun-routes.json",100);
        var backup=Create(App,"ag-dns-backup-"+new string('a',32)+".json",100);
        var report=Create(App,"dns-policy-report.json",100);
        WindowsTransientFiles.Prune(App,Runtime,now,false,true);Assert.All(new[]{journal,routes,backup,report},p=>Assert.True(File.Exists(p)));
        WindowsTransientFiles.Prune(App,Runtime,now,false,false);Assert.False(File.Exists(backup));Assert.False(File.Exists(report));Assert.True(File.Exists(journal));Assert.True(File.Exists(routes));
    }
    [Fact]public void LegacyConfigsExpireButSettingsAndMainJournalRemain()
    {
        var config=Create(App,"config.xray.generated.json",25);var log=Create(Path.Combine(App,"diagnostics"),"events.jsonl",100);
        WindowsTransientFiles.Prune(App,Runtime,now,false,false);Assert.False(File.Exists(config));Assert.True(File.Exists(log));
    }
    [Theory]
    [InlineData("tun_adapter_ready")]
    [InlineData("https_response_headers")]
    [InlineData("udp_dns")]
    public async Task ProbeMeasurementsUseRealRecorderWithoutStoppingConnection(string phase)
    {
        Directory.CreateDirectory(App);
        using var recorder=new ConnectionDiagnostics(App);
        Assert.True(await TunAdapterDiscovery.WaitAsync("ior-xray",inspect:()=>[new("ior-xray",System.Net.NetworkInformation.OperationalStatus.Up)]));
        XrayProbeDiagnostics.Record(recorder,phase,0,42,"OK");
        await recorder.SizeAsync();
        var text=File.ReadAllText(Path.Combine(App,"events.jsonl"));
        Assert.Contains("XRAY_TUN_PROBE_MEASURED",text);Assert.Contains(phase,text);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
