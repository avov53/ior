using System.Text.Json;
using System.Xml.Linq;
using Xunit;
namespace HappBox.Tests;
public class LifecycleAndJournalTests
{
    [Fact] public void DefaultsDoNotRegisterAutostartOrEnableLan()
    {var state=new AppState();Assert.False(state.LaunchWithWindows);Assert.False(state.ConnectAtWindowsStartup);Assert.False(state.Lan.Enabled);Assert.True(state.RestoreAfterSleep);Assert.Equal(20,state.Journal.MaxMb);Assert.Equal(7,state.Journal.Days);}
    [Fact] public void StartupTaskEscapesPathsAndRequiresInteractiveLogin()
    {var xml=XDocument.Parse(WindowsStartup.TaskXml("C:\\Программы & игры\\ior.exe","S-1-5-21-test"));XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";Assert.Equal("C:\\Программы & игры\\ior.exe",xml.Descendants(ns+"Command").Single().Value);Assert.Equal("--autostart",xml.Descendants(ns+"Arguments").Single().Value);Assert.Equal("InteractiveToken",xml.Descendants(ns+"LogonType").Single().Value);Assert.Equal("HighestAvailable",xml.Descendants(ns+"RunLevel").Single().Value);Assert.Equal("false",xml.Descendants(ns+"StopIfGoingOnBatteries").Single().Value);}
    [Fact] public async Task ClearWaitsForQueuedRowsAndExportContainsNewRowsOnly()
    {var dir=Path.Combine(Path.GetTempPath(),"ior-clear-"+Guid.NewGuid().ToString("N"));try{using var d=new ConnectionDiagnostics(dir);for(var i=0;i<100;i++)d.Record("test","BEFORE_CLEAR");await d.ClearAsync();d.Record("test","AFTER_CLEAR");await d.ConfigureAsync(new(){MaxMb=5,Days=1});await d.SizeAsync();var text=string.Join("",Directory.GetFiles(dir,"*.jsonl").Select(File.ReadAllText));Assert.DoesNotContain("BEFORE_CLEAR",text);Assert.Contains("AFTER_CLEAR",text);}finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}}
    [Fact] public void RetentionKeepsExportsAndPrunesOldFiles()
    {var dir=Path.Combine(Path.GetTempPath(),"ior-retain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);try{var old=Path.Combine(dir,"events-old.jsonl");File.WriteAllText(old,"{}\n");File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-8));var saved=Path.Combine(dir,"saved.zip");File.WriteAllText(saved,"export");var store=new JournalFileStore(dir);store.Prune(true);Assert.False(File.Exists(old));Assert.True(File.Exists(saved));store.Clear();Assert.True(File.Exists(saved));}finally{Directory.Delete(dir,true);}}
}
