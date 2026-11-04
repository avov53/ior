using System.Text;
using System.Text.Json;
namespace HappBox;

public sealed class JournalPolicy
{
    public int MaxMb {get;set;}=20;
    public int Days {get;set;}=7;
    public void Validate(){if(MaxMb is not (5 or 20 or 50)||Days is not (1 or 7 or 30))throw new InvalidOperationException("Недопустимый лимит журнала.");}
}
internal sealed class JournalFileStore(string directory)
{
    public JournalPolicy Policy {get;set;}=new();
    private DateTimeOffset _lastPrune;
    public IEnumerable<string> Files()=>Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Where(p=>Path.GetFileName(p) is "events.jsonl" or "events.jsonl.previous" or "resources.jsonl" || Path.GetFileName(p).StartsWith("events-")&&p.EndsWith(".jsonl")||Path.GetFileName(Path.GetDirectoryName(p))=="native-start"&&p.EndsWith(".json")):[];
    public void Append(string line)
    {
        var path=Path.Combine(directory,"events.jsonl");
        if(File.Exists(path)&&new FileInfo(path).Length+Encoding.UTF8.GetByteCount(line)>1024*1024)
            File.Move(path,Path.Combine(directory,$"events-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.jsonl"));
        File.AppendAllText(path,line);Prune();
    }
    public void Prune(bool force=false,DateTimeOffset? at=null)
    {
        var now=at??DateTimeOffset.UtcNow;
        if(!force&&now-_lastPrune<TimeSpan.FromMinutes(1)&&Size()<=Policy.MaxMb*1024L*1024)return;
        _lastPrune=now;var cutoff=now.AddDays(-Policy.Days);
        foreach(var path in Files().ToArray())
        {
            if(File.GetLastWriteTimeUtc(path)<cutoff.UtcDateTime){File.Delete(path);continue;}
            using var reader=new StreamReader(path);var first=reader.ReadLine();reader.Dispose();
            if(Timestamp(first) is { } start&&start<cutoff)
            {
                var tmp=path+".tmp";
                using(var output=new StreamWriter(tmp,false,new UTF8Encoding(false)))foreach(var line in File.ReadLines(path))if(Timestamp(line) is not { } stamp||stamp>=cutoff)output.WriteLine(line);
                File.Move(tmp,path,true);
            }
        }
        var total=Size();foreach(var path in Files().OrderBy(File.GetLastWriteTimeUtc).ToArray()){if(total<=Policy.MaxMb*1024L*1024)break;var size=new FileInfo(path).Length;File.Delete(path);total-=size;}
    }
    private static DateTimeOffset? Timestamp(string? line){try{using var json=JsonDocument.Parse(line??"{}");return json.RootElement.GetProperty("Utc").GetDateTimeOffset();}catch{return null;}}
    public long Size()=>Files().Sum(p=>new FileInfo(p).Length);
    public void Clear(){foreach(var path in Files().ToArray())File.Delete(path);}
}
