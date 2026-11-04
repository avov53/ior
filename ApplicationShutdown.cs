namespace HappBox;

public sealed partial class MainForm
{
    private Action? _afterNetworkShutdown;
    private bool _shutdownRequested;
    private bool _shutdownComplete;

    private async Task ShutdownAsync()
    {
        _shutdownRequested = true;
        CancelBackgroundConnectionActions();
        Log("SHUTDOWN_STARTED: остановка ядра и восстановление сети.");
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (_connectionTransitionInProgress || _webOperationRunning)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Операция подключения не завершилась. Журнал восстановления сохранён.");
                await Task.Delay(100);
            }
            if (!await StopSingBoxAsync()) throw new InvalidOperationException("Не удалось восстановить сеть. Журнал восстановления сохранён.");
            await WindowsNetworkCommand.ShutdownAsync();
            _afterNetworkShutdown?.Invoke();_afterNetworkShutdown=null;
            _shutdownComplete = true;
            Log("SHUTDOWN_COMPLETE: сеть восстановлена.");
            Close();
        }
        catch (Exception error)
        {
            if(_afterNetworkShutdown is not null){_afterNetworkShutdown=null;_updateError=error.Message;_updateUiState=UpdateUiState.Failed;PushWebSnapshot(includeLogs:false);}
            _shutdownRequested = false;
            _allowExit = false;
            Log($"SHUTDOWN_FAILED: {error.Message}");
            RestoreFromTray();
            MessageBox.Show(this, error.Message, "Завершение ior", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
