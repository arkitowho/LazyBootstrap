using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;
using LazyBootstrap.Services;

namespace LazyBootstrap.UI
{

    public partial class MainWindow
    {
        private const string MainMonitorOptionName = "mainmonitor";
        private const string SubMonitorOptionName = "sdvxsubmonitor";
        private const string MainRefreshOptionName = "graphics-force-refresh";
        private const string SubRefreshOptionName = "graphics-force-refresh-sub";
        private const string MainDisplayIdConfigKey = "maindisplayid";
        private const string SubDisplayIdConfigKey = "subdisplayid";
        private const string LegacyMainScreenConfigKey = "mainscreen";
        private const string LegacySubScreenConfigKey = "subscreen";
        private const string UnchangedRefreshRateOption = "不修改";
        private const string UnchangedRefreshRateConfigValue = "unchanged";

        private static bool IsUnchangedRefreshRate(string value) => value == UnchangedRefreshRateOption;

        private static bool IsSpiceRefreshRate(string value, string configuredValue) =>
            !string.IsNullOrWhiteSpace(configuredValue) && value == configuredValue;

        private void ReadSpiceRefreshRateSelections(DisplayConfigurationState state)
        {
            if (state.CompatibilityMode) return;
            if (!_spiceXmlConfigEditor.TryLoadOptionsContext(GetActiveSpiceXmlPathForMonitorSync(),
                    LoadOptions.PreserveWhitespace, false, out var context, out var error, out _))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogWarning("Failed to read Spice refresh overrides: {Error}", error);
                return;
            }

            state.MainSpiceRefreshRate = context.GetOptionValue(MainRefreshOptionName).Trim();
            state.SubSpiceRefreshRate = context.GetOptionValue(SubRefreshOptionName).Trim();
            state.SelectedMainRefreshRate = state.MainSpiceRefreshRate.Length == 0 ? UnchangedRefreshRateOption : state.MainSpiceRefreshRate;
            state.SelectedSubRefreshRate = state.SubSpiceRefreshRate.Length == 0 ? UnchangedRefreshRateOption : state.SubSpiceRefreshRate;
            if (state.MainSpiceRefreshRate.Length > 0)
            {
                state.SelectedMainRefreshRate = state.MainSpiceRefreshRate;
                if (!state.MainRefreshRates.Contains(state.MainSpiceRefreshRate)) state.MainRefreshRates.Add(state.MainSpiceRefreshRate);
                if (!TryNormalizeSpiceRefreshRate(state.MainSpiceRefreshRate, out _)) state.MainCustomRefresh = false;
            }
            if (state.SubSpiceRefreshRate.Length > 0)
            {
                state.SelectedSubRefreshRate = state.SubSpiceRefreshRate;
                if (!state.SubRefreshRates.Contains(state.SubSpiceRefreshRate)) state.SubRefreshRates.Add(state.SubSpiceRefreshRate);
                if (!TryNormalizeSpiceRefreshRate(state.SubSpiceRefreshRate, out _)) state.SubCustomRefresh = false;
            }
            NormalizeUnchangedRefreshSelections(state);
        }

        private static string ReadRefreshRateSelection(string value) =>
            value == UnchangedRefreshRateConfigValue ? UnchangedRefreshRateOption : value;

        private static string WriteRefreshRateSelection(string value) =>
            IsUnchangedRefreshRate(value) ? UnchangedRefreshRateConfigValue : value ?? string.Empty;

        private static void NormalizeUnchangedRefreshSelections(DisplayConfigurationState state)
        {
            if (IsUnchangedRefreshRate(state.SelectedMainRefreshRate)) state.MainCustomRefresh = false;
            if (IsUnchangedRefreshRate(state.SelectedSubRefreshRate)) state.SubCustomRefresh = false;
        }

        private static void ResetDisabledDisplayMode(DisplayConfigurationState state)
        {
            if (state.IsDisplayConfigurationEnabled) return;
            state.IsDualDisplay = false;
        }

        private WindowsDisplayConfigurationService _displayConfigurationService = null!;
        private DisplaySettingsTransactionCoordinator _displaySettingsTransactionCoordinator = null!;
        private readonly CancellationTokenSource _displayWindowLifetime = new();
        private CancellationTokenSource _displayTransactionCancellation;
        private TaskCompletionSource _displayTransactionCompletion;

        private void InitializeDisplayServices(
            WindowsDisplayConfigurationService displayConfigurationService,
            DisplaySettingsTransactionCoordinator displaySettingsTransactionCoordinator)
        {
            _displayConfigurationService = displayConfigurationService ?? throw new ArgumentNullException(nameof(displayConfigurationService));
            _displaySettingsTransactionCoordinator = displaySettingsTransactionCoordinator ?? throw new ArgumentNullException(nameof(displaySettingsTransactionCoordinator));
        }

