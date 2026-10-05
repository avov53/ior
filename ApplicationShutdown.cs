namespace HappBox;

public sealed partial class MainForm
{
    private bool _shutdownRequested;
    private bool _shutdownComplete;

    private async Task ShutdownAsync()
    {
        _shutdownRequested = true;
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
            _shutdownComplete = true;
            Log("SHUTDOWN_COMPLETE: сеть восстановлена.");
            Close();
        }
        catch (Exception error)
        {
            _shutdownRequested = false;
            _allowExit = false;
            Log($"SHUTDOWN_FAILED: {error.Message}");
            RestoreFromTray();
            MessageBox.Show(this, error.Message, "Завершение ior", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
