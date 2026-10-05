using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappBox;

public sealed partial class MainForm
{
    private WebView2? _webView;
    private readonly System.Windows.Forms.Timer _webUiSnapshotTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _subscriptionRefreshTimer = new() { Interval = 60_000 };
    private bool _webUiReady;
    private bool _scheduledRefreshRunning;
    private ConnectionOperation? _webOperation;
    private bool _webOperationRunning;
    private void AdvanceWebOperation(string stage)
    {
        _webOperation?.Advance(stage);
        PushWebSnapshot(includeLogs: false);
    }
    private readonly Dictionary<string, string?> _serviceIconDataCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _validatedConfig;
    private bool? _configValidationResult;

    private bool ValidateSelectedConfig(bool force = false)
    {
        var selected = SelectedProfile();
        if (selected is null) return false;
        string config;
        try { config = SingBoxConfigBuilder.Build(_state, selected); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            _validatedConfig = null;
            _configValidationResult = false;
            Log($"Routing configuration rejected: {ex.Message}");
            return false;
        }
        if (!force && config == _validatedConfig && _configValidationResult.HasValue)
            return _configValidationResult.Value;
        _validatedConfig = config;
        _configValidationResult = TryWriteConfig(selected) && ValidateSingBoxConfig(showErrors: false);
        PushWebSnapshot();
        return _configValidationResult.Value;
    }

