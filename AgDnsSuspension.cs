using System.Text.Json;
namespace HappBox;

public sealed record AgDnsRule(string Id,string[] Namespaces,string[] Servers,string DisplayName,string Comment,bool Simple);
public interface IAgDnsBackend
{
    Task<AgDnsRule[]> ReadAsync();
    Task RemoveAsync(AgDnsRule rule);
    Task AddAsync(AgDnsRule rule);
}

// One durable transaction per connection, including managed reconnects. No process control.
public sealed class AgDnsSuspension(string directory,IAgDnsBackend backend)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private AgDnsRule[]? suspended;
    private bool warned;
    public string Status {get;private set;}="notRequired";
    public bool RulesReappeared=>warned;
    public event Action<string>? Notice;
    private string Journal=>Path.Combine(directory,"ag-dns-recovery.json");
    public async Task AdoptActiveAsync()
    {
        await gate.WaitAsync();
        try
        {
            if(!File.Exists(Journal))return;
            var rules=JsonSerializer.Deserialize<AgDnsRule[]>(await File.ReadAllTextAsync(Journal));
            if(rules is null||rules.Length==0||rules.Any(r=>!Recognized(r)))throw new IOException("AG_DNS_INVALID_JOURNAL");
            suspended=rules;Status="suspended";
        }
        catch{Status="recoveryRequired";throw;}
        finally{gate.Release();}
    }
    public static bool Recognized(AgDnsRule r)=>r.Simple && Guid.TryParse(r.Id,out _) &&
        r.Comment=="AG_UNLOCKER_NRPT_V2" && r.DisplayName.StartsWith("AG Unlocker ",StringComparison.Ordinal) &&
        r.Namespaces.Length==1 && r.Namespaces[0] is "cloudcode-pa.googleapis.com" or "daily-cloudcode-pa.googleapis.com" &&
        r.Servers.Length>0 && r.Servers.All(s=>System.Net.IPAddress.TryParse(s,out _));
    private static bool Same(AgDnsRule a,AgDnsRule b)=>a.Simple==b.Simple && a.DisplayName==b.DisplayName && a.Comment==b.Comment &&
        a.Namespaces.Order().SequenceEqual(b.Namespaces.Order()) && a.Servers.Order().SequenceEqual(b.Servers.Order());
    private static bool Overlaps(AgDnsRule a,AgDnsRule b)=>a.Namespaces.Any(x=>b.Namespaces.Any(y=>
        x=="."||y=="."||x.Equals(y,StringComparison.OrdinalIgnoreCase)||
        x.TrimStart('.').EndsWith("."+y.TrimStart('.'),StringComparison.OrdinalIgnoreCase)||
        y.TrimStart('.').EndsWith("."+x.TrimStart('.'),StringComparison.OrdinalIgnoreCase)));
    public async Task SuspendAsync()
    {
        await gate.WaitAsync();
        try
        {
            if(suspended is not null)return;
            if(File.Exists(Journal))throw new IOException("AG_DNS_RECOVERY_PENDING");
            var all=await backend.ReadAsync();
            var rules=all.Where(Recognized).ToArray();
            if(rules.Length==0){Status="notRequired";return;}
            if(all.Any(r=>!Recognized(r)&&rules.Any(saved=>Overlaps(saved,r))))throw new IOException("AG_DNS_FOREIGN_CONFLICT");
            Directory.CreateDirectory(directory);
            var json=JsonSerializer.Serialize(rules);
            // Keep an immutable backup and a separate active recovery journal.
            await File.WriteAllTextAsync(Path.Combine(directory,"ag-dns-backup-"+Guid.NewGuid().ToString("N")+".json"),json);
            var temp=Journal+".tmp";await File.WriteAllTextAsync(temp,json);File.Move(temp,Journal,false);
            suspended=rules;Status="suspended";
            foreach(var rule in rules)
            {
                var current=(await backend.ReadAsync()).SingleOrDefault(r=>r.Id==rule.Id);
                if(current is null||!Same(current,rule))throw new IOException("AG_DNS_RULE_CHANGED");
                await backend.RemoveAsync(rule);
            }
            if((await backend.ReadAsync()).Any(r=>rules.Any(saved=>Overlaps(saved,r))))throw new IOException("AG_DNS_RULE_RECREATED");
            Notice?.Invoke("DNS-правила AG Unlocker временно отключены на время VPN. Связанные функции AG могут временно не работать.");
        }
        catch {Status=File.Exists(Journal)?"recoveryRequired":"notRequired";throw;}
        finally {gate.Release();}
    }
    public async Task RestoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            if(!File.Exists(Journal)){suspended=null;Status="notRequired";return;}
            Status="restoring";
            var rules=JsonSerializer.Deserialize<AgDnsRule[]>(await File.ReadAllTextAsync(Journal))??throw new IOException("AG_DNS_INVALID_JOURNAL");
            if(rules.Length==0||rules.Any(r=>!Recognized(r)))throw new IOException("AG_DNS_INVALID_JOURNAL");
            foreach(var rule in rules)
            {
                var current=await backend.ReadAsync();
                if(current.Any(r=>Same(r,rule)))continue;
                if(current.Any(r=>r.Id==rule.Id||Overlaps(rule,r)))throw new IOException("AG_DNS_RESTORE_CONFLICT: правила изменены другой программой; резервная копия сохранена.");
                await backend.AddAsync(rule);
                if(!(await backend.ReadAsync()).Any(r=>Same(r,rule)))throw new IOException("AG_DNS_RESTORE_NOT_VERIFIED");
            }
            File.Delete(Journal);suspended=null;warned=false;Status="notRequired";
            Notice?.Invoke("DNS-правила AG Unlocker восстановлены.");
        }
        catch {Status="recoveryRequired";throw;}
        finally {gate.Release();}
    }
    public async Task MonitorAsync()
    {
        if(!await gate.WaitAsync(0))return;
        try
        {
            if(suspended is null||warned)return;
            if((await backend.ReadAsync()).Any(r=>Recognized(r)&&suspended.Any(saved=>Overlaps(saved,r))))
            {warned=true;Notice?.Invoke("AG_UNLOCKER_DNS_CHANGED: DNS-правила появились снова. Повторное удаление не выполняется.");}
        }
        finally{gate.Release();}
    }
}
