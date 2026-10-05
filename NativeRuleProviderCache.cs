using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace HappBox;

internal static class NativeRuleProviderCache
{
    private static string CachePath(RouteProviderDefinition provider)=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ior","rule-cache",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.Url+"|"+provider.Format+"|"+provider.Behavior)))+".rules");
    public static async Task PrepareAsync(HappRoutingProfile? route,HttpClient client,CancellationToken token)
    {
        if(route?.RoutePlan is not { } plan)return;
        foreach(var provider in plan.Providers.Where(item=>plan.Rules.Any(rule=>rule.Kind=="RULE-SET" && rule.Value==item.Name)))
        {
            if(provider.Type!="http" || provider.Format is not ("yaml" or "text"))continue;
            if(!Uri.TryCreate(provider.Url,UriKind.Absolute,out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new FormatException("Недопустимый URL провайдера правил: "+provider.Name);
            var path=CachePath(provider);
            if(File.Exists(path) && DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(Math.Clamp(provider.IntervalSeconds,60,86400)))continue;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength is >2_000_000)throw new FormatException("Провайдер превышает лимит.");
                using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var buffer=new MemoryStream();var bytes=new byte[8192];
                int count;while((count=await stream.ReadAsync(bytes,deadline.Token))>0){if(buffer.Length+count>2_000_000)throw new FormatException("Провайдер превышает лимит.");buffer.Write(bytes,0,count);}
                var body=Encoding.UTF8.GetString(buffer.ToArray());Parse(provider,body,"PROXY",false);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary=path+"."+Guid.NewGuid().ToString("N")+".pending";
                try {await File.WriteAllTextAsync(temporary,body,deadline.Token);File.Move(temporary,path,true);}
                finally {if(File.Exists(temporary))File.Delete(temporary);}
            }
            catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or FormatException)
            {
                if(token.IsCancellationRequested)throw;
                if(!File.Exists(path))throw new FormatException("Нет исправного кеша провайдера: "+provider.Name);
            }
        }
    }
    public static IEnumerable<OrderedRouteRule> Expand(OrderedRouteRule rule,HappRoutingProfile? route)
    {
        if(rule.Kind.ToUpperInvariant()!="RULE-SET")return [rule];
        var provider=route?.RoutePlan?.Providers.FirstOrDefault(item=>item.Name==rule.Value)
            ??throw new FormatException("Не найден провайдер RULE-SET: "+rule.Value);
        string body;
        if(provider.Type=="inline" && provider.Parameters["payload"] is System.Text.Json.Nodes.JsonArray payload)
            body="payload:\n"+string.Join("\n",payload.Select(item=>"  - "+System.Text.Json.JsonSerializer.Serialize(item?.GetValue<string>())));
        else if(provider.Type=="http" && File.Exists(CachePath(provider)))body=File.ReadAllText(CachePath(provider));
        else throw new FormatException("RULE-SET без проверенного кеша: "+provider.Name+". Обновите подписку.");
        return Parse(provider,body,rule.Target,rule.NoResolve);
    }
    internal static OrderedRouteRule[] Parse(RouteProviderDefinition provider,string body,string target,bool noResolve)
    {
        if(body.Length>2_000_000 || provider.Format is not ("yaml" or "text"))throw new FormatException("Неподдерживаемый формат RULE-SET: "+provider.Name);
        IEnumerable<string> values;
        if(provider.Format=="text")values=body.Split('\n').Select(line=>line.Trim()).Where(line=>line.Length>0 && !line.StartsWith('#'));
        else
        {
            var yaml=new YamlStream();
            try { yaml.Load(new StringReader(body)); }
            catch(YamlDotNet.Core.YamlException) { throw new FormatException("Повреждённый YAML провайдера: "+provider.Name); }
            if(yaml.Documents.Count!=1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
                root.Children.FirstOrDefault(pair=>pair.Key is YamlScalarNode {Value:"payload"}).Value is not YamlSequenceNode sequence)
                throw new FormatException("Нет payload провайдера: "+provider.Name);
            values=sequence.Children.Select(item=>item is YamlScalarNode {Value:{ } value}?value:throw new FormatException("Не строковый RULE-SET."));
        }
        var result=new List<OrderedRouteRule>();
        foreach(var value in values)
        {
            string kind,matcher;
            switch(provider.Behavior)
            {
                case "ipcidr":kind="IP-CIDR";matcher=value;break;
                case "domain":
                    kind=value.StartsWith("+.")?"DOMAIN-SUFFIX":value.Contains('*')?"REGEXP":"DOMAIN";
                    matcher=kind=="DOMAIN-SUFFIX"?value[2..]:kind=="REGEXP"?"^"+System.Text.RegularExpressions.Regex.Escape(value).Replace("\\*",".*")+"$":value;break;
                case "classical":
                    var parts=value.Split(',',StringSplitOptions.TrimEntries);
                    if(parts.Length is <2 or >3 || parts.Length==3 && parts[2]!="no-resolve")throw new FormatException("Неизвестное classical-правило: "+provider.Name);
                    kind=parts[0].ToUpperInvariant();matcher=parts[1];break;
                default:throw new FormatException("Неподдерживаемый behavior RULE-SET: "+provider.Name);
            }
            if(kind is not ("DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "IP-CIDR" or "GEOIP" or "GEOSITE" or "REGEXP"))
                throw new FormatException("Неисполняемое правило RULE-SET: "+provider.Name+" / "+kind);
            if(kind=="IP-CIDR" && !System.Net.IPNetwork.TryParse(matcher,out _))throw new FormatException("Некорректный CIDR провайдера: "+provider.Name);
            if(string.IsNullOrWhiteSpace(matcher))throw new FormatException("Пустое условие провайдера: "+provider.Name);
            result.Add(new OrderedRouteRule {Kind=kind,Value=matcher,Target=target,NoResolve=noResolve || value.EndsWith(",no-resolve"),Source=RoutingProfileSource.Clash,Raw="RULE-SET,"+provider.Name+" / "+kind});
            if(result.Count>25000)throw new FormatException("RULE-SET превышает лимит: "+provider.Name);
        }
        if(result.Count==0)throw new FormatException("Пустой RULE-SET: "+provider.Name);
        return result.ToArray();
    }
}
