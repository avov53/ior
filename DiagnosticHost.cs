namespace HappBox;

public sealed partial class MainForm
{
    private ConnectionDiagnostics? _connectionDiagnostics;
    private bool IsNativeTunValidationRunning() => NativeTunValidator.IsRunning();
    private ConnectionDiagnostics Diagnostics => _connectionDiagnostics ??=
        new ConnectionDiagnostics(Path.Combine(_appDir, "diagnostics"));

    private IEnumerable<string?> DiagnosticSecrets()
    {
        yield return _state.Lan.Password;
        yield return _xrayDirectHelper?.Password;
        foreach (var profile in _state.Profiles.Concat(_state.RunningProfileSnapshot is { } running ? [running] : []))
        {
            yield return profile.UserId; yield return profile.Password; yield return profile.PublicKey;
            yield return profile.PrivateKey; yield return profile.PeerPublicKey; yield return profile.PreSharedKey;
            if(profile.Awg.TryGetValue("header_protection_key",out var headerKey))yield return headerKey;
            yield return profile.ShortId; yield return profile.ObfsPassword;
        }
        foreach (var subscription in _state.Subscriptions)
        { yield return subscription.Url; yield return subscription.HwidOverride; yield return subscription.HwidOverride?.ToUpperInvariant(); yield return subscription.HwidOverride?.ToLowerInvariant(); }
        if (_subscriptionIdentity is { } identity) foreach (var value in identity.Secrets) yield return value;
        foreach (var value in _knownHwidSecrets.Keys) yield return value;
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
        var nativeReports = Path.Combine(_appDir, "diagnostics", "native-start");
        if (Directory.Exists(nativeReports))
        {
            using var archive = System.IO.Compression.ZipFile.Open(dialog.FileName, System.IO.Compression.ZipArchiveMode.Update);
            foreach (var file in Directory.GetFiles(nativeReports, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, file, "native-start/" + Path.GetFileName(file));
        }
        return "Обезличенный диагностический пакет сохранён.";
    }
}
