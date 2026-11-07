namespace HappBox;
public sealed partial class MainForm
{
    private System.Windows.Forms.Timer? _transientTimer;
    private void InitializeTransientMaintenance()
    {
        if(!AgDnsEnabled)return;
        PruneTransientFiles();
        _transientTimer=new System.Windows.Forms.Timer{Interval=60*60*1000};
        _transientTimer.Tick+=(_,_)=>PruneTransientFiles();_transientTimer.Start();
        Disposed+=(_,_)=>_transientTimer.Dispose();
    }
    private void PruneTransientFiles()
    {
        if(!AgDnsEnabled||_agLifecycleDepth!=0)return;
        try
        {
            var pending=File.Exists(Path.Combine(_appDir,"ag-dns-recovery.json"))||File.Exists(XrayTunRouteLease.JournalPath(_appDir));
            var removed=WindowsTransientFiles.Prune(_appDir,WindowsTransientFiles.RuntimeDirectory(_appDir),DateTime.UtcNow,IsConnectionRunning(),pending);
            if(removed>0)Log("TRANSIENT_FILES_PRUNED: удалено устаревших файлов: "+removed);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){Log("TRANSIENT_CLEANUP_SKIPPED: "+e.GetType().Name);}
    }
}
