using HappBox;
using System.Net.NetworkInformation;
using Xunit;
namespace HappBox.Tests;
public sealed class AgDnsSuspensionTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"ior-ag-test-"+Guid.NewGuid().ToString("N"));
    private static AgDnsRule Rule(string domain="cloudcode-pa.googleapis.com")=>new(Guid.NewGuid().ToString(),[domain],["1.1.1.1"],"AG Unlocker test","AG_UNLOCKER_NRPT_V2",true);
    private sealed class Backend(params AgDnsRule[] initial):IAgDnsBackend
    {
        public readonly List<AgDnsRule> Rules=[..initial];public int Removed,Added;public int FailRemoveAt;
        public Task<AgDnsRule[]> ReadAsync()=>Task.FromResult(Rules.ToArray());
        public Task RemoveAsync(AgDnsRule r){if(++Removed==FailRemoveAt)throw new IOException("simulated");Rules.Remove(r);return Task.CompletedTask;}
        public Task AddAsync(AgDnsRule r){Added++;Rules.Add(r with{Id=Guid.NewGuid().ToString()});return Task.CompletedTask;}
    }
    [Fact] public async Task NoRulesDoesNotCreateJournal(){var b=new Backend();var lease=new AgDnsSuspension(directory,b);await lease.SuspendAsync();Assert.Equal("notRequired",lease.Status);Assert.False(Directory.Exists(directory));}
    [Fact] public async Task RestartUsesOneLeaseAndRecoversFromDisk()
    {
        var b=new Backend(Rule());var lease=new AgDnsSuspension(directory,b);await lease.SuspendAsync();await lease.SuspendAsync();
        Assert.Equal(1,b.Removed);Assert.Empty(b.Rules);Assert.True(File.Exists(Path.Combine(directory,"ag-dns-recovery.json")));
        await new AgDnsSuspension(directory,b).RestoreAsync();Assert.Single(b.Rules);Assert.Equal(1,b.Added);
    }
    [Fact] public async Task PartialFailureCanBeRolledBack()
    {
        var b=new Backend(Rule(),Rule("daily-cloudcode-pa.googleapis.com")){FailRemoveAt=2};var lease=new AgDnsSuspension(directory,b);
        await Assert.ThrowsAsync<IOException>(lease.SuspendAsync);await lease.RestoreAsync();Assert.Equal(2,b.Rules.Count);Assert.Equal("notRequired",lease.Status);
    }
    [Fact] public async Task ChangedRulesAreNotOverwritten()
    {
        var b=new Backend(Rule());var lease=new AgDnsSuspension(directory,b);await lease.SuspendAsync();b.Rules.Add(Rule() with{Servers=["8.8.8.8"]});
        await Assert.ThrowsAsync<IOException>(lease.RestoreAsync);Assert.Equal(0,b.Added);Assert.Equal("recoveryRequired",lease.Status);
        Assert.True(File.Exists(Path.Combine(directory,"ag-dns-recovery.json")));
    }
    [Fact] public async Task RecreatedRuleWarnsOnceWithoutDeletingAgain()
    {
        var rule=Rule();var b=new Backend(rule);var lease=new AgDnsSuspension(directory,b);var notices=new List<string>();lease.Notice+=notices.Add;
        await lease.SuspendAsync();b.Rules.Add(rule);await lease.MonitorAsync();await lease.MonitorAsync();Assert.Equal(2,notices.Count);Assert.Equal(1,b.Removed);
        await lease.RestoreAsync();Assert.Equal(0,b.Added);
    }
    [Fact]public async Task OwnedIorRuleAndUnknownRuleAreNotReportedAsAgReturning()
    {
        var b=new Backend(Rule());var lease=new AgDnsSuspension(directory,b);await lease.SuspendAsync();
        b.Rules.Add(Rule() with{Namespaces=["."],DisplayName="ior-dns-ior-native-test",Comment="ior-owned:ior-native-test"});
        b.Rules.Add(Rule() with{Comment="unknown-owner"});
        await lease.MonitorAsync();Assert.False(lease.RulesReappeared);Assert.Equal(1,b.Removed);
    }
    [Fact] public async Task ForeignAndComplexRulesAreNotRemoved()
    {
        var foreign=Rule() with{Comment="corporate"};var b=new Backend(foreign,Rule("daily-cloudcode-pa.googleapis.com") with{Simple=false});
        await new AgDnsSuspension(directory,b).SuspendAsync();Assert.Equal(0,b.Removed);
    }
    [Fact] public async Task CannotSaveJournalDoesNotDeleteAnything()
    {
        Directory.CreateDirectory(directory);var file=Path.Combine(directory,"not-a-directory");File.WriteAllText(file,"occupied");var b=new Backend(Rule());
        await Assert.ThrowsAnyAsync<IOException>(()=>new AgDnsSuspension(file,b).SuspendAsync());Assert.Equal(0,b.Removed);
    }
    [Fact] public async Task DeadCoreStopsAdapterWaitImmediately()
    {
        var reads=0;Assert.False(await TunAdapterDiscovery.WaitAsync("ior-xray",inspect:()=>{reads++;return [];},limit:TimeSpan.FromSeconds(30),coreAlive:()=>false));Assert.Equal(0,reads);
    }
    [Fact] public async Task WrongAdapterIsNeverAccepted()=>Assert.False(await TunAdapterDiscovery.WaitAsync("ior-xray",inspect:()=>[new("another-vpn",OperationalStatus.Up)],limit:TimeSpan.Zero));
    [Fact] public async Task ActiveConnectionAdoptsJournalWithoutRestoringRules()
    {
        var b=new Backend(Rule());await new AgDnsSuspension(directory,b).SuspendAsync();
        var recovered=new AgDnsSuspension(directory,b);await recovered.AdoptActiveAsync();await recovered.SuspendAsync();
        Assert.Empty(b.Rules);Assert.Equal(1,b.Removed);await recovered.RestoreAsync();Assert.Single(b.Rules);
    }
    [Fact] public async Task ForeignOverlappingPolicyPreventsAnyRemoval()
    {
        var b=new Backend(Rule(),Rule() with{Namespaces=["."],Comment="corporate"});
        await Assert.ThrowsAsync<IOException>(()=>new AgDnsSuspension(directory,b).SuspendAsync());Assert.Equal(0,b.Removed);
    }
    [Fact] public async Task WindowsCommandsParseWithoutExecutingNetworkChanges()
    {
        async Task<string> ParseOnly(string script)
        {
            var encoded=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            var result=await WindowsNetworkCommand.RunIsolatedAsync("$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+encoded+"')),[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){throw ($errors|Out-String)};'[]'");
            return result;
        }
        var backend=new AgDnsWindowsBackend(ParseOnly);Assert.Empty(await backend.ReadAsync());await backend.RemoveAsync(Rule());await backend.AddAsync(Rule());
    }
    [Fact] public async Task Win10WrappedServersUseOriginalCimStrings()
    {
        const string mock="""
function Test-Path {param($LiteralPath) return $false}
function Get-DnsClientNrptRule {
 [pscustomobject]@{Name='{11111111-1111-1111-1111-111111111111}';Namespace=@('cloudcode-pa.googleapis.com');
 NameServers=[pscustomobject]@{value=@('wrapped-not-an-address')};DisplayName='AG Unlocker test';Comment='AG_UNLOCKER_NRPT_V2';NameEncoding='Disable';
 CimInstanceProperties=@([pscustomobject]@{Name='NameServers';Value=@('1.1.1.1','127.0.0.53')})}
}
""";
        var backend=new AgDnsWindowsBackend(script=>WindowsNetworkCommand.RunIsolatedAsync(mock+"\n"+script));
        var rule=Assert.Single(await backend.ReadAsync());Assert.True(AgDnsSuspension.Recognized(rule));Assert.Contains("127.0.0.53",rule.Servers);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
