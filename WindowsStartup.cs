using System.Diagnostics;
using System.Security;
using System.Security.Principal;
namespace HappBox;

internal static class WindowsStartup
{
    internal static string TaskName=>"ior login "+WindowsIdentity.GetCurrent().User!.Value;
    internal static string TaskXml(string executable,string sid)=>$"""
    <?xml version="1.0" encoding="UTF-16"?>
    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
      <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(sid)}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
      <Principals><Principal id="User"><UserId>{SecurityElement.Escape(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
      <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><WakeToRun>false</WakeToRun></Settings>
      <Actions Context="User"><Exec><Command>{SecurityElement.Escape(executable)}</Command><Arguments>--autostart</Arguments><WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(executable))}</WorkingDirectory></Exec></Actions>
    </Task>
    """;
    internal static async Task ConfigureAsync(bool enabled)
    {
        if(!enabled){
            var type=Type.GetTypeFromProgID("Schedule.Service")??throw new IOException("Планировщик задач недоступен.");
            dynamic service=Activator.CreateInstance(type)!;
            try{service.Connect();dynamic folder=service.GetFolder("\\");try{folder.DeleteTask(TaskName,0);}catch(System.Runtime.InteropServices.COMException error)when(unchecked((uint)error.HResult)==0x80070002){}finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);}}
            finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service);}return;
        }
        var xml=Path.Combine(Path.GetTempPath(),"ior-startup-"+Guid.NewGuid().ToString("N")+".xml");
        try{await File.WriteAllTextAsync(xml,TaskXml(Environment.ProcessPath!,WindowsIdentity.GetCurrent().User!.Value),System.Text.Encoding.Unicode);await RunAsync(["/Create","/TN",TaskName,"/XML",xml,"/F"]);}finally{File.Delete(xml);}
    }
    private static async Task<string> RunAsync(string[] args,bool allowMissing=false)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Не удалось запустить планировщик задач.");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));try{await process.WaitForExitAsync(deadline.Token);}catch{try{process.Kill();}catch{}throw;}var text=await output;await error;if(process.ExitCode!=0&&!allowMissing)throw new IOException("Не удалось настроить автозагрузку. Проверьте права администратора.");return text;
    }
    internal static async Task<string> StatusAsync(){var xml=await RunAsync(["/Query","/TN",TaskName,"/XML"],true);return xml.Contains(SecurityElement.Escape(Environment.ProcessPath!)!,StringComparison.OrdinalIgnoreCase)?"Задача автозагрузки настроена":"Задача не найдена или путь изменился. Сохраните настройку заново.";}
}
