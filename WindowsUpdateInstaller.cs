using System.Diagnostics;
using System.Text.Json;
namespace HappBox;
internal static class WindowsUpdateInstaller
{
    public static void Start(string source,string root)
    {
        var script=Path.Combine(root,"install.ps1");
        using(var input=typeof(WindowsUpdateInstaller).Assembly.GetManifestResourceStream("HappBox.Install-Update.ps1")??throw new IOException("Installer resource missing"))
        using(var output=File.Create(script))input.CopyTo(output);
        var manifest=Path.Combine(root,"install.json");
        File.WriteAllText(manifest,JsonSerializer.Serialize(new{source=Path.GetFullPath(source),root=Path.GetFullPath(root),target=AppContext.BaseDirectory,processId=Environment.ProcessId}));
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Manifest",manifest})info.ArgumentList.Add(argument);
        var ready=Path.Combine(root,"installer-ready.txt");File.Delete(ready);
        using var process=Process.Start(info)??throw new IOException("Не удалось запустить установщик.");
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(DateTime.UtcNow<deadline){
            try{if(File.Exists(ready)&&File.ReadAllText(ready).Trim()==process.Id.ToString())return;}catch(IOException){}
            if(process.HasExited)break;
            Thread.Sleep(50);
        }
        if(!process.HasExited)process.Kill(entireProcessTree:true);
        throw new IOException("Установщик не подтвердил готовность. Приложение не закрыто.");
    }
}
