using System.Text;
using System.Text.Json;
namespace HappBox;

public sealed class AgDnsWindowsBackend(Func<string,Task<string>>? execute=null):IAgDnsBackend
{
    private const string Projection="""
function Project($r) {
 $simple=$true
 foreach($p in @('DA','DADnsServers','DnsSec','DirectAccessDnsServers','DirectAccessEnabled','DirectAccessProxyType','DirectAccessProxyName','DirectAccessQueryIPsecEncryption','DirectAccessQueryIPsecRequired','DnsSecEnabled','DnsSecQueryIPsecEncryption','DnsSecQueryIPsecRequired','DnsSecValidationRequired','IPsecCARestriction')) {
  if($r.$p){$simple=$false}
 }
 if($r.NameEncoding -and $r.NameEncoding -ne 'Disable'){$simple=$false}
 # Do not alter policy-managed entries, even if their display metadata resembles AG.
 if(Test-Path -LiteralPath ('HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig\'+$r.Name)){$simple=$false}
 # The friendly NameServers property can be a wrapped IPAddress collection on Win10.
 # The underlying CIM value is the original string array needed for exact restoration.
 $rawServers=@($r.CimInstanceProperties | Where-Object {$_.Name -eq 'NameServers'})
 $servers=if($rawServers.Count -eq 1){@($rawServers[0].Value)}else{@($r.NameServers)}
 [ordered]@{Id=[string]$r.Name;Namespaces=@($r.Namespace | Sort-Object);Servers=@($servers | ForEach-Object {$_.ToString()} | Sort-Object);DisplayName=[string]$r.DisplayName;Comment=[string]$r.Comment;Simple=$simple}
}
""";
    private Task<string> Run(string script)=>(execute ?? (text=>WindowsNetworkCommand.RunIsolatedAsync(text,CancellationToken.None)))(Projection+"\n"+script);
    public async Task<AgDnsRule[]> ReadAsync()
    {
        var text=await Run("$rows=@(Get-DnsClientNrptRule -ErrorAction Stop | ForEach-Object {Project $_}); ConvertTo-Json -InputObject $rows -Depth 5 -Compress");
        return JsonSerializer.Deserialize<AgDnsRule[]>(text)??[];
    }
    private static string Input(AgDnsRule rule)=>"$expected=([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rule)))+"')) | ConvertFrom-Json)\n";
    public async Task RemoveAsync(AgDnsRule rule)
    {
        if(!AgDnsSuspension.Recognized(rule))throw new IOException("AG_DNS_NOT_OWNED");
        await Run(Input(rule)+"""
$current=@(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object {$_.Name -eq $expected.Id})
if($current.Count -ne 1){throw 'AG_DNS_RULE_CHANGED'}
$actual=Project $current[0]
$same=$actual.Simple -and $actual.DisplayName -ceq $expected.DisplayName -and $actual.Comment -ceq $expected.Comment -and
 (($actual.Namespaces -join '|') -ceq ($expected.Namespaces -join '|')) -and (($actual.Servers -join '|') -ceq ($expected.Servers -join '|'))
if(!$same){throw 'AG_DNS_RULE_CHANGED'}
Remove-DnsClientNrptRule -Name $current[0].Name -Force -Confirm:$false -ErrorAction Stop
Clear-DnsClientCache -ErrorAction Stop
""");
    }
    public async Task AddAsync(AgDnsRule rule)
    {
        if(!AgDnsSuspension.Recognized(rule))throw new IOException("AG_DNS_NOT_OWNED");
        await Run(Input(rule)+"""
$existing=@(Get-DnsClientNrptRule -ErrorAction Stop)
foreach($r in $existing){foreach($ns in @($r.Namespace)){
 foreach($saved in $expected.Namespaces){
 if($ns -eq '.' -or $ns -eq $saved -or $r.Name -eq $expected.Id -or
    $saved.TrimStart('.').EndsWith('.'+$ns.TrimStart('.'),[StringComparison]::OrdinalIgnoreCase) -or
    $ns.TrimStart('.').EndsWith('.'+$saved.TrimStart('.'),[StringComparison]::OrdinalIgnoreCase)){throw 'AG_DNS_RESTORE_CONFLICT'}
 }
}}
Add-DnsClientNrptRule -Namespace @($expected.Namespaces) -NameServers @($expected.Servers) -DisplayName $expected.DisplayName -Comment $expected.Comment -NameEncoding Disable -ErrorAction Stop | Out-Null
Clear-DnsClientCache -ErrorAction Stop
""");
    }
}
