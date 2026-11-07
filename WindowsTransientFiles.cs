using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace HappBox;

public static class WindowsTransientFiles
{
    public static string RuntimeDirectory(string appDirectory)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(appDirectory).ToUpperInvariant())))[..24];
        var directory=Path.Combine(Path.GetTempPath(),"ior-runtime-"+key);
        AssertPlain(directory);Directory.CreateDirectory(directory);
        var user=WindowsIdentity.GetCurrent().User??throw new IOException("RUNTIME_IDENTITY_UNAVAILABLE");
        var acl=new DirectorySecurity();acl.SetOwner(user);acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        return directory;
    }
    private static void AssertPlain(string path)
    {
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("TRANSIENT_PATH_IS_LINK");
    }
    public static int Prune(string appDirectory,string runtimeDirectory,DateTime utcNow,bool coreActive,bool recoveryPending)
    {
        var removed=0;
        void RemoveOld(string path)
        {
            try
            {
                AssertPlain(path);
                if(File.Exists(path)&&File.GetLastWriteTimeUtc(path)<utcNow.AddHours(-24)){File.Delete(path);removed++;}
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){ }
        }
        if(!coreActive)
        {
            foreach(var name in new[]{"config.generated.json","config.xray.generated.json"})
            {RemoveOld(Path.Combine(appDirectory,name));RemoveOld(Path.Combine(runtimeDirectory,name));}
        }
        if(recoveryPending)return removed;
        foreach(var name in new[]{"dns-policy-report.json","win10-tun-failure.json"})RemoveOld(Path.Combine(appDirectory,name));
        AssertPlain(appDirectory);
        if(Directory.Exists(appDirectory))foreach(var path in Directory.EnumerateFiles(appDirectory,"ag-dns-backup-*.json"))
            if(Regex.IsMatch(Path.GetFileName(path),"^ag-dns-backup-[a-f0-9]{32}\\.json$"))RemoveOld(path);
        var reports=Path.Combine(appDirectory,"diagnostics","native-start");AssertPlain(reports);
        if(!coreActive&&Directory.Exists(reports))foreach(var path in Directory.EnumerateFiles(reports))
            if(Regex.IsMatch(Path.GetFileName(path),"^[a-f0-9]{32}\\.json(\\.tmp)?$"))RemoveOld(path);
        return removed;
    }
}