        private async Task WarmDisplayStateAsync(DisplayConfigurationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            _logger.LogInformation("Display configuration warm-up started.");

            state.IsDisplayConfigurationEnabled = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "displayconfigure", false);
            state.CompatibilityMode = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "compatibilitymode", false);
            state.MainCustomRefresh = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "maincustomrefresh", false);
            state.SubCustomRefresh = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "subcustomrefresh", false);
            ResetDisabledDisplayMode(state);
            _displayRefreshCoordinator.SetConfigurationEnabled(state.IsDisplayConfigurationEnabled);
            if (state.IsDisplayConfigurationEnabled)
            {
                var discoveryResult = await _displayRefreshCoordinator.RunAsync(_displayConfigurationService.GetDisplays);
                _displayCatalog.Update(discoveryResult);
                if (!discoveryResult.Succeeded)
                {
                    _logger.LogWarning("Display discovery failed during warm-up: {Error}", discoveryResult.ErrorMessage);
                    ShowWarningToast("读取显示器列表失败", "请检查显示器连接后重新检测。详情请查看日志。");
                }
                else
                {
                    _logger.LogInformation("Display discovery completed. DisplayCount={DisplayCount}", discoveryResult.Displays.Count);
                }
            }

            {
                state.Displays.Clear();
                foreach (var display in _displayCatalog.Displays)
                {
                    state.Displays.Add(new DisplayChoiceOption(display, BuildDisplayLabel(display)));
                }

                EnsureRotationOptions(state);

                state.IsDualDisplay = state.IsDisplayConfigurationEnabled && string.Equals(_appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "mode", "single"), "dual", StringComparison.OrdinalIgnoreCase);
                state.ExitRestore = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "exitrestore", true);

                string mainDisplayId = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, MainDisplayIdConfigKey, string.Empty);
                string subDisplayId = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, SubDisplayIdConfigKey, string.Empty);
                string legacyMainIndex = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, LegacyMainScreenConfigKey, string.Empty);
                string legacySubIndex = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, LegacySubScreenConfigKey, string.Empty);
                state.LegacyMainIndex = legacyMainIndex;
                state.LegacySubIndex = legacySubIndex;
                int mainRotation = NormalizeRotationValue(ReadInt(AppConfigDefaults.DisplaySectionName, "mainrotation", 0));
                int subRotation = NormalizeRotationValue(ReadInt(AppConfigDefaults.DisplaySectionName, "subrotation", 0));

                state.SelectedMainDisplay = ResolveConfiguredDisplay(
                    state,
                    mainDisplayId,
                    legacyMainIndex,
                    0);
                state.SelectedSubDisplay = ResolveConfiguredDisplay(
                    state,
                    subDisplayId,
                    legacySubIndex,
                    Math.Min(1, Math.Max(0, state.Displays.Count - 1)));
                state.SelectedMainRotation = state.Rotations.FirstOrDefault(option => option.Angle == mainRotation) ?? state.Rotations.FirstOrDefault();
                state.SelectedSubRotation = state.Rotations.FirstOrDefault(option => option.Angle == subRotation) ?? state.Rotations.FirstOrDefault();
                state.SelectedMainResolution = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "mainresolution", string.Empty);
                state.SelectedSubResolution = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "subresolution", string.Empty);
                if (state.CompatibilityMode)
                {
                    state.SelectedMainRefreshRate = ReadRefreshRateSelection(_appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "mainrefresh", string.Empty));
                    state.SelectedSubRefreshRate = ReadRefreshRateSelection(_appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "subrefresh", string.Empty));
                    state.MainSpiceRefreshRate = string.Empty;
                    state.SubSpiceRefreshRate = string.Empty;
                }
                ReadSpiceRefreshRateSelections(state);
                NormalizeUnchangedRefreshSelections(state);
                state.SelectedTarget = DisplaySelectionTarget.None;
                state.ShowNoScreenSelected = true;
                state.ShowMainScreenConfig = false;
                state.ShowSubScreenConfig = false;
            }

            await HandleConfigurationChangedAsync(state, refreshMainOptions: true, refreshSubOptions: true, persist: false);
            UpdateDisplayDiscoveryStatus();
        }

        private Task PersistGeneralSettingsAsync(DisplayConfigurationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            NormalizeUnchangedRefreshSelections(state);
            _logger.LogInformation("Display general settings persistence started.");

            if (state.IsDisplayConfigurationEnabled)
            {
                if (_displayInitialization.IsPending) return Task.CompletedTask;
                if (AreDisplaySelectionsReady(state))
                {
                    PersistSelectionState(state);
                    return Task.CompletedTask;
                }
                // Explicit general-setting changes must still persist without replacing invalid mode selections.
            }

            _appConfig.WriteSection(AppConfigDefaults.DisplaySectionName, new Dictionary<string, string>
            {
                ["displayconfigure"] = state.IsDisplayConfigurationEnabled.ToString().ToLowerInvariant(),
                ["mode"] = state.IsDualDisplay ? "dual" : "single",
                ["exitrestore"] = state.ExitRestore.ToString().ToLowerInvariant(),
                ["compatibilitymode"] = state.CompatibilityMode.ToString().ToLowerInvariant(),
                ["maincustomrefresh"] = state.MainCustomRefresh.ToString().ToLowerInvariant(),
                ["subcustomrefresh"] = state.SubCustomRefresh.ToString().ToLowerInvariant()
            });
            ResetDisabledDisplayMode(state);
            if (!SyncSpiceMonitorOverrides(state)) ShowDisplayConfigurationError();
            _logger.LogInformation("Display general settings persisted. Enabled={Enabled}, DualDisplay={DualDisplay}, ExitRestore={ExitRestore}", state.IsDisplayConfigurationEnabled, state.IsDualDisplay, state.ExitRestore);
            return Task.CompletedTask;
        }

        private async Task<DisplayRefreshOutcome> HandleConfigurationChangedAsync(DisplayConfigurationState state, bool refreshMainOptions, bool refreshSubOptions, bool persist = true)
        {
            NormalizeUnchangedRefreshSelections(state);
            if (!state.IsDisplayConfigurationEnabled) return DisplayRefreshOutcome.Canceled;
            if (IsDisplayDetectionPaused) return DisplayRefreshOutcome.Deferred;
            if (!persist) ReadSpiceRefreshRateSelections(state);
            long revision = ++_displayRevision;
            var main = state.SelectedMainDisplay;
            var sub = state.SelectedSubDisplay;
            int mainRotation = state.SelectedMainRotation?.Angle ?? 0;
            int subRotation = state.SelectedSubRotation?.Angle ?? 0;
            string mainResolution = state.SelectedMainResolution;
            string subResolution = state.SelectedSubResolution;
            string mainRefresh = state.SelectedMainRefreshRate;
            string subRefresh = state.SelectedSubRefreshRate;
            bool mainCustomRefresh = state.MainCustomRefresh;
            bool subCustomRefresh = state.SubCustomRefresh;
            bool mainSpiceRefresh = IsSpiceRefreshRate(mainRefresh, state.MainSpiceRefreshRate);
            bool subSpiceRefresh = IsSpiceRefreshRate(subRefresh, state.SubSpiceRefreshRate);
            // Persist only after a complete mode query confirms the active selections.
            try
            {
                var results = await _displayRefreshCoordinator.RunAsync(() =>
                {
                    var mainOptions = refreshMainOptions
                        ? RefreshDisplayOptions(main, mainRotation, mainResolution, mainRefresh, !persist, mainCustomRefresh, mainSpiceRefresh) : default;
                    var subOptions = refreshSubOptions
                        ? RefreshDisplayOptions(sub, subRotation, subResolution, subRefresh, !persist, subCustomRefresh, subSpiceRefresh) : default;
                    var mainState = main?.Display?.IsAvailable == true
                        ? _displayConfigurationService.GetCurrentState(main.Display.DeviceName) : null;
                    var subState = sub?.Display?.IsAvailable == true
                        ? _displayConfigurationService.GetCurrentState(sub.Display.DeviceName) : null;
                    return (mainOptions, subOptions, mainState, subState);
                });
                if (revision != _displayRevision || _displayRefreshCoordinator.IsDisposed || _displayTransactionActive || IsDisplayDetectionPaused) return DisplayRefreshOutcome.Canceled;
                if (refreshMainOptions)
                {
                    ReplaceCollection(state.MainResolutions, results.mainOptions.Resolutions);
                    ReplaceCollection(state.MainRefreshRates, results.mainOptions.RefreshRates);
                    state.SelectedMainResolution = results.mainOptions.SelectedResolution;
                    state.SelectedMainRefreshRate = results.mainOptions.SelectedRefreshRate;
                    state.MainDiagnosticsTooltip = results.mainOptions.Tooltip;
                    state.MainModeQuerySucceeded = results.mainOptions.QuerySucceeded;
                }
                if (refreshSubOptions)
                {
                    ReplaceCollection(state.SubResolutions, results.subOptions.Resolutions);
                    ReplaceCollection(state.SubRefreshRates, results.subOptions.RefreshRates);
                    state.SelectedSubResolution = results.subOptions.SelectedResolution;
                    state.SelectedSubRefreshRate = results.subOptions.SelectedRefreshRate;
                    state.SubDiagnosticsTooltip = results.subOptions.Tooltip;
                    state.SubModeQuerySucceeded = results.subOptions.QuerySucceeded;
                }
                UpdateDisplayInfo(state, true, results.mainState);
                UpdateDisplayInfo(state, false, results.subState);
                bool refreshReplaced = mainRefresh != state.SelectedMainRefreshRate ||
                    (state.IsDualDisplay && subRefresh != state.SelectedSubRefreshRate);
                if ((persist || refreshReplaced) && !_displayInitialization.IsPending && _displayCatalog.StatusMessage.Length == 0)
                    PersistSelectionState(state);
                return AreDisplaySelectionsReady(state) ? DisplayRefreshOutcome.Completed : DisplayRefreshOutcome.Failed;
            }
            catch (OperationCanceledException) { return DisplayRefreshOutcome.Canceled; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display configuration refresh failed.");
                if (!_displayRefreshCoordinator.IsDisposed) ShowDisplayConfigurationError();
                return DisplayRefreshOutcome.Failed;
            }
        }

        private async Task PreviewDisplaySettingsAsync(DisplayConfigurationState state)
        {
            if (!state.IsDisplayConfigurationEnabled)
            {
                ShowWarningToast("显示器预览", "显示配置未启用，无法预览。");
                return;
            }
            var result = await ApplyDisplayTransactionAsync(BuildDisplayConfigurationRequest(state), preview: true);
            if (!result.Succeeded && !_displayRefreshCoordinator.IsDisposed)
            {
                _logger.LogWarning("Display preview did not complete. Cancelled={Cancelled}, Messages={Messages}", result.Cancelled, string.Join("; ", result.Messages));
                if (result.RestoreStates.Count > 0)
                    ShowDisplayRestoreWarning(result.Messages);
                else if (!result.Cancelled)
                    ShowWarningToast("显示器预览", result.UserMessage ?? "无法应用预览，请检查显示器连接、分辨率和刷新率后重试。详情请查看日志。");
            }
        }

        private async Task<DisplaySettingsTransactionResult> ApplyDisplayTransactionAsync(DisplayConfigurationRequest request, bool preview = false, CancellationToken cancellationToken = default)
        {
            DisplaySettingsTransactionResult Failure(string message) => new(false, null, new[] { message }) { UserMessage = message };
            if (!request.IsDisplayConfigurationEnabled)
                return new DisplaySettingsTransactionResult(true, new Dictionary<string, DisplayState>(), Array.Empty<string>());
            if (preview && IsDisplayDetectionPaused) return Failure("游戏启动或运行期间无法预览显示器配置。");
            if (_displayTransactionActive) return Failure("正在处理显示器设置，请稍后重试。");
            using var transactionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _displayWindowLifetime.Token);
            cancellationToken = transactionCancellation.Token;
            _displayTransactionCancellation = transactionCancellation;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _displayTransactionCompletion = completion;
            _displayTransactionActive = true;
            _displayRevision++;
            UpdateDisplayLayoutControlsEnabled();
            bool displaySettingsAttempted = false;
            try
            {
                using var lease = await _displayRefreshCoordinator.EnterTransactionAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var discovery = await _displayRefreshCoordinator.DiscoverForTransactionAsync(_displayConfigurationService.GetDisplays, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_displayRefreshCoordinator.IsDisposed) return Failure("窗口已关闭，已取消显示器配置。");
                if (discovery.Status == DisplayDiscoveryStatus.Failed)
                {
                    _logger.LogWarning("Display validation discovery failed: {Error}", discovery.ErrorMessage);
                    return Failure("无法确认显示器状态，请检查显示器连接后重新检测。详情请查看日志。");
                }
                if (discovery.Status == DisplayDiscoveryStatus.Partial)
                    _logger.LogWarning("Display validation uses partial discovery; checking requested targets. Error={Error}", discovery.ErrorMessage);
                DisplayChoiceOption ResolveTarget(DisplayChoiceOption selected) => selected == null ? null
                    : new DisplayChoiceOption(DisplayCatalog.ResolveFresh(discovery, selected.Display), selected.DisplayName);
                var main = ResolveTarget(request.SelectedMainDisplay);
                var sub = request.IsDualDisplay ? ResolveTarget(request.SelectedSubDisplay) : request.SelectedSubDisplay;
                if (main?.Display?.IsAvailable != true)
                    return Failure($"主显示器（{request.SelectedMainDisplay?.Display?.FriendlyName ?? "未选择"}）暂未连接，已停止应用设置。");
                if (request.IsDualDisplay && sub?.Display?.IsAvailable != true)
                    return Failure($"副显示器（{request.SelectedSubDisplay?.Display?.FriendlyName ?? "未选择"}）暂未连接，已停止应用设置。");
                request = request with { SelectedMainDisplay = main, SelectedSubDisplay = sub };
                string refreshError = await Task.Run(() => ValidateDisplayRefreshRates(request), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(refreshError)) return Failure(refreshError);
                if (!preview && !SyncSpiceMonitorOverrides(request))
                    return Failure("更新 Spice2x 显示器配置失败，已停止启动。");
                displaySettingsAttempted = true;
                var result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool succeeded = TryApplyDisplayForLaunch(request, out var restoreStates, out var messages, cancellationToken);
                    return new DisplaySettingsTransactionResult(succeeded, restoreStates, messages);
                });
                // No cancellation throw here: launch must receive any outstanding restore snapshots.
                if (!preview) return result;
                if (!result.Succeeded)
                {
                    PreservePendingRestoreStates(result.RestoreStates);
                    return result;
                }
                bool keep = false;
                try
                {
                    if (!_displayRefreshCoordinator.IsDisposed && !cancellationToken.IsCancellationRequested)
                        keep = await ShowDialogAsync("显示器预览",
                            "已应用当前预览设置。\n\n点击“保持现状”将保留当前结果，点击“还原”将恢复预览前状态。",
                            "保持现状", "还原", NotificationType.Information, "Basic", "Danger").WaitAsync(cancellationToken);
                }
                finally
                {
                    if (!keep)
                    {
                        var restored = await Task.Run(() => _displaySettingsTransactionCoordinator.Restore(result.RestoreStates));
                        PreservePendingRestoreStates(restored.RestoreStates);
                        if (restored.Messages.Count > 0 && !_displayRefreshCoordinator.IsDisposed)
                            ShowDisplayRestoreWarning(restored.Messages);
                    }
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Display transaction cancelled.");
                return new DisplaySettingsTransactionResult(false, null, Array.Empty<string>()) { Cancelled = true };
            }
            finally
            {
                FinishDisplayTransaction();
                _displayTransactionCancellation = null;
                completion.TrySetResult();
                if (preview && displaySettingsAttempted) await RequestDisplayListRefreshAsync("PreviewCompleted", waitForCurrent: true);
            }
        }

        private Task OpenTouchPanelAsync()
        {
            try
            {
                _logger.LogInformation("Opening touch panel settings.");
                ProcessExecutionHelper.OpenControlPanel("/name Microsoft.TabletPCSettings");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open touch panel settings.");
                ShowErrorToast("打开面板失败", "无法打开系统触控设置，请在 Windows 设置中检查触控设备。详情请查看日志。");
            }

            return Task.CompletedTask;
        }

        private bool TryApplyDisplayForLaunch(
            DisplayConfigurationRequest request,
            out IReadOnlyDictionary<string, DisplayState> restoreStates,
            out IReadOnlyList<string> messages,
            CancellationToken cancellationToken = default)
        {
            var mutableRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
            var mutableMessages = new List<string>();
            restoreStates = mutableRestoreStates;
            messages = mutableMessages;

            if (!request.IsDisplayConfigurationEnabled)
            {
                _logger.LogDebug("Display configuration apply skipped because it is disabled.");
                return true;
            }

            _logger.LogInformation("Applying display configuration for launch.");
            var requests = new List<DisplaySettingsRequest>();
            bool allValid = true;
            allValid &= TryBuildRequest(request.SelectedMainDisplay, request.SelectedMainRotation?.Angle ?? 0, request.SelectedMainResolution, request.SelectedMainRefreshRate, request.CompatibilityMode, "主显示器", requests, mutableMessages);

            if (request.IsDualDisplay)
            {
                allValid &= TryBuildRequest(request.SelectedSubDisplay, request.SelectedSubRotation?.Angle ?? 0, request.SelectedSubResolution, request.SelectedSubRefreshRate, request.CompatibilityMode, "副显示器", requests, mutableMessages);
            }

            if (!allValid)
            {
                _logger.LogWarning("Display configuration request validation failed. MessageCount={MessageCount}", mutableMessages.Count);
                return false;
            }

            var transactionResult = _displaySettingsTransactionCoordinator.Apply(requests, cancellationToken);
            mutableRestoreStates = new Dictionary<string, DisplayState>(transactionResult.RestoreStates, StringComparer.OrdinalIgnoreCase);
            mutableMessages.AddRange(transactionResult.Messages);
            restoreStates = mutableRestoreStates;
            messages = mutableMessages;
            _logger.LogInformation(
                "Display configuration transaction completed. Succeeded={Succeeded}, RequestCount={RequestCount}, RestoreStateCount={RestoreStateCount}, MessageCount={MessageCount}",
                transactionResult.Succeeded,
                requests.Count,
                mutableRestoreStates.Count,
                mutableMessages.Count);
            return transactionResult.Succeeded;
        }

        private int RestoreAppliedDisplaySettings(
            IReadOnlyDictionary<string, DisplayState> restoreStates,
            IList<string> messages,
            out IReadOnlyDictionary<string, DisplayState> pendingRestoreStates)
        {
            _displayRevision++;
            try
            {
                var result = _displayRefreshCoordinator.RunSynchronous(() => _displaySettingsTransactionCoordinator.Restore(restoreStates));
                pendingRestoreStates = result.RestoreStates;
                foreach (var message in result.Messages) messages.Add(message);
                return restoreStates.Count - pendingRestoreStates.Count;
            }
            finally
            {
                // Defer until the native transaction has released its gate and closing cleanup has finished.
                Dispatcher.UIThread.Post(() => _ = RequestDisplayListRefreshAsync("RestoreCompleted", waitForCurrent: true));
            }
        }

        private void PreservePendingRestoreStates(IReadOnlyDictionary<string, DisplayState> pending)
        {
            var merged = new Dictionary<string, DisplayState>(_displayRestoreStates, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pending) merged[pair.Key] = pair.Value;
            _displayRestoreStates = merged;
        }

        private DisplayModeOptions RefreshDisplayOptions(
            DisplayChoiceOption selectedDisplay,
            int rotation,
            string selectedResolution,
            string selectedRefreshRate, bool preserveSelection = false, bool customRefresh = false, bool spiceRefresh = false)
        {
            if (selectedDisplay?.Display?.IsAvailable != true)
            {
                return new DisplayModeOptions(
                    string.IsNullOrWhiteSpace(selectedResolution) ? Array.Empty<string>() : new[] { selectedResolution },
                    spiceRefresh ? new[] { UnchangedRefreshRateOption, selectedRefreshRate } : new[] { UnchangedRefreshRateOption },
                    selectedResolution, selectedRefreshRate, "显示器暂未连接，已保留原配置。");
            }

            var supportedModesResult = _displayConfigurationService.GetSupportedModes(selectedDisplay.Display.DeviceName);
            if (!supportedModesResult.Succeeded)
                _logger.LogWarning("Display mode query incomplete. Device={Device}, Error={Error}", selectedDisplay.Display.DeviceName, supportedModesResult.ErrorMessage);
            string tooltip = BuildTooltip(supportedModesResult);

            var resolutionItems = BuildResolutionItems(supportedModesResult.Modes, rotation, out string highestResolution);

            if (!string.IsNullOrWhiteSpace(selectedResolution) && (preserveSelection || !supportedModesResult.Succeeded))
            {
                if (!resolutionItems.Contains(selectedResolution, StringComparer.OrdinalIgnoreCase)) resolutionItems = resolutionItems.Append(selectedResolution).ToArray();
            }
            else if (!resolutionItems.Contains(selectedResolution ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                selectedResolution = highestResolution;
            }

            var refreshItems = supportedModesResult.Modes
                .Where(mode => string.Equals(NormalizeResolutionByRotation(mode.Width, mode.Height, rotation), selectedResolution, StringComparison.OrdinalIgnoreCase))
                .Where(mode => mode.RefreshRate >= WindowsDisplayConfigurationService.MinimumSelectableRefreshRate)
                .Select(mode => mode.RefreshRate)
                .Distinct()
                .OrderBy(value => value)
                .Select(value => value.ToString(CultureInfo.InvariantCulture))
                .ToList();

            if (spiceRefresh && !refreshItems.Contains(selectedRefreshRate)) refreshItems.Add(selectedRefreshRate);

            if (!IsUnchangedRefreshRate(selectedRefreshRate) && !customRefresh && supportedModesResult.Succeeded && !refreshItems.Contains(selectedRefreshRate ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                selectedRefreshRate = refreshItems.FirstOrDefault() ?? string.Empty;
            }
            if (!customRefresh && supportedModesResult.Succeeded && refreshItems.Count == 0)
                tooltip = "所选分辨率没有可用的 60 Hz 及以上刷新率，请选择“不修改”、其他分辨率或重新检测。";

            refreshItems.Insert(0, UnchangedRefreshRateOption);

            return new DisplayModeOptions(resolutionItems, refreshItems, selectedResolution, selectedRefreshRate, tooltip, supportedModesResult.Succeeded);
        }

        private string ValidateDisplayRefreshRates(DisplayConfigurationRequest request)
        {
            string Validate(DisplayChoiceOption display, int rotation, string resolution, string refreshRate, bool customRefresh, bool spiceRefresh, string label)
            {
                bool unchanged = IsUnchangedRefreshRate(refreshRate);
                int rate = 0;
                if (!unchanged && (!int.TryParse(refreshRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate) ||
                    rate < (spiceRefresh && !request.CompatibilityMode ? 1 : WindowsDisplayConfigurationService.MinimumSelectableRefreshRate)))
                    return $"{label}刷新率无效，请输入或选择 60 Hz 及以上的整数刷新率。";
                if (display?.Display?.IsAvailable != true)
                    return $"{label}未选择可用的显示器，请重新检测后重试。";
                var result = _displayConfigurationService.GetSupportedModes(display.Display.DeviceName);
                if (!result.Succeeded)
                    return $"无法完整读取{label}的显示模式，请重新检测后重试。";
                return result.Modes.Any(mode => (unchanged || customRefresh || spiceRefresh || mode.RefreshRate == rate) &&
                    NormalizeResolutionByRotation(mode.Width, mode.Height, rotation) == resolution)
                    ? string.Empty : $"{label}不支持所选分辨率和刷新率，请重新检测后选择有效值。";
            }
            string error = Validate(request.SelectedMainDisplay, request.SelectedMainRotation?.Angle ?? 0,
                request.SelectedMainResolution, request.SelectedMainRefreshRate, request.MainCustomRefresh, request.MainSpiceRefresh, "主显示器");
            if (error.Length > 0 || !request.IsDualDisplay) return error;
            return Validate(request.SelectedSubDisplay, request.SelectedSubRotation?.Angle ?? 0,
                request.SelectedSubResolution, request.SelectedSubRefreshRate, request.SubCustomRefresh, request.SubSpiceRefresh, "副显示器");
        }

        private static bool AreDisplaySelectionsReady(DisplayConfigurationState state)
        {
            bool RefreshReady(bool custom, List<string> rates, string value) => IsUnchangedRefreshRate(value) || (custom
                ? TryNormalizeSpiceRefreshRate(value, out string normalized) && normalized.Length > 0
                : rates.Contains(value));
            bool mainReady = state.MainModeQuerySucceeded && state.SelectedMainDisplay?.Display?.IsAvailable == true &&
                RefreshReady(state.MainCustomRefresh, state.MainRefreshRates, state.SelectedMainRefreshRate) && state.MainResolutions.Contains(state.SelectedMainResolution);
            bool subReady = !state.IsDualDisplay || (state.SubModeQuerySucceeded && state.SelectedSubDisplay?.Display?.IsAvailable == true &&
                RefreshReady(state.SubCustomRefresh, state.SubRefreshRates, state.SelectedSubRefreshRate) && state.SubResolutions.Contains(state.SelectedSubResolution));
            return mainReady && subReady;
        }

        private static IReadOnlyList<string> BuildResolutionItems(IReadOnlyList<DisplayMode> modes, int rotation, out string highestResolution)
        {
            highestResolution = string.Empty;
            if (modes == null || modes.Count == 0)
            {
                return Array.Empty<string>();
            }

            var highestMode = modes
                .OrderByDescending(mode => (long)mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width)
                .ThenByDescending(mode => mode.Height)
                .First();
            highestResolution = NormalizeResolutionByRotation(highestMode.Width, highestMode.Height, rotation);

            var supportedResolutions = new HashSet<string>(
                modes.Select(mode => NormalizeResolutionByRotation(mode.Width, mode.Height, rotation)),
                StringComparer.OrdinalIgnoreCase);
            var resolutionItems = new List<string>();

            AddSupportedResolution(resolutionItems, supportedResolutions, NormalizeResolutionByRotation(1280, 720, rotation));
            AddSupportedResolution(resolutionItems, supportedResolutions, NormalizeResolutionByRotation(1920, 1080, rotation));
            AddSupportedResolution(resolutionItems, supportedResolutions, highestResolution);

            return resolutionItems;
        }

        private static void AddSupportedResolution(ICollection<string> target, ISet<string> supportedResolutions, string resolution)
        {
            if (string.IsNullOrWhiteSpace(resolution) || !supportedResolutions.Contains(resolution))
            {
                return;
            }

            if (target.Contains(resolution, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            target.Add(resolution);
        }

        private void UpdateDisplayInfo(DisplayConfigurationState state, bool isMainTarget, DisplayStateQueryResult stateResult)
        {
            var selectedDisplay = isMainTarget ? state.SelectedMainDisplay : state.SelectedSubDisplay;

            if (selectedDisplay?.Display == null)
            {
                if (isMainTarget)
                {
                    state.MainOutputInfo = "未知";
                    state.MainStartupInfo = "未设置";
                }
                else
                {
                    state.SubOutputInfo = "未知";
                    state.SubStartupInfo = "未设置";
                }
                return;
            }

            stateResult ??= new DisplayStateQueryResult(null, "显示器暂未连接，已保留原配置。");
            if (!stateResult.Succeeded)
                _logger.LogWarning("Current display state query failed. Device={Device}, Error={Error}", selectedDisplay.Display.DeviceName, stateResult.ErrorMessage);
            var outputInfo = stateResult.Succeeded
                ? $"设备: {selectedDisplay.Display.FriendlyName} ({selectedDisplay.Display.DeviceName})\n当前: {stateResult.State.Width}x{stateResult.State.Height} @ {stateResult.State.RefreshRate}Hz, {FormatRotationDisplay(_displayConfigurationService.OrientationToAngle(stateResult.State.Orientation))}"
                : $"设备: {selectedDisplay.Display.FriendlyName} ({selectedDisplay.Display.DeviceName})\n当前: 无法读取，请检查显示器连接后重新检测。详情请查看日志。";
            var startupInfo = BuildDisplayStartupInfo(state, isMainTarget);

            if (isMainTarget)
            {
                state.MainOutputInfo = outputInfo;
                state.MainStartupInfo = startupInfo;
            }
            else
            {
                state.SubOutputInfo = outputInfo;
                state.SubStartupInfo = startupInfo;
            }
        }

        private static string BuildDisplayStartupInfo(DisplayConfigurationState state, bool isMainTarget)
        {
            var selectedDisplay = isMainTarget ? state.SelectedMainDisplay : state.SelectedSubDisplay;
            if (selectedDisplay?.Display == null) return "未设置";
            var rotation = isMainTarget ? state.SelectedMainRotation?.Angle ?? 0 : state.SelectedSubRotation?.Angle ?? 0;
            var resolution = isMainTarget ? state.SelectedMainResolution : state.SelectedSubResolution;
            var refreshRate = isMainTarget ? state.SelectedMainRefreshRate : state.SelectedSubRefreshRate;
            string refreshSource = state.CompatibilityMode ? "（系统设置）" : string.Empty;
            return $"旋转: {FormatRotationDisplay(rotation)}\n分辨率: {FormatTextOrFallback(resolution)}\n刷新率: {FormatRefreshRateDisplay(refreshRate)}{refreshSource}";
        }

        private static void UpdateDisplayStartupInfo(DisplayConfigurationState state)
        {
            state.MainStartupInfo = BuildDisplayStartupInfo(state, true);
            state.SubStartupInfo = BuildDisplayStartupInfo(state, false);
        }

        private void PersistSelectionState(DisplayConfigurationState state)
        {
            NormalizeUnchangedRefreshSelections(state);
            if (state.IsDisplayConfigurationEnabled && !AreDisplaySelectionsReady(state)) return;
            _logger.LogDebug("Persisting display selection state.");
            var values = new Dictionary<string, string>
            {
                ["displayconfigure"] = state.IsDisplayConfigurationEnabled.ToString().ToLowerInvariant(),
                ["mode"] = state.IsDualDisplay ? "dual" : "single",
                ["exitrestore"] = state.ExitRestore.ToString().ToLowerInvariant(),
                ["compatibilitymode"] = state.CompatibilityMode.ToString().ToLowerInvariant(),
                ["maincustomrefresh"] = state.MainCustomRefresh.ToString().ToLowerInvariant(),
                ["subcustomrefresh"] = state.SubCustomRefresh.ToString().ToLowerInvariant(),
                [MainDisplayIdConfigKey] = state.SelectedMainDisplay?.Display?.PersistentId ?? string.Empty,
                [SubDisplayIdConfigKey] = state.SelectedSubDisplay?.Display?.PersistentId ?? string.Empty,
                ["mainrotation"] = (state.SelectedMainRotation?.Angle ?? 0).ToString(),
                ["subrotation"] = (state.SelectedSubRotation?.Angle ?? 0).ToString(),
                ["mainresolution"] = state.SelectedMainResolution ?? string.Empty,
                ["subresolution"] = state.SelectedSubResolution ?? string.Empty
            };
            if (state.CompatibilityMode)
            {
                values["mainrefresh"] = WriteRefreshRateSelection(state.SelectedMainRefreshRate);
                values["subrefresh"] = WriteRefreshRateSelection(state.SelectedSubRefreshRate);
            }
            _appConfig.WriteSection(AppConfigDefaults.DisplaySectionName, values, new[] {
                state.SelectedMainDisplay != null ? LegacyMainScreenConfigKey : null,
                state.SelectedSubDisplay != null ? LegacySubScreenConfigKey : null
            }.Where(key => key != null).ToArray());
            if (!SyncSpiceMonitorOverrides(state)) ShowDisplayConfigurationError();
            _logger.LogDebug("Display selection state persisted.");
        }

        private string GetActiveSpiceXmlPathForMonitorSync()
        {
            bool useSystem = bool.TryParse(
                _appConfig.ReadString(AppConfigDefaults.SettingSectionName, "use-system-config", "false"),
                out var parsed)
                && parsed;
            return _paths.ResolveSpiceXmlPath(useSystem);
        }

        private bool SyncSpiceMonitorOverrides(DisplayConfigurationState state)
        {
            // A missing selection must not clear the user's existing Spice output binding.
            if (state.IsDisplayConfigurationEnabled &&
                (state.SelectedMainDisplay?.Display?.IsAvailable != true ||
                 (state.IsDualDisplay && state.SelectedSubDisplay?.Display?.IsAvailable != true))) return false;
            return SyncSpiceMonitorOverrides(BuildDisplayConfigurationRequest(state));
        }

        private bool SyncSpiceMonitorOverrides(DisplayConfigurationRequest state)
        {
            string mainMonitorValue = string.Empty;
            string subMonitorValue = string.Empty;
            string mainRefreshValue = string.Empty;
            string subRefreshValue = string.Empty;
            bool updateMainRefresh = state.CompatibilityMode || state.IsDisplayConfigurationEnabled;
            bool updateSubRefresh = state.CompatibilityMode || (state.IsDisplayConfigurationEnabled && state.IsDualDisplay);

            if (state.IsDisplayConfigurationEnabled)
            {
                mainMonitorValue = state.SelectedMainDisplay?.Display?.DeviceName ?? string.Empty;
                subMonitorValue = state.IsDualDisplay
                    ? state.SelectedSubDisplay?.Display?.DeviceName ?? string.Empty
                    : string.Empty;
                if (!state.CompatibilityMode)
                {
                    if (!TryNormalizeSpiceRefreshRate(state.SelectedMainRefreshRate, out mainRefreshValue, state.MainSpiceRefresh) ||
                        (state.IsDualDisplay && !TryNormalizeSpiceRefreshRate(state.SelectedSubRefreshRate, out subRefreshValue, state.SubSpiceRefresh)))
                    {
                        _logger.LogWarning("Spice display override sync failed because a selected refresh rate is invalid.");
                        return false;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mainMonitorValue))
            {
                mainMonitorValue = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(subMonitorValue))
            {
                subMonitorValue = string.Empty;
            }

            try
            {
                string spiceXmlPath = GetActiveSpiceXmlPathForMonitorSync();
                if (string.IsNullOrWhiteSpace(spiceXmlPath) || !File.Exists(spiceXmlPath))
                {
                    _logger.LogDebug("Spice display override sync skipped because active spice XML is missing.");
                    return !state.IsDisplayConfigurationEnabled;
                }

                // Skip writes only when both output bindings and refresh overrides are current.
                if (_spiceXmlConfigEditor.TryLoadOptionsContext(
                        spiceXmlPath,
                        LoadOptions.PreserveWhitespace,
                        false,
                        out var context,
                        out _,
                        out _))
                {
                    string currentMainMonitor = context.GetOptionValue(MainMonitorOptionName) ?? string.Empty;
                    string currentSubMonitor = context.GetOptionValue(SubMonitorOptionName) ?? string.Empty;
                    if (string.Equals(currentMainMonitor, mainMonitorValue, StringComparison.Ordinal)
                        && string.Equals(currentSubMonitor, subMonitorValue, StringComparison.Ordinal)
                        && (!updateMainRefresh || string.Equals(context.GetOptionValue(MainRefreshOptionName), mainRefreshValue, StringComparison.Ordinal))
                        && (!updateSubRefresh || string.Equals(context.GetOptionValue(SubRefreshOptionName), subRefreshValue, StringComparison.Ordinal)))
                    {
                        _logger.LogDebug("Spice display override sync skipped because values are already current.");
                        return true;
                    }
                }

                var updates = new List<SpiceOptionUpdate>
                {
                    new(MainMonitorOptionName, mainMonitorValue, false),
                    new(SubMonitorOptionName, subMonitorValue, false)
                };
                if (updateMainRefresh) updates.Add(new(MainRefreshOptionName, mainRefreshValue, false));
                if (updateSubRefresh) updates.Add(new(SubRefreshOptionName, subRefreshValue, false));
                if (!_spiceXmlConfigEditor.ApplySpiceOptions(
                        spiceXmlPath, updates,
                        out var error))
                {
                    _logger.LogWarning("Failed to sync spice display overrides: {Error}", error);
                    return false;
                }
                else
                {
                    _logger.LogInformation("Spice display overrides synced.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync spice display overrides.");
                return false;
            }
            return true;
        }

        private static bool TryNormalizeSpiceRefreshRate(string selectedRate, out string value, bool spiceRefresh = false)
        {
            value = string.Empty;
            // Selections can be empty while the display's supported modes are being refreshed.
            if (string.IsNullOrWhiteSpace(selectedRate) || IsUnchangedRefreshRate(selectedRate)) return true;
            if (!int.TryParse(selectedRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int refreshRate) ||
                refreshRate < (spiceRefresh ? 1 : WindowsDisplayConfigurationService.MinimumSelectableRefreshRate))
                return false;
            value = refreshRate.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static void EnsureRotationOptions(DisplayConfigurationState state)
        {
            if (state.Rotations.Count > 0)
            {
                return;
            }

            state.Rotations.Add(new RotationOption(0));
            state.Rotations.Add(new RotationOption(90));
            state.Rotations.Add(new RotationOption(180));
            state.Rotations.Add(new RotationOption(270));
        }

        private static DisplayChoiceOption ResolveConfiguredDisplay(
            DisplayConfigurationState state,
            string persistentId,
            string legacyIndexText,
            int defaultIndex)
        {
            var display = DisplayCatalog.Resolve(state.Displays.Where(option => option.Display.IsAvailable)
                .Select(option => option.Display).ToArray(), persistentId, legacyIndexText, defaultIndex);
            if (display == null) return null;
            var option = state.Displays.FirstOrDefault(item => ReferenceEquals(item.Display, display));
            if (option != null) return option;
            option = new DisplayChoiceOption(display, BuildDisplayLabel(display));
            state.Displays.Add(option);
            return option;
        }

        private static void ReplaceCollection(List<string> target, IReadOnlyList<string> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private static bool TryBuildRequest(
            DisplayChoiceOption selectedDisplay,
            int rotation,
            string resolution,
            string refreshRate,
            bool compatibilityMode,
            string targetName,
            List<DisplaySettingsRequest> requests,
            List<string> messages)
        {
            if (selectedDisplay?.Display == null)
            {
                messages.Add($"{targetName}未选择有效的显示器。");
                return false;
            }

            if (!TryParseResolution(resolution, out var width, out var height))
            {
                messages.Add($"{targetName}分辨率无效: {resolution}");
                return false;
            }

            bool unchanged = IsUnchangedRefreshRate(refreshRate);
            int parsedRefresh = 0;
            if (!unchanged && (!int.TryParse(refreshRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedRefresh) ||
                parsedRefresh < (compatibilityMode ? WindowsDisplayConfigurationService.MinimumSelectableRefreshRate : 1)))
            {
                messages.Add($"{targetName}刷新率无效，请选择 60 Hz 及以上刷新率: {refreshRate}");
                return false;
            }
            int? refreshValue = compatibilityMode && !unchanged ? parsedRefresh : null;

            requests.Add(new DisplaySettingsRequest(targetName, selectedDisplay.Display.DeviceName, rotation, width, height, refreshValue));
            return true;
        }

        private static bool TryParseResolution(string resolution, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(resolution))
            {
                return false;
            }

            var parts = resolution.Split('x', 'X');
            return parts.Length == 2
                && int.TryParse(parts[0], out width)
                && int.TryParse(parts[1], out height);
        }

        private static string NormalizeResolutionByRotation(int width, int height, int rotation)
        {
            bool vertical = rotation == 90 || rotation == 270;
            int normalizedWidth = width;
            int normalizedHeight = height;

            if (vertical && normalizedWidth > normalizedHeight)
            {
                (normalizedWidth, normalizedHeight) = (normalizedHeight, normalizedWidth);
            }

            if (!vertical && normalizedWidth < normalizedHeight)
            {
                (normalizedWidth, normalizedHeight) = (normalizedHeight, normalizedWidth);
            }

            return $"{normalizedWidth}x{normalizedHeight}";
        }

        private static string BuildDisplayLabel(DisplayInfo display)
        {
            if (display == null)
            {
                return "未知显示器";
            }

            if (!display.IsAvailable) return $"{display.FriendlyName}（暂未连接）";
            var deviceName = display.DeviceName ?? string.Empty;
            var displayId = deviceName.StartsWith(@"\.\", StringComparison.OrdinalIgnoreCase)
                ? deviceName[4..]
                : deviceName;

            if (string.IsNullOrWhiteSpace(displayId))
            {
                return string.IsNullOrWhiteSpace(display.FriendlyName) ? "未知显示器" : display.FriendlyName;
            }

            if (string.IsNullOrWhiteSpace(display.FriendlyName))
            {
                return display.IsPrimary ? $"{displayId} - 主屏" : displayId;
            }

            var label = $"{displayId} - {display.FriendlyName}";
            return display.IsPrimary ? $"{label} - 主屏" : label;
        }

        private static string BuildTooltip(DisplayModeQueryResult result)
        {
            if (result == null || result.Succeeded || string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                return string.Empty;
            }

            return result.Modes.Count > 0
                ? "显示模式读取不完整，已显示可读取的模式。请重新检测，详情请查看日志。"
                : "无法读取显示模式，请检查显示器连接后重新检测。详情请查看日志。";
        }

        private void ShowDisplayRestoreWarning(IReadOnlyList<string> messages)
        {
            _logger.LogWarning("Display restoration incomplete: {Messages}", string.Join("; ", messages));
            if (_launchWorkflowLifetime.IsStopping) return;
            ShowWarningToast("显示器还原未完成", "部分显示器设置尚未还原，请点击停止重试。详情请查看日志。");
        }

        private static string FormatRotationDisplay(int angle)
        {
            return RotationOption.GetDisplayName(angle);
        }

        private static string FormatTextOrFallback(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "未设置" : value;
        }

        private static string FormatRefreshRateDisplay(string refreshRate)
        {
            if (IsUnchangedRefreshRate(refreshRate)) return UnchangedRefreshRateOption;
            return string.IsNullOrWhiteSpace(refreshRate) ? "未设置" : $"{refreshRate}Hz";
        }

        private readonly record struct DisplayModeOptions(
            IReadOnlyList<string> Resolutions,
            IReadOnlyList<string> RefreshRates,
            string SelectedResolution,
            string SelectedRefreshRate,
            string Tooltip,
            bool QuerySucceeded = false);


        private int ReadInt(string section, string key, int defaultValue)
        {
            return int.TryParse(_appConfig.ReadString(section, key, defaultValue.ToString()), out var value)
                ? value
                : defaultValue;
        }

        private static int NormalizeRotationValue(int value)
        {
            return value switch
            {
                0 => 0,
                1 => 90,
                2 => 180,
                3 => 270,
                90 => 90,
                180 => 180,
                270 => 270,
                _ => 0
            };
        }
    }
}