    private void ValidateSelectedConfigIfEnabled()
    {
        if (_state.AutoValidateConfig) ValidateSelectedConfig(force: true);
    }

    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private async Task InitializeWebUiAsync()
    {
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "webui");
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                ShowWebUiFailure("React-интерфейс не найден в папке webui.", false);
                return;
            }

            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_appDir, "webview2"));
            _webView = new WebView2 { Dock = DockStyle.Fill, BackColor = Bg };
            Controls.Add(_webView);
            _webView.BringToFront();
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.ior",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                _webUiReady = args.IsSuccess;
                if (_webUiReady) PushWebSnapshot();
            };
            _webView.Source = new Uri("https://app.ior/index.html");

            _webUiSnapshotTimer.Tick += (_, _) => PushWebSnapshot(includeLogs: false);
            _webUiSnapshotTimer.Start();
            _subscriptionRefreshTimer.Tick += async (_, _) => await RefreshDueSubscriptionsAsync();
            _subscriptionRefreshTimer.Start();
            FormClosed += (_, _) =>
            {
                _webUiSnapshotTimer.Stop();
                _webUiSnapshotTimer.Dispose();
                _subscriptionRefreshTimer.Stop();
                _subscriptionRefreshTimer.Dispose();
                _webView?.Dispose();
            };
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowWebUiFailure("Для нового интерфейса ior требуется Microsoft Edge WebView2 Runtime.", true);
        }
        catch (Exception ex)
        {
            Log($"Web UI initialization failed: {ex.Message}");
            ShowWebUiFailure($"Не удалось открыть новый интерфейс: {ex.Message}", false);
        }
    }

    private void ShowWebUiFailure(string message, bool showInstall)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(32) };
        var title = new Label { Dock = DockStyle.Top, Height = 48, Text = "ior", ForeColor = TextColor, Font = new Font("Segoe UI", 24, FontStyle.Bold) };
        var text = new Label { Dock = DockStyle.Top, Height = 80, Text = message + "\r\nМожно продолжить в резервном интерфейсе.", ForeColor = Muted, Font = new Font("Segoe UI", 11) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, FlowDirection = FlowDirection.LeftToRight, BackColor = Bg };
        if (showInstall)
        {
            var install = new Button { Text = "Установить WebView2", Width = 170, Height = 38 };
            install.Click += (_, _) => Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            actions.Controls.Add(install);
        }
        var legacy = new Button { Text = "Открыть резервный интерфейс", Width = 210, Height = 38 };
        legacy.Click += (_, _) => { Controls.Remove(panel); panel.Dispose(); };
        actions.Controls.Add(legacy);
        panel.Controls.Add(actions);
        panel.Controls.Add(text);
        panel.Controls.Add(title);
        Controls.Add(panel);
        panel.BringToFront();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var method = root.GetProperty("method").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement.Clone() : default;
            if (_shutdownRequested) throw new InvalidOperationException("Приложение завершает подключение и восстанавливает сеть.");
            if (method is "connection.toggle" or "connection.mode" or "server.select" or "settings.update" or "route.select")
            {
                if (_webOperationRunning) throw new InvalidOperationException("Предыдущая операция ещё выполняется.");
                var operation = new ConnectionOperation(method);
                var operationId = operation.Id;
                _webOperationRunning = true;
                _webOperation = operation;
                PostWebMessage(new { type = "response", id, ok = true, result = new { accepted = true, operationId } });
                PushWebSnapshot();
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var value = await HandleWebRequestAsync(method, payload);
                        operation.Complete(value);
                    }
                    catch (Exception error)
                    {
                        Log($"Web operation {operationId} failed: {error.Message}");
                        operation.Fail(error.Message);
                    }
                    finally { _webOperationRunning = false; PushWebSnapshot(); }
                }));
                return;
            }
            var result = await HandleWebRequestAsync(method, payload);
            PostWebMessage(new { type = "response", id, ok = true, result });
        }
        catch (Exception ex)
        {
            Log($"Web command failed: {ex.Message}");
            PostWebMessage(new { type = "response", id, ok = false, error = ex.Message });
        }
    }

    private async Task<object?> HandleWebRequestAsync(string method, JsonElement payload)
    {
        switch (method)
        {
            case "state.get": _webLogDelta.Clear(); return BuildWebSnapshot();
            case "backup.export": return JsonSerializer.Serialize(_state, JsonOptions());
            case "backup.import":
            {
                var json = RequiredString(payload, "json");
                if (json.Length > 5_000_000) throw new InvalidOperationException("Резервная копия слишком велика.");
                var restored = JsonSerializer.Deserialize<AppState>(json, JsonOptions()) ?? throw new InvalidOperationException("Некорректная резервная копия.");
                if (restored.SchemaVersion > new AppState().SchemaVersion) throw new InvalidOperationException("Резервная копия создана более новой версией ior.");
                SeedDefaultRoutingSites(restored);
                NormalizeSubscriptions(restored);
                NormalizeHappRoutingProfiles(restored);
                MigrateWebUiState(restored);
                if (!CoreEngineService.CanSelect(restored.SelectedCore)) restored.SelectedCore = CoreKind.SingBox;
                ServiceMonitorCatalog.NormalizeTargets(restored);
                if (IsConnectionRunning() && !await StopSingBoxAsync()) throw new InvalidOperationException("Не удалось остановить работающее ядро для импорта резервной копии.");
                File.Copy(_statePath, _statePath + ".before-import." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json", overwrite: false);
                restored.ActiveProcessId = null;
                restored.RunningCore = null;
                restored.RunningServerId = null;
                restored.RunningServerName = null;
                restored.RunningProfileSnapshot = null;
                restored.ActiveConnectionStartedUtc = null;
                _state = restored;
                _serviceStatusStrip.SetTargets(_state.ServiceMonitorTargets);
                SaveState(); RefreshProfiles(); PushWebSnapshot();
                return null;
            }
            case "external.open":
            {
                var url = RequiredString(payload, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new InvalidOperationException("Некорректная ссылка.");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return null;
            }
            case "connection.toggle":
                if (IsNativeTunValidationRunning()) throw new InvalidOperationException("Дождитесь завершения базовой проверки Reality TUN.");
                if (!IsConnectionRunning() && !CoreEngineService.CanRun(_state, SelectedProfile(), out var connectionReason))
                    throw new InvalidOperationException(connectionReason);
                if (_state.ConnectionProfiles.Any(profile => profile.Id == _state.SelectedConnectionProfileId) && _state.Profiles.All(profile => profile.Id != _state.SelectedProfileId))
                    throw new InvalidOperationException("В активном профиле нет сервера для подключения.");
                if (!await ToggleConnectionAsync())
                    throw new InvalidOperationException("Не удалось применить подключение. Проверьте последние записи журнала: там указана причина.");
                PushWebSnapshot();
                return null;
            case "connection.mode":
                var requestedTun = RequiredString(payload, "mode").Equals("tun", StringComparison.OrdinalIgnoreCase);
                if (_connectionTransitionInProgress)
                    throw new InvalidOperationException("Переключение подключения ещё выполняется. Дождитесь его завершения.");
                if (IsConnectionRunning())
                {
                    var oldMode = _state.TunEnabled;
                    _state.TunEnabled = requestedTun;
                    var runningProfile = _state.RunningProfileSnapshot ?? _state.Profiles.FirstOrDefault(profile => profile.Id == _state.RunningServerId);
                    var canRun = CoreEngineService.CanRun(_state, runningProfile, out var modeReason);
                    _state.TunEnabled = oldMode;
                    if (!canRun) throw new InvalidOperationException(modeReason);
                }
                if (!await ApplyConnectionModeAsync(requestedTun))
                    throw new InvalidOperationException("Новый режим не запустился. Прежний режим и подключение восстановлены; подробности в журнале.");
                PushWebSnapshot(); return null;
            case "server.select":
            {
                _runtimeConnectionWarning = null;
                if (_connectionTransitionInProgress) throw new InvalidOperationException("Дождитесь завершения текущего переключения.");
                var target = FindProfile(RequiredString(payload, "id"));
                var wasRunning = IsConnectionRunning();
                if (wasRunning && target.Id != _state.RunningServerId && !OptionalBool(payload, "confirmReconnect"))
                    throw new InvalidOperationException("Смена работающего сервера требует подтверждения переподключения.");
                var oldSelection = _state.SelectedProfileId;
                var oldRunningProfile = _state.RunningProfileSnapshot ?? _state.Profiles.FirstOrDefault(profile => profile.Id == _state.RunningServerId);
                var oldRunningCore = _state.RunningCore;
                _connectionTransitionInProgress = wasRunning && target.Id != _state.RunningServerId;
                try
                {
                    SelectProfile(target, save: true);
                    if (wasRunning && target.Id != _state.RunningServerId)
                    {
                        if (!await StopSingBoxAsync())
                        {
                            _state.SelectedProfileId = oldSelection;
                            SaveState(); RefreshProfiles(); RefreshMainPreview();
                            throw new InvalidOperationException("Не удалось остановить прежнее подключение; сервер не переключён.");
                        }
                        if (!await StartSingBoxAsync())
                        {
                            if (oldRunningCore.HasValue) _state.SelectedCore = oldRunningCore.Value;
                            var restored = oldRunningProfile is not null && await StartSingBoxAsync(oldRunningProfile);
                            _state.SelectedProfileId = oldSelection;
                            SaveState(); RefreshProfiles(); RefreshMainPreview();
                            throw new InvalidOperationException(restored
                                ? "Новый сервер не запустился; прежнее подключение восстановлено."
                                : "Новый сервер не запустился, прежнее подключение не удалось восстановить. Проверьте журнал.");
                        }
                    }
                }
                finally { _connectionTransitionInProgress = false; }
                PushWebSnapshot();
                return null;
            }
            case "server.import":
                if (!await ImportProfilesFromTextAsync(RequiredString(payload, "text"), OptionalString(payload, "name"), OptionalString(payload, "kind") == "config"))
                    throw new InvalidOperationException("Профиль не был импортирован. Проверьте ссылку или конфигурацию.");
                PushWebSnapshot();
                return null;
            case "server.rename":
            {
                var profile = FindProfile(RequiredString(payload, "id"));
                profile.Name = RequiredString(payload, "name").Trim();
                SaveState(); RefreshProfiles(); PushWebSnapshot(); return null;
            }
            case "server.get": return FindProfile(RequiredString(payload, "id"));
            case "server.copy":
            {
                var source = FindProfile(RequiredString(payload, "id"));
                var copy = JsonSerializer.Deserialize<ProxyProfile>(JsonSerializer.Serialize(source, WebJsonOptions), WebJsonOptions)!;
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Name = source.Name + " (копия)";
                copy.SubscriptionId = null;
                copy.SubscriptionUploadBytes = null; copy.SubscriptionDownloadBytes = null;
                copy.SubscriptionTotalBytes = null; copy.SubscriptionExpireUtc = null;
                _state.Profiles.Add(copy);
                SaveState(); RefreshProfiles(); PushWebSnapshot();
                return copy;
            }
            case "server.save":
            {
                var profile = Deserialize<ProxyProfile>(payload);
                var current = FindProfile(profile.Id);
                if (current.SubscriptionId is not null) throw new InvalidOperationException("Сервер подписки доступен только для чтения. Создайте локальную копию.");
                ValidateEditableProfile(profile);
                profile.SubscriptionId = null;
                var index = _state.Profiles.FindIndex(item => item.Id == profile.Id);
                _state.Profiles[index] = profile;
                var restart = _state.SelectedProfileId == profile.Id && IsConnectionRunning();
                SaveState(); RefreshProfiles(); ValidateSelectedConfigIfEnabled();
                if (restart) await RestartConnectionAsync();
                PushWebSnapshot();
                return null;
            }
            case "server.delete":
            {
                var id = RequiredString(payload, "id");
                _state.Profiles.RemoveAll(profile => profile.Id == id);
                if (_state.SelectedProfileId == id) _state.SelectedProfileId = _state.Profiles.FirstOrDefault()?.Id;
                SaveState(); RefreshProfiles(); PushWebSnapshot(); return null;
            }
            case "subscription.refresh":
                await RefreshSubscriptionAsync(FindSubscription(RequiredString(payload, "id")), showErrors: false);
                PushWebSnapshot(); return null;
            case "subscription.rename":
            {
                var subscription = FindSubscription(RequiredString(payload, "id"));
                subscription.Name = RequiredString(payload, "name").Trim();
                subscription.NameIsCustom = true;
                SaveState(); RefreshProfiles(); PushWebSnapshot(); return null;
            }
            case "subscription.delete":
                SubscriptionService.Remove(_state, FindSubscription(RequiredString(payload, "id")));
                SaveState(); RefreshProfiles(); PushWebSnapshot(); return null;
            case "settings.update":
            {
                if (IsNativeTunValidationRunning() && (RequiresConnectionRestart(payload) || payload.TryGetProperty("selectedCore", out _)))
                    throw new InvalidOperationException("Сетевые настройки можно изменить после завершения базовой проверки TUN.");
                var requestedCore = _state.SelectedCore;
                var coreSwitch = payload.TryGetProperty("selectedCore", out var selectedCoreValue) &&
                    Enum.TryParse<CoreKind>(selectedCoreValue.GetString(), true, out requestedCore) &&
                    requestedCore != _state.SelectedCore;
                var restart = !coreSwitch && IsConnectionRunning() && RequiresConnectionRestart(payload);
                if (coreSwitch) await SwitchCoreAsync(requestedCore);
                ApplyWebSettings(payload); SaveState();
                if (_trayPopup is { IsDisposed: false, Visible: true } popup)
                {
                    if (_state.MinimizeToTray) ShowTrayPopup(popup.AnchorPoint);
                    else popup.Close();
                }
                if (restart) await RestartConnectionAsync();
                PushWebSnapshot(); return null;
            }
            case "connectionProfile.save":
            {
                var profile = Deserialize<ConnectionWorkspaceProfile>(payload);
                var active = profile.Id == _state.SelectedConnectionProfileId;
                var wasRunning = active && IsConnectionRunning();
                SaveConnectionProfile(profile);
                if (active) SelectConnectionProfile(profile.Id);
                if (wasRunning) await RestartConnectionAsync();
                return null;
            }
            case "connectionProfile.select":
            {
                var wasRunning = IsConnectionRunning();
                SelectConnectionProfile(RequiredString(payload, "id"));
                if (wasRunning) await RestartConnectionAsync();
                return null;
            }
            case "iorTrafficMode.set":
            {
                var profile = _state.ConnectionProfiles.FirstOrDefault(item => item.Id == _state.SelectedConnectionProfileId)
                    ?? throw new InvalidOperationException("Выберите профиль подключения.");
                if (!Enum.TryParse<IorTrafficMode>(RequiredString(payload, "mode"), true, out var mode))
                    throw new InvalidOperationException("Неизвестный режим ior-core.");
                if(mode!=IorTrafficMode.Standard)throw new InvalidOperationException("Игровой режим удалён; ior-core использует основной режим.");
                profile.IorTrafficMode = IorTrafficMode.Standard;
                SaveState(); PushWebSnapshot(); return null;
            }
            case "connectionProfile.delete":
                DeleteConnectionProfile(RequiredString(payload, "id")); return null;
            case "route.save":
            {
                var wasRunning = IsConnectionRunning();
                var candidate = Deserialize<HappRoutingProfile>(payload);
                var previousSelection = _state.SelectedHappRoutingProfileId;
                var previousProfile = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == candidate.Id);
                SaveRoutingProfile(candidate);
                if (wasRunning && !CoreEngineService.CanRun(_state, SelectedProfile(), out var reason))
                {
                    var index = _state.HappRoutingProfiles.FindIndex(item => item.Id == candidate.Id);
                    if (index >= 0)
                    {
                        if (previousProfile is null) _state.HappRoutingProfiles.RemoveAt(index);
                        else _state.HappRoutingProfiles[index] = previousProfile;
                    }
                    _state.SelectedHappRoutingProfileId = previousSelection;
                    SaveState(); PushWebSnapshot();
                    throw new InvalidOperationException(reason);
                }
                if (wasRunning) await RestartConnectionAsync();
                return null;
            }
            case "route.select":
            {
                var wasRunning = IsConnectionRunning();
                var previouslySelectedRoute = _state.SelectedHappRoutingProfileId;
                SelectRoutingProfile(RequiredString(payload, "id"));
                if (wasRunning)
                {
                    if (!CoreEngineService.CanRun(_state, SelectedProfile(), out var reason))
                    {
                        _state.SelectedHappRoutingProfileId = previouslySelectedRoute;
                        SaveState(); PushWebSnapshot();
                        throw new InvalidOperationException(reason);
                    }
                    await RestartConnectionAsync();
                }
                return null;
            }
            case "route.rename":
            {
                var route = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == RequiredString(payload, "id")) ?? throw new InvalidOperationException("Маршрут не найден.");
                route.Name = RequiredString(payload, "name").Trim(); SaveState(); PushWebSnapshot(); return null;
            }
            case "route.delete":
            {
                var wasRunning = IsConnectionRunning();
                DeleteRoutingProfile(RequiredString(payload, "id"));
                if (wasRunning) await RestartConnectionAsync();
                return null;
            }
            case "route.import":
            {
                var wasRunning = IsConnectionRunning();
                var previouslySelectedRoute = _state.SelectedHappRoutingProfileId;
                await ImportRoutingProfileAsync(RequiredString(payload, "text"), OptionalString(payload, "name"));
                if (wasRunning && _state.SelectedHappRoutingProfileId != previouslySelectedRoute)
                {
                    if (!CoreEngineService.CanRun(_state, SelectedProfile(), out _))
                    {
                        _state.SelectedHappRoutingProfileId = previouslySelectedRoute;
                        SaveState(); PushWebSnapshot();
                    }
                    else await RestartConnectionAsync();
                }
                return null;
            }
            case "route.convert":
            {
                var original = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == RequiredString(payload, "id")) ?? throw new InvalidOperationException("Маршрут не найден.");
                if (original.Source != RoutingProfileSource.Clash) throw new InvalidOperationException("Конвертация доступна для Clash-профилей.");
                if (original.ClashOrderedRules.Count > 0 || original.ClashProxyGroups.Count > 0 || original.ClashUnmappedRules.Count > 0)
                    throw new InvalidOperationException("Конвертация этого Clash-профиля в списки ior изменит порядок правил или выбор группы. Оставьте исходный профиль Clash; копия без этих свойств не создаётся.");
                var copy = JsonSerializer.Deserialize<HappRoutingProfile>(JsonSerializer.Serialize(original, WebJsonOptions), WebJsonOptions)!;
                copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " (ior)"; copy.Source = RoutingProfileSource.Ior;
                copy.OriginalClashYaml = null; copy.ClashUnmappedRules = []; copy.ClashProxyGroups = []; copy.ClashOrderedRules = [];
                _state.HappRoutingProfiles.Add(copy); _state.SelectedHappRoutingProfileId = copy.Id;
                SaveState(); PushWebSnapshot(); return copy;
            }
            case "route.share": return ExportRoutingProfile(RequiredString(payload, "id"));
            case "service.save":
                SaveServiceTarget(Deserialize<ServiceMonitorTarget>(payload)); return null;
            case "service.visibility":
            {
                var target = _state.ServiceMonitorTargets.FirstOrDefault(item => item.Id == RequiredString(payload, "id"))
                    ?? throw new InvalidOperationException("Сервис не найден.");
                target.Visible = payload.GetProperty("visible").GetBoolean();
                SaveState(); PushWebSnapshot(); return null;
            }
            case "service.delete":
                _state.ServiceMonitorTargets.RemoveAll(target => target.Id == RequiredString(payload, "id"));
                _serviceStatusStrip.SetTargets(_state.ServiceMonitorTargets); SaveState(); PushWebSnapshot(); return null;
            case "logs.clear": _logs.Clear(); PushWebSnapshot(); return null;
            case "diagnostics.export": return await ExportDiagnosticsAsync();
            case "config.get":
                if (_state.AutoValidateConfig) ValidateSelectedConfig();
                return SelectedProfile() is { } selected ? SingBoxConfigBuilder.Build(_state, selected) : "";
            case "config.preview":
                return BuildPreviewConfig(FindProfile(RequiredString(payload, "id")));
            case "config.validatePreview":
                return await ValidatePreviewConfigAsync(FindProfile(RequiredString(payload, "id")));
            case "config.validate":
            {
                return ValidateSelectedConfig(force: true);
            }
            case "app.update.download":
            {
                if (_availableUpdate is null || _updateUiState is not (UpdateUiState.Available or UpdateUiState.Failed))
                    throw new InvalidOperationException("Нет доступного обновления для загрузки.");
                _ = DownloadUpdateAsync();
                return new { accepted = true };
            }
            case "app.update.apply": ApplyStagedUpdate(); return new { accepted = true };
            default: throw new InvalidOperationException($"Unknown web command: {method}");
        }
    }

    private async Task<object> ValidatePreviewConfigAsync(ProxyProfile profile)
    {
        var previewPath = Path.Combine(_appDir, $"config.preview.{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(previewPath, BuildPreviewConfig(profile));
            if (_state.SelectedCore == CoreKind.XrayCore)
            {
                var check = await CheckXrayConfigAsync(CoreEngineService.XrayCorePath(_state), previewPath);
                return new { valid = check.Valid, output = check.Output };
            }
            var start = new ProcessStartInfo
            {
                FileName = _state.SingBoxPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("check");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(previewPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проверку sing-box.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return new { valid = false, output = "Время проверки sing-box истекло." };
            }
            var output = (await stdout + Environment.NewLine + await stderr).Trim();
            return new { valid = process.ExitCode == 0, output = output.Length == 0 ? (process.ExitCode == 0 ? "Конфигурация корректна." : "sing-box отклонил конфигурацию.") : output };
        }
        finally
        {
            if (File.Exists(previewPath)) File.Delete(previewPath);
        }
    }

    private string BuildPreviewConfig(ProxyProfile profile) => _state.SelectedCore switch
    {
        CoreKind.SingBox => SingBoxConfigBuilder.Build(_state, profile),
        CoreKind.XrayCore => XrayConfigBuilder.Build(_state, profile),
        _ => throw new InvalidOperationException("У ior-core ещё нет исполняемого сетевого конфига.")
    };

    private object BuildWebSnapshot(bool includeLogs = true)
    {
        var connected = IsConnectionRunning();
        var workspace = _state.ConnectionProfiles.FirstOrDefault(profile => profile.Id == _state.SelectedConnectionProfileId);
        var selected = workspace is null
            ? SelectedProfile()
            : _state.Profiles.FirstOrDefault(profile => profile.Id == _state.SelectedProfileId);
        var serviceStatuses = _serviceStatusStrip.StatusSnapshot();
        var latencies = _latencyMonitor.Snapshot();
        var availableServerIds = workspace is null
            ? _state.Profiles.Select(profile => profile.Id).ToArray()
            : _state.Profiles.Where(profile => profile.SubscriptionId == workspace.SubscriptionId || workspace.ConfigIds.Contains(profile.Id) ||
                (connected && profile.Id == _state.RunningServerId)).Select(profile => profile.Id).ToArray();
        var now = DateTime.UtcNow;
        string? currentConfig = null;
        if (selected is not null)
        {
            try { currentConfig = SingBoxConfigBuilder.Build(_state, selected); }
            catch (Exception ex) when (ex is FormatException or NotSupportedException) { /* Invalid/unsupported profiles are reported on validation, never crash snapshot updates. */ }
        }
        var currentValidation = currentConfig == _validatedConfig ? _configValidationResult : null;
        return new
        {
            version = AppVersion,
            update = new
            {
                status = _updateUiState.ToString().ToLowerInvariant(),
                tag = _availableUpdate?.Tag,
                percent = _updatePercent,
                error = _updateError,
                hasAsset = !string.IsNullOrWhiteSpace(_availableUpdate?.AssetDownloadUrl)
            },
            connected,
            connecting = _connectionTransitionInProgress || _webOperationRunning,
            operation = _webOperation,
            diagnosticRunId = _connectionDiagnostics?.RunId,
            connectedSeconds = _state.ActiveConnectionStartedUtc is { } started ? Math.Max(0, (long)(now - started).TotalSeconds) : 0,
            selectedServerId = selected?.Id,
            runningServerId = connected ? _state.RunningServerId : null,
            runningServerName = connected ? _state.RunningServerName : null,
            runtimeConnectionWarning = connected ? _runtimeConnectionWarning : null,
            tunReadiness = connected ? _state.NativeTunReadiness : null,
            activeRoutingProfileId = _state.SelectedHappRoutingProfileId,
            activeConnectionProfileId = _state.SelectedConnectionProfileId,
            core = new { selected = _state.SelectedCore.ToString(), activeBackend = CoreEngineService.ActiveBackend(_state, connected), buildId = _state.RunningCore == CoreKind.IorCore || _state.SelectedCore == CoreKind.IorCore ? CoreEngineService.NativeBuildId(_state) : null, protocol = connected ? _state.RunningProfileSnapshot?.Protocol.ToString() : null, mode = _state.TunEnabled ? "TUN" : "Proxy", options = CoreEngineService.AvailableCores(_state) },
            configValidation = new { checkedConfig = currentValidation.HasValue, valid = currentValidation },
            availableServerIds,
            traffic = new { download = _downloadSpeedValue.Text, upload = _uploadSpeedValue.Text, ping = _lastLatencyMs, quality = _qualityValue.Text },
            diagnostics = _loadMonitor.Sample(connected ? _state.ActiveProcessId : null,
                _state.TunEnabled ? "TUN" : "Proxy", _state.SystemProxyApplied),
            settings = new
            {
                tunEnabled = _state.TunEnabled, mixedProxyEnabled = _state.MixedProxyEnabled, mixedProxyPort = _state.MixedProxyPort,
                tunMtu = _state.TunMtu, sniffEnabled = _state.SniffEnabled, autoRoute = _state.AutoRoute,
                strictRoute = _state.StrictRoute, stackSystem = _state.StackSystem, allowInsecureCertificates = _state.AllowInsecureCertificates,
                setSystemProxy = _state.SetSystemProxy, tunAddress = _state.TunAddress, dnsServer = _state.DnsServer,
                dnsStrategy = _state.DnsStrategy, logLevel = _state.LogLevel, language = _state.Language.ToString(),
                theme = _state.Theme.ToString(), navPanelPosition = _state.NavPanelPosition.ToString(), showNavTooltips = _state.ShowNavTooltips,
                showNavSeparators = _state.ShowNavSeparators, usePanelGlass = _state.UsePanelGlass,
                minimizeToTray = _state.MinimizeToTray, autoRefreshSubscriptions = _state.AutoRefreshSubscriptions,
                autoValidateConfig = _state.AutoValidateConfig, powerSavingMode = _state.PowerSavingMode,
                experimentalCoreTesting = _state.ExperimentalCoreTesting, realityUdp443Enabled = _state.RealityUdp443Enabled
                , selectedCore = _state.SelectedCore.ToString(), bootstrapDnsServer = _state.BootstrapDnsServer
            },
            servers = _state.Profiles.Select(profile => new
            {
                profile.Id, profile.Name, protocol = profile.Protocol.ToString(), profile.Server, profile.Port, profile.CountryCode,
                profile.CountryName, profile.SubscriptionId, uploadBytes = profile.SubscriptionUploadBytes,
                downloadBytes = profile.SubscriptionDownloadBytes, totalBytes = profile.SubscriptionTotalBytes,
                expireUtc = profile.SubscriptionExpireUtc,
                ping = latencies.TryGetValue(ServerTreeView.EndpointKey(profile), out var latency) ? latency.Current : null
            }),
            subscriptions = _state.Subscriptions,
            routes = _state.HappRoutingProfiles,
            connectionProfiles = _state.ConnectionProfiles,
            services = _state.ServiceMonitorTargets.Select(service => new
            {
                service.Id, service.Name, service.Url, service.PresetId, service.Visible,
                iconData = ServiceIconData(service.Url),
                status = serviceStatuses.GetValueOrDefault(service.Id, "unknown")
            }),
            logs = includeLogs ? _logs.TakeLast(500).ToArray() : null
        };
    }

    private void PushWebSnapshot(bool includeLogs = true)
    {
        if (!_webUiReady || _webView?.CoreWebView2 is null || IsDisposed) return;
        PostWebMessage(new { type = "event", name = "state", payload = BuildWebSnapshot(includeLogs) });
        if (includeLogs) _webLogDelta.Clear();
        else if (_webLogDelta.Count > 0)
        {
            PostWebMessage(new { type = "event", name = "logs", payload = _webLogDelta.ToArray() });
            _webLogDelta.Clear();
        }
    }

    private void PostWebMessage(object message)
    {
        if (_webView?.CoreWebView2 is null) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, WebJsonOptions));
    }

    private ProxyProfile FindProfile(string id) => _state.Profiles.FirstOrDefault(profile => profile.Id == id) ?? throw new InvalidOperationException("Сервер не найден.");
    private static void ValidateEditableProfile(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535)
            throw new InvalidOperationException("Укажите название, адрес сервера и порт 1–65535.");
        if (profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess && !Guid.TryParse(profile.UserId, out _))
            throw new InvalidOperationException("Для VLESS/VMess нужен корректный UUID.");
        if (profile.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks && string.IsNullOrWhiteSpace(profile.Password))
            throw new InvalidOperationException("Пароль не может быть пустым.");
        if (profile.Protocol == ProxyProtocol.Shadowsocks && string.IsNullOrWhiteSpace(profile.Method))
            throw new InvalidOperationException("Укажите метод шифрования Shadowsocks.");
        if (profile.Protocol == ProxyProtocol.WireGuard && (string.IsNullOrWhiteSpace(profile.PrivateKey) || string.IsNullOrWhiteSpace(profile.PeerPublicKey)))
            throw new InvalidOperationException("Для WireGuard нужны приватный и публичный ключи.");
    }
    private ProxySubscription FindSubscription(string id) => _state.Subscriptions.FirstOrDefault(subscription => subscription.Id == id) ?? throw new InvalidOperationException("Подписка не найдена.");
    private static T Deserialize<T>(JsonElement payload) => JsonSerializer.Deserialize<T>(payload.GetRawText(), WebJsonOptions) ?? throw new InvalidOperationException("Некорректные данные.");
    private static string RequiredString(JsonElement payload, string property) => OptionalString(payload, property) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Поле {property} обязательно.");
    private static string? OptionalString(JsonElement payload, string property) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static bool OptionalBool(JsonElement payload, string property) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private void SaveConnectionProfile(ConnectionWorkspaceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("Название профиля не может быть пустым.");
        profile.ConfigIds = profile.ConfigIds
            .Select(value => _state.Profiles.FirstOrDefault(server => server.Id == value || server.Name.Equals(value, StringComparison.OrdinalIgnoreCase))?.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct()
            .ToList();
        if (!_state.Profiles.Any(server => server.Id == profile.DefaultServerId))
            profile.DefaultServerId = _state.Profiles.FirstOrDefault(server => server.SubscriptionId == profile.SubscriptionId || profile.ConfigIds.Contains(server.Id))?.Id;
        var index = _state.ConnectionProfiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0) _state.ConnectionProfiles[index] = profile; else _state.ConnectionProfiles.Add(profile);
        _state.SelectedConnectionProfileId ??= profile.Id;
        SaveState(); PushWebSnapshot();
    }

    private void SelectConnectionProfile(string id)
    {
        var profile = _state.ConnectionProfiles.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Профиль подключения не найден.");
        _state.SelectedConnectionProfileId = id;
        var available = _state.Profiles.Where(server => server.SubscriptionId == profile.SubscriptionId || profile.ConfigIds.Contains(server.Id)).ToList();
        _state.SelectedProfileId = available.Any(server => server.Id == profile.DefaultServerId) ? profile.DefaultServerId : available.FirstOrDefault()?.Id;
        if (_state.HappRoutingProfiles.Any(route => route.Id == profile.RoutingProfileId)) _state.SelectedHappRoutingProfileId = profile.RoutingProfileId;
        SaveState(); RefreshProfiles(); PushWebSnapshot();
    }

    private void DeleteConnectionProfile(string id)
    {
        _state.ConnectionProfiles.RemoveAll(profile => profile.Id == id);
        if (_state.ConnectionProfiles.Count == 0) _state.ConnectionProfiles.Add(new ConnectionWorkspaceProfile());
        if (_state.SelectedConnectionProfileId == id) _state.SelectedConnectionProfileId = _state.ConnectionProfiles[0].Id;
        SaveState(); PushWebSnapshot();
    }

    private void SaveRoutingProfile(HappRoutingProfile profile)
    {
        profile = RoutingProfileEditor.Prepare(profile, _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == profile.Id));
        var index = _state.HappRoutingProfiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0) _state.HappRoutingProfiles[index] = profile; else _state.HappRoutingProfiles.Add(profile);
        _state.SelectedHappRoutingProfileId = profile.Id;
        SaveState(); PushWebSnapshot();
    }

    private void SelectRoutingProfile(string id)
    {
        var profile = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Маршрут не найден.");
        var compatibility = profile.Source == RoutingProfileSource.Clash && !string.IsNullOrWhiteSpace(profile.OriginalClashYaml)
            ? ClashProfileParser.Parse(profile.OriginalClashYaml, profile.Name)
            : profile;
        if (compatibility.ClashUnmappedRules.Count > 0 &&
            !(_state.SelectedCore == CoreKind.XrayCore &&
              XrayClashRuleCompiler.TryCompile(compatibility, out _, out _, out _)))
            throw new InvalidOperationException($"В Clash-профиле есть {compatibility.ClashUnmappedRules.Count} правил, которые выбранное ядро пока не умеет применить без изменения порядка или целевой группы. Профиль сохранён, но пока не может быть активирован.");
        if (!ReferenceEquals(compatibility, profile))
        {
            profile.ClashUnmappedRules = compatibility.ClashUnmappedRules;
            profile.ClashOrderedRules = compatibility.ClashOrderedRules;
        }
        _state.SelectedHappRoutingProfileId = id; SaveState(); PushWebSnapshot();
    }

    private void DeleteRoutingProfile(string id)
    {
        var removedIndex = _state.HappRoutingProfiles.FindIndex(profile => profile.Id == id);
        _state.HappRoutingProfiles.RemoveAll(profile => profile.Id == id);
        if (_state.HappRoutingProfiles.Count == 0) _state.HappRoutingProfiles.Add(new HappRoutingProfile { Name = "Без маршрутизации", Source = RoutingProfileSource.Ior, GlobalProxy = true });
        if (_state.SelectedHappRoutingProfileId == id) _state.SelectedHappRoutingProfileId = _state.HappRoutingProfiles[Math.Clamp(removedIndex, 0, _state.HappRoutingProfiles.Count - 1)].Id;
        SaveState(); PushWebSnapshot();
    }

    private async Task ImportRoutingProfileAsync(string text, string? name)
    {
        text = text.Trim();
        HappRoutingProfile profile;
        if (text.StartsWith("clash://", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\nrules:", StringComparison.OrdinalIgnoreCase))
        {
            var yaml = text;
            if (text.StartsWith("clash://", StringComparison.OrdinalIgnoreCase))
            {
                var marker = text.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
                if (marker < 0) throw new InvalidOperationException("В Clash-ссылке нет параметра url.");
                yaml = Uri.UnescapeDataString(text[(marker + 4)..].Split('&')[0]);
            }
            if (Uri.TryCreate(yaml, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("Clash-подписка должна использовать HTTP или HTTPS.");
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                yaml = await client.GetStringAsync(uri);
                if (yaml.Length > 2_000_000) throw new InvalidOperationException("Clash-профиль слишком велик.");
            }
            profile = ClashProfileParser.Parse(yaml, name);
            _state.HappRoutingProfiles.Add(profile);
            if (profile.ClashUnmappedRules.Count == 0 ||
                (_state.SelectedCore == CoreKind.XrayCore &&
                 XrayClashRuleCompiler.TryCompile(profile, out _, out _, out _)))
                _state.SelectedHappRoutingProfileId = profile.Id;
            SaveState(); PushWebSnapshot(); return;
        }
        if (text.StartsWith("ior-route://", StringComparison.OrdinalIgnoreCase))
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text["ior-route://".Length..]));
            profile = JsonSerializer.Deserialize<HappRoutingProfile>(json, WebJsonOptions) ?? throw new InvalidOperationException("Некорректная ссылка ior-route.");
            profile.Id = Guid.NewGuid().ToString("N"); profile.Source = RoutingProfileSource.Ior;
        }
        else if (text.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var explicitlyLocal = (root.TryGetProperty("source", out var source) && source.GetString()?.Equals("ior", StringComparison.OrdinalIgnoreCase) == true) || root.TryGetProperty("version", out _);
            if (explicitlyLocal)
            {
                profile = JsonSerializer.Deserialize<HappRoutingProfile>(text, WebJsonOptions) ?? throw new InvalidOperationException("Некорректный JSON маршрута.");
                profile.Source = RoutingProfileSource.Ior;
            }
            else
            {
                profile = new HappRoutingProfile { Name = "Happ профиль", Source = RoutingProfileSource.Happ, Links = [text] };
            }
            profile.Id = Guid.NewGuid().ToString("N");
        }
        else
        {
            var links = ParseRoutingLinks(text);
            if (links.Count == 0) throw new InvalidOperationException("Happ-ссылка не распознана.");
            profile = new HappRoutingProfile { Name = string.IsNullOrWhiteSpace(name) ? DefaultHappRoutingProfileName(links) : name.Trim(), Source = RoutingProfileSource.Happ, Links = links };
        }
        if (!string.IsNullOrWhiteSpace(name)) profile.Name = name.Trim();
        _state.HappRoutingProfiles.Add(profile); _state.SelectedHappRoutingProfileId = profile.Id; SaveState(); PushWebSnapshot();
    }

    private string ExportRoutingProfile(string id)
    {
        var profile = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Маршрут не найден.");
        if (profile.Source == RoutingProfileSource.Clash) return profile.OriginalClashYaml ?? "";
        if (profile.Source == RoutingProfileSource.Happ) return string.Join(Environment.NewLine, profile.Links);
        var json = JsonSerializer.Serialize(profile, WebJsonOptions);
        return "ior-route://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private void SaveServiceTarget(ServiceMonitorTarget target)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Введите корректный HTTP или HTTPS адрес.");
        var index = _state.ServiceMonitorTargets.FindIndex(item => item.Id == target.Id);
        if (index >= 0) _state.ServiceMonitorTargets[index] = target; else _state.ServiceMonitorTargets.Add(target);
        ServiceMonitorCatalog.NormalizeTargets(_state); _serviceStatusStrip.SetTargets(_state.ServiceMonitorTargets); SaveState(); PushWebSnapshot();
        _ = LoadServiceIconAsync(target).ContinueWith(_ =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { _serviceIconDataCache.Remove(target.Url); PushWebSnapshot(); }));
        }, TaskScheduler.Default);
    }

    private string? ServiceIconData(string url)
    {
        if (_serviceIconDataCache.TryGetValue(url, out var cached)) return cached;
        var bytes = _faviconCache.TryGetCached(url);
        if (bytes is null) { _serviceIconDataCache[url] = null; return null; }
        var mime = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
            : bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
            : bytes.Length >= 6 && Encoding.ASCII.GetString(bytes, 0, 3) == "GIF" ? "image/gif"
            : "image/x-icon";
        var data = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        _serviceIconDataCache[url] = data;
        return data;
    }

    private void ApplyWebSettings(JsonElement payload)
    {
        // Validate before changing any of the settings in this request.
        foreach (var property in payload.EnumerateObject())
        {
            switch (property.Name)
            {
                case "tunEnabled": _state.TunEnabled = property.Value.GetBoolean(); break;
                case "mixedProxyEnabled": _state.MixedProxyEnabled = property.Value.GetBoolean(); break;
                case "mixedProxyPort": _state.MixedProxyPort = Math.Clamp(property.Value.GetInt32(), 1, 65535); break;
                case "tunMtu": _state.TunMtu = Math.Clamp(property.Value.GetInt32(), 1280, 10000); break;
                case "sniffEnabled": _state.SniffEnabled = property.Value.GetBoolean(); break;
                case "autoRoute": _state.AutoRoute = property.Value.GetBoolean(); break;
                case "strictRoute": _state.StrictRoute = property.Value.GetBoolean(); break;
                case "stackSystem": _state.StackSystem = property.Value.GetBoolean(); break;
                case "allowInsecureCertificates": _state.AllowInsecureCertificates = property.Value.GetBoolean(); break;
                case "setSystemProxy": _state.SetSystemProxy = property.Value.GetBoolean(); break;
                case "tunAddress": _state.TunAddress = property.Value.GetString() ?? _state.TunAddress; break;
                case "dnsServer": _state.DnsServer = property.Value.GetString() ?? _state.DnsServer; break;
                case "bootstrapDnsServer": _state.BootstrapDnsServer = BootstrapDnsResolver.ServerAddress(property.Value.GetString()).ToString(); break;
                case "selectedCore" when Enum.TryParse<CoreKind>(property.Value.GetString(), true, out var core):
                    _runtimeConnectionWarning = null;
                    if (!CoreEngineService.CanSelect(core)) throw new InvalidOperationException("Неизвестное ядро.");
                    _state.SelectedCore = core;
                    break;
                case "dnsStrategy": _state.DnsStrategy = property.Value.GetString() ?? _state.DnsStrategy; break;
                case "logLevel": _state.LogLevel = property.Value.GetString() ?? _state.LogLevel; break;
                case "powerSavingMode": _state.PowerSavingMode = property.Value.GetBoolean(); ApplyPowerSavingMode(); break;
                case "experimentalCoreTesting": _state.ExperimentalCoreTesting = property.Value.GetBoolean(); break;
                case "realityUdp443Enabled": _state.RealityUdp443Enabled = property.Value.GetBoolean(); break;
                case "minimizeToTray": _state.MinimizeToTray = property.Value.GetBoolean(); _trayIcon.Visible = _state.MinimizeToTray; break;
                case "autoRefreshSubscriptions": _state.AutoRefreshSubscriptions = property.Value.GetBoolean(); break;
                case "autoValidateConfig": _state.AutoValidateConfig = property.Value.GetBoolean(); if (_state.AutoValidateConfig) ValidateSelectedConfigIfEnabled(); break;
                case "showNavTooltips": _state.ShowNavTooltips = property.Value.GetBoolean(); break;
                case "showNavSeparators": _state.ShowNavSeparators = property.Value.GetBoolean(); break;
                case "usePanelGlass": _state.UsePanelGlass = property.Value.GetBoolean(); break;
                case "navPanelPosition" when Enum.TryParse<NavPanelPosition>(property.Value.GetString(), true, out var position): _state.NavPanelPosition = position; break;
                case "theme" when Enum.TryParse<AppTheme>(property.Value.GetString(), true, out var theme): _state.Theme = theme; break;
                case "language" when Enum.TryParse<AppLanguage>(property.Value.GetString(), true, out var language): _state.Language = language; break;
                case "subscriptionRefresh" when property.Value.ValueKind == JsonValueKind.Object:
                    foreach (var item in property.Value.EnumerateObject())
                    {
                        var subscription = _state.Subscriptions.FirstOrDefault(value => value.Id == item.Name);
                        if (subscription is null) continue;
                        if (item.Value.TryGetProperty("enabled", out var enabled)) subscription.AutoRefreshEnabled = enabled.GetBoolean();
                        if (item.Value.TryGetProperty("intervalHours", out var interval))
                        {
                            subscription.RefreshIntervalHours = Math.Clamp(interval.GetInt32(), 1, 168);
                            subscription.RefreshIntervalIsCustom = true;
                        }
                        SubscriptionRefreshSchedule.ScheduleNext(subscription, DateTime.UtcNow);
                    }
                    break;
            }
        }
    }

    private static bool RequiresConnectionRestart(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        var liveSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tunEnabled", "mixedProxyEnabled", "mixedProxyPort", "tunMtu", "sniffEnabled", "autoRoute", "strictRoute",
            "stackSystem", "allowInsecureCertificates", "setSystemProxy", "tunAddress", "dnsServer", "dnsStrategy", "logLevel"
            , "selectedCore", "bootstrapDnsServer"
        };
        return payload.EnumerateObject().Any(property => liveSettings.Contains(property.Name));
    }

    private async Task RestartConnectionAsync()
    {
        if (!await StopSingBoxAsync()) throw new InvalidOperationException("Не удалось остановить прежнее подключение для перезапуска.");
        if (_state.Profiles.All(profile => profile.Id != _state.SelectedProfileId)) return;
        if (!await StartSingBoxAsync()) await ShowConnectionFailedAsync();
    }

    private async Task SwitchCoreAsync(CoreKind target)
    {
        if (!CoreEngineService.CanSelect(target)) throw new InvalidOperationException("Неизвестное ядро.");
        var previous = _state.SelectedCore;
        var wasRunning = IsConnectionRunning();
        var runningProfile = wasRunning
            ? _state.RunningProfileSnapshot ?? _state.Profiles.FirstOrDefault(profile => profile.Id == _state.RunningServerId)
            : null;
        _state.SelectedCore = target;
        var supported = CoreEngineService.CanRun(_state, wasRunning ? runningProfile : SelectedProfile(), out var reason);
        _state.SelectedCore = previous;
        if (wasRunning && !supported) throw new InvalidOperationException(reason);
        if (!wasRunning) { _state.SelectedCore = target; return; }

        if (!await StopSingBoxAsync()) throw new InvalidOperationException("Не удалось остановить прежнее ядро; переключение отменено.");
        _state.SelectedCore = target;
        if (await StartSingBoxAsync(runningProfile)) return;

        _state.SelectedCore = previous;
        var restored = await StartSingBoxAsync(runningProfile);
        throw new InvalidOperationException(restored
            ? $"Не удалось запустить {target}; прежнее ядро восстановлено."
            : $"Не удалось запустить {target}, и прежнее ядро не восстановилось. Проверьте журнал и системный прокси.");
    }

    private bool IsConnectionRunning()
    {
        if (ProcessLifetime.IsRunning(_process)) return true;
        // Port-based recovery runs at startup. Polling netstat here blocks the UI thread
        // on every WebView snapshot (and on each mode click while disconnected).
        if (_state.ActiveProcessId is not { } processId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            var expectedName = _state.RunningCore switch {CoreKind.XrayCore=>"xray",CoreKind.IorCore=>"ior-core",_=>"sing-box"};
            return !process.HasExited && process.ProcessName.Contains(expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task RefreshDueSubscriptionsAsync()
    {
        if (_scheduledRefreshRunning || !_state.AutoRefreshSubscriptions) return;
        var due = _state.Subscriptions.Where(subscription => SubscriptionRefreshSchedule.IsDue(subscription, DateTime.UtcNow)).ToArray();
        if (due.Length == 0) return;
        _scheduledRefreshRunning = true;
        try
        {
            foreach (var subscription in due)
            {
                await RefreshSubscriptionAsync(subscription, showErrors: false);
                SubscriptionRefreshSchedule.ScheduleNext(subscription, DateTime.UtcNow);
            }
            SaveState(); PushWebSnapshot();
        }
        finally { _scheduledRefreshRunning = false; }
    }
}
