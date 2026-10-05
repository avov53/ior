using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace HappBox;

/// <summary>Reads the protobuf geodata format used by the packaged Xray databases.</summary>
internal static class NativeGeoData
{
    private sealed record Field(int Number, ulong Integer, ReadOnlyMemory<byte> Bytes);
    private static readonly ConcurrentDictionary<string, OrderedRouteRule[]> Cache = new();

    public static OrderedRouteRule[] Expand(OrderedRouteRule rule, string directory)
    {
        var kind=rule.Kind.ToUpperInvariant();
        if(kind=="IP-CIDR" && rule.Value.StartsWith("geoip:",StringComparison.OrdinalIgnoreCase))
        { kind="GEOIP"; rule=new OrderedRouteRule {Kind=kind,Value=rule.Value[6..],Target=rule.Target,Source=rule.Source,Raw=rule.Raw,NoResolve=rule.NoResolve}; }
        if(kind=="IP-CIDR6")return [new OrderedRouteRule {Kind="IP-CIDR",Value=rule.Value,Target=rule.Target,Source=rule.Source,Raw=rule.Raw,NoResolve=rule.NoResolve}];
        if(kind is not ("GEOIP" or "GEOSITE")) return [rule];
        var name=rule.Value.Trim().ToLowerInvariant();
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z0-9_-]{1,80}$"))
            throw new FormatException("Геоправило с атрибутами или инверсией пока не поддерживается: "+rule.Raw);
        var path=Path.Combine(directory,kind=="GEOIP"?"geoip.dat":"geosite.dat");
        var info=new FileInfo(path);
        if(!info.Exists || info.Length>64*1024*1024)throw new FormatException("Недоступна обязательная геобаза для "+rule.Raw);
        var key=path+"|"+info.LastWriteTimeUtc.Ticks+"|"+name;
        if(Cache.Count>32)Cache.Clear();
        var values=Cache.GetOrAdd(key,_=> {
            var parsed=Read(File.ReadAllBytes(path),kind,name);
            return kind=="GEOIP" ? [new OrderedRouteRule {Kind="IP-SET",Value=string.Join('\n',parsed.Select(value=>value.Value))}] : parsed;
        });
        return values.Select(value=>new OrderedRouteRule {Kind=value.Kind,Value=value.Value,Target=rule.Target,
            NoResolve=rule.NoResolve,Source=rule.Source,Raw=rule.Raw}).ToArray();
    }
    private static OrderedRouteRule[] Read(byte[] data,string kind,string name)
    {
        foreach(var entry in Fields(data))
        {
            if(entry.Number!=1)continue;
            var fields=Fields(entry.Bytes).ToArray();
            var code=fields.FirstOrDefault(field=>field.Number==1);
            if(code is null || !Text(code.Bytes).Equals(name,StringComparison.OrdinalIgnoreCase))continue;
            if(kind=="GEOIP" && fields.Any(field=>field.Number==3 && field.Integer!=0))
                throw new FormatException("Инверсная GEOIP-база пока не поддерживается: "+name);
            var result=new List<OrderedRouteRule>();
            foreach(var item in fields.Where(field=>field.Number==2))
            {
                var value=Fields(item.Bytes).ToArray();
                if(kind=="GEOIP")
                {
                    var ip=value.FirstOrDefault(field=>field.Number==1)?.Bytes??default;
                    var prefix=value.FirstOrDefault(field=>field.Number==2)?.Integer??0;
                    if(ip.Length is not (4 or 16) || prefix>(ulong)(ip.Length*8))throw new FormatException("Повреждённая GEOIP-база.");
                    result.Add(new OrderedRouteRule {Kind="IP-CIDR",Value=new IPAddress(ip.Span)+"/"+prefix});
                }
                else
                {
                    var type=value.FirstOrDefault(field=>field.Number==1)?.Integer??0;
                    var domain=value.FirstOrDefault(field=>field.Number==2)?.Bytes??default;
                    result.Add(new OrderedRouteRule {Kind=type switch {0=>"DOMAIN-KEYWORD",1=>"REGEXP",2=>"DOMAIN-SUFFIX",3=>"DOMAIN",_=>throw new FormatException("Неизвестный тип GEOSITE.")},Value=Text(domain)});
                }
                if(result.Count>250000)throw new FormatException("Геоправило превышает защитный лимит: "+name);
            }
            if(result.Count==0)throw new FormatException("Пустая геобаза: "+name);
            return result.ToArray();
        }
        throw new FormatException("В геобазе отсутствует раздел: "+name);
    }
    private static string Text(ReadOnlyMemory<byte> bytes)=>Encoding.UTF8.GetString(bytes.Span);
    private static IEnumerable<Field> Fields(ReadOnlyMemory<byte> input)
    {
        var data=input.ToArray();var offset=0;
        while(offset<data.Length)
        {
            var tag=Varint(data,ref offset);var number=checked((int)(tag>>3));
            if(number==0)throw new FormatException("Повреждённая protobuf-геобаза.");
            switch(tag&7)
            {
                case 0:yield return new Field(number,Varint(data,ref offset),default);break;
                case 2:
                    var length=checked((int)Varint(data,ref offset));
                    if(length>data.Length-offset)throw new FormatException("Повреждённая длина геобазы.");
                    yield return new Field(number,0,data.AsMemory(offset,length));offset+=length;break;
                case 1:if(data.Length-offset<8)throw new FormatException("Повреждённая геобаза.");offset+=8;break;
                case 5:if(data.Length-offset<4)throw new FormatException("Повреждённая геобаза.");offset+=4;break;
                default:throw new FormatException("Неизвестный protobuf-формат геобазы.");
            }
        }
    }
    private static ulong Varint(byte[] data,ref int offset)
    {
        ulong result=0;
        for(var shift=0;shift<64;shift+=7)
        {
            if(offset>=data.Length)throw new FormatException("Оборванная геобаза.");
            var value=data[offset++];if(shift==63 && value>1)throw new FormatException("Переполнение геобазы.");
            result|=(ulong)(value&127)<<shift;if((value&128)==0)return result;
        }
        throw new FormatException("Переполнение геобазы.");
    }
}
