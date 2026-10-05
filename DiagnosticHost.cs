namespace HappBox;

public sealed partial class MainForm
{
    private ConnectionDiagnostics? _connectionDiagnostics;
    private bool IsNativeTunValidationRunning() => NativeTunValidator.IsRunning();
    private ConnectionDiagnostics Diagnostics => _connectionDiagnostics ??=
        new ConnectionDiagnostics(Path.Combine(_appDir, "diagnostics"));

    private IEnumerable<string?> DiagnosticSecrets()
    {
        yield return _xrayDirectHelper?.Password;
        foreach (var profile in _state.Profiles.Concat(_state.RunningProfileSnapshot is { } running ? [running] : []))
        {
            yield return profile.UserId; yield return profile.Password; yield return profile.PublicKey;
            yield return profile.PrivateKey; yield return profile.PeerPublicKey; yield return profile.PreSharedKey;
            yield return profile.ShortId; yield return profile.ObfsPassword;
        }
        foreach (var subscription in _state.Subscriptions) yield return subscription.Url;
    }

    private async Task<string?> ExportDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "Диагностический пакет (*.zip)|*.zip",
            FileName = $"ior-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        // CreateNew prevents accidental replacement of another diagnostic artifact.
        if (File.Exists(dialog.FileName)) throw new IOException("Выберите новое имя файла: пакет не перезаписывает существующие файлы.");
        await Diagnostics.ExportAsync(dialog.FileName,
            [("sing-box", _state.SingBoxPath), ("xray", CoreEngineService.XrayCorePath(_state)),
             ("ior-core", _state.IorCorePath ?? Path.Combine(AppContext.BaseDirectory, "core", "ior-core", "ior-core.exe"))]);
        return "Обезличенный диагностический пакет сохранён.";
    }
}
