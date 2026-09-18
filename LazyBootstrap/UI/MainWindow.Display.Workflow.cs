using System;
using SystemEnvironment = System.Environment;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls.Notifications;
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
        private const string MainDisplayIdConfigKey = "maindisplayid";
        private const string SubDisplayIdConfigKey = "subdisplayid";
        private const string LegacyMainScreenConfigKey = "mainscreen";
        private const string LegacySubScreenConfigKey = "subscreen";

        private WindowsDisplayConfigurationService _displayConfigurationService = null!;
        private DisplaySettingsTransactionCoordinator _displaySettingsTransactionCoordinator = null!;

        private void InitializeDisplayServices(
            WindowsDisplayConfigurationService displayConfigurationService,
            DisplaySettingsTransactionCoordinator displaySettingsTransactionCoordinator)
        {
            _displayConfigurationService = displayConfigurationService ?? throw new ArgumentNullException(nameof(displayConfigurationService));
            _displaySettingsTransactionCoordinator = displaySettingsTransactionCoordinator ?? throw new ArgumentNullException(nameof(displaySettingsTransactionCoordinator));
        }

        private Task WarmDisplayStateAsync(DisplayConfigurationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            _logger.LogInformation("Display configuration warm-up started.");

            var discoveryResult = _displayConfigurationService.GetDisplays();
            if (!discoveryResult.Succeeded)
            {
                _logger.LogWarning("Display discovery failed during warm-up: {Error}", discoveryResult.ErrorMessage);
                ShowWarningToast("读取显示器列表失败", discoveryResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("Display discovery completed. DisplayCount={DisplayCount}", discoveryResult.Displays.Count);
            }

            {
                state.Displays.Clear();
                foreach (var display in discoveryResult.Displays)
                {
                    state.Displays.Add(new DisplayChoiceOption(display, BuildDisplayLabel(display)));
                }

                EnsureRotationOptions(state);

                state.IsDisplayConfigurationEnabled = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "displayconfigure", false);
                state.IsDualDisplay = !string.Equals(_appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "mode", "single"), "single", StringComparison.OrdinalIgnoreCase);
                state.ExitRestore = _appConfig.ReadBool(AppConfigDefaults.DisplaySectionName, "exitrestore", true);

                string mainDisplayId = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, MainDisplayIdConfigKey, string.Empty);
                string subDisplayId = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, SubDisplayIdConfigKey, string.Empty);
                string legacyMainIndex = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, LegacyMainScreenConfigKey, string.Empty);
                string legacySubIndex = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, LegacySubScreenConfigKey, string.Empty);
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
                state.SelectedMainRefreshRate = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "mainrefresh", string.Empty);
                state.SelectedSubRefreshRate = _appConfig.ReadString(AppConfigDefaults.DisplaySectionName, "subrefresh", string.Empty);
                state.SelectedTarget = DisplaySelectionTarget.None;
                state.ShowNoScreenSelected = true;
                state.ShowMainScreenConfig = false;
                state.ShowSubScreenConfig = false;
            }

            return HandleConfigurationChangedAsync(state, refreshMainOptions: true, refreshSubOptions: true, persist: false);
        }

        private Task PersistGeneralSettingsAsync(DisplayConfigurationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            _logger.LogInformation("Display general settings persistence started.");

            if (state.IsDisplayConfigurationEnabled)
            {
                PersistSelectionState(state);
                return Task.CompletedTask;
            }

            _appConfig.WriteString(AppConfigDefaults.DisplaySectionName, "displayconfigure", state.IsDisplayConfigurationEnabled.ToString().ToLowerInvariant());
            _appConfig.WriteString(AppConfigDefaults.DisplaySectionName, "mode", state.IsDualDisplay ? "dual" : "single");
            _appConfig.WriteString(AppConfigDefaults.DisplaySectionName, "exitrestore", state.ExitRestore.ToString().ToLowerInvariant());
            SyncSpiceMonitorOverrides(state);
            _logger.LogInformation("Display general settings persisted. Enabled={Enabled}, DualDisplay={DualDisplay}, ExitRestore={ExitRestore}", state.IsDisplayConfigurationEnabled, state.IsDualDisplay, state.ExitRestore);
            return Task.CompletedTask;
        }

        private Task HandleConfigurationChangedAsync(DisplayConfigurationState state, bool refreshMainOptions, bool refreshSubOptions, bool persist = true)
        {
            ArgumentNullException.ThrowIfNull(state);
            _logger.LogDebug("Display configuration change handling started. RefreshMainOptions={RefreshMainOptions}, RefreshSubOptions={RefreshSubOptions}", refreshMainOptions, refreshSubOptions);

            try
            {
                {
                    if (refreshMainOptions)
                    {
                        var mainOptions = RefreshDisplayOptions(
                            state.SelectedMainDisplay,
                            state.SelectedMainRotation?.Angle ?? 0,
                            state.SelectedMainResolution,
                            state.SelectedMainRefreshRate);
                        ReplaceCollection(state.MainResolutions, mainOptions.Resolutions);
                        ReplaceCollection(state.MainRefreshRates, mainOptions.RefreshRates);
                        state.SelectedMainResolution = mainOptions.SelectedResolution;
                        state.SelectedMainRefreshRate = mainOptions.SelectedRefreshRate;
                        state.MainDiagnosticsTooltip = mainOptions.Tooltip;
                    }

                    if (refreshSubOptions)
                    {
                        var subOptions = RefreshDisplayOptions(
                            state.SelectedSubDisplay,
                            state.SelectedSubRotation?.Angle ?? 0,
                            state.SelectedSubResolution,
                            state.SelectedSubRefreshRate);
                        ReplaceCollection(state.SubResolutions, subOptions.Resolutions);
                        ReplaceCollection(state.SubRefreshRates, subOptions.RefreshRates);
                        state.SelectedSubResolution = subOptions.SelectedResolution;
                        state.SelectedSubRefreshRate = subOptions.SelectedRefreshRate;
                        state.SubDiagnosticsTooltip = subOptions.Tooltip;
                    }

                    if (!state.IsDualDisplay && state.SelectedTarget == DisplaySelectionTarget.Sub)
                    {
                        state.SelectedTarget = DisplaySelectionTarget.None;
                        state.ShowNoScreenSelected = true;
                        state.ShowMainScreenConfig = false;
                        state.ShowSubScreenConfig = false;
                    }

                    UpdateDisplayInfo(state, true);
                    UpdateDisplayInfo(state, false);
                }

                if (persist) PersistSelectionState(state);
                _logger.LogInformation("Display configuration change handled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display configuration refresh failed.");
                ShowErrorToast("显示器配置失败", ex.Message);
            }

            return Task.CompletedTask;
        }

        private async Task PreviewDisplaySettingsAsync(DisplayConfigurationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            _logger.LogInformation("Display configuration preview requested.");

            if (!state.IsDisplayConfigurationEnabled)
            {
                _logger.LogWarning("Display configuration preview skipped because display configuration is disabled.");
                ShowWarningToast("显示器预览", "显示配置未启用，无法预览。");
                return;
            }

            var request = new DisplayConfigurationRequest(
                state.IsDisplayConfigurationEnabled,
                state.IsDualDisplay,
                state.ExitRestore,
                state.SelectedMainDisplay,
                state.SelectedSubDisplay,
                state.SelectedMainRotation,
                state.SelectedSubRotation,
                state.SelectedMainResolution,
                state.SelectedSubResolution,
                state.SelectedMainRefreshRate,
                state.SelectedSubRefreshRate);

            if (!TryApplyDisplayForLaunch(request, out var restoreStates, out var messages))
            {
                _logger.LogWarning("Display configuration preview failed. RestoreStateCount={RestoreStateCount}, MessageCount={MessageCount}", restoreStates.Count, messages.Count);
                if (messages.Count > 0)
                {
                    ShowWarningToast("显示器预览", BuildDiagnosticsMessage(messages));
                }

                await HandleConfigurationChangedAsync(state, refreshMainOptions: false, refreshSubOptions: false);
                return;
            }

            bool keepCurrentState = await ShowDialogAsync(
                "显示器预览",
                "已应用当前预览设置。\n\n点击“保持现状”将保留当前结果，点击“还原”将恢复预览前状态。",
                "保持现状",
                "还原",
                NotificationType.Information,
                "Basic",
                "Danger");

            if (!keepCurrentState)
            {
                var restoreMessages = new List<string>();
                int restored = RestoreAppliedDisplaySettings(restoreStates, restoreMessages);
                _logger.LogInformation("Display configuration preview restored. RestoredCount={RestoredCount}, MessageCount={MessageCount}", restored, restoreMessages.Count);
                if (restoreMessages.Count > 0)
                {
                    ShowWarningToast("显示器还原", BuildDiagnosticsMessage(restoreMessages));
                }
                else
                {
                    ShowInfoToast("显示器还原", restored > 0 ? $"已还原 {restored} 个显示器设置。" : "未还原任何显示器设置。");
                }

                await HandleConfigurationChangedAsync(state, refreshMainOptions: false, refreshSubOptions: false);
                return;
            }

            _logger.LogInformation("Display configuration preview kept by user.");
            ShowInfoToast("显示器预览", "已保留当前预览设置。");
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
                ShowErrorToast("打开面板失败", ex.Message);
            }

            return Task.CompletedTask;
        }

        private bool TryApplyDisplayForLaunch(
            DisplayConfigurationRequest request,
            out IReadOnlyDictionary<string, DisplayState> restoreStates,
            out IReadOnlyList<string> messages)
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
            allValid &= TryBuildRequest(request.SelectedMainDisplay, request.SelectedMainRotation?.Angle ?? 0, request.SelectedMainResolution, request.SelectedMainRefreshRate, "主显示器", requests, mutableMessages);

            if (request.IsDualDisplay)
            {
                allValid &= TryBuildRequest(request.SelectedSubDisplay, request.SelectedSubRotation?.Angle ?? 0, request.SelectedSubResolution, request.SelectedSubRefreshRate, "副显示器", requests, mutableMessages);
            }

            if (!allValid)
            {
                _logger.LogWarning("Display configuration request validation failed. MessageCount={MessageCount}", mutableMessages.Count);
                return false;
            }

            var transactionResult = _displaySettingsTransactionCoordinator.Apply(requests);
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
            IList<string> messages)
        {
            int restored = 0;
            if (restoreStates == null)
            {
                return restored;
            }

            foreach (var state in restoreStates.Values)
            {
                var result = _displayConfigurationService.RestoreDisplaySettings(state);
                if (result.Succeeded)
                {
                    restored++;
                    continue;
                }

                messages?.Add($"还原 {state.DeviceName} 失败: {result.ErrorMessage}");
            }

            _logger.LogInformation("Display state restore completed. RestoredCount={RestoredCount}, RequestedCount={RequestedCount}, MessageCount={MessageCount}", restored, restoreStates.Count, messages?.Count ?? 0);
            return restored;
        }

        private DisplayModeOptions RefreshDisplayOptions(
            DisplayChoiceOption selectedDisplay,
            int rotation,
            string selectedResolution,
            string selectedRefreshRate)
        {
            if (selectedDisplay?.Display == null)
            {
                return new DisplayModeOptions(Array.Empty<string>(), Array.Empty<string>(), string.Empty, string.Empty, string.Empty);
            }

            var supportedModesResult = _displayConfigurationService.GetSupportedModes(selectedDisplay.Display.DeviceName);
            string tooltip = BuildTooltip(supportedModesResult);

            var resolutionItems = BuildResolutionItems(supportedModesResult.Modes, rotation, out string highestResolution);

            if (!resolutionItems.Contains(selectedResolution ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                selectedResolution = highestResolution;
            }

            var refreshItems = supportedModesResult.Modes
                .Where(mode => string.Equals(NormalizeResolutionByRotation(mode.Width, mode.Height, rotation), selectedResolution, StringComparison.OrdinalIgnoreCase))
                .Select(mode => mode.RefreshRate)
                .Distinct()
                .OrderBy(value => value)
                .Select(value => value.ToString())
                .ToList();

            if (!refreshItems.Contains(selectedRefreshRate ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                selectedRefreshRate = refreshItems.FirstOrDefault() ?? string.Empty;
            }

            return new DisplayModeOptions(resolutionItems, refreshItems, selectedResolution, selectedRefreshRate, tooltip);
        }

        private static IReadOnlyList<string> BuildResolutionItems(IReadOnlyList<DisplayMode> modes, int rotation, out string highestResolution)
        {
            highestResolution = string.Empty;
            if (modes == null || modes.Count == 0)
            {
                return Array.Empty<string>();
            }

            var highestMode = modes
                .OrderByDescending(mode => mode.Width * mode.Height)
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

        private void UpdateDisplayInfo(DisplayConfigurationState state, bool isMainTarget)
        {
            var selectedDisplay = isMainTarget ? state.SelectedMainDisplay : state.SelectedSubDisplay;
            var rotation = isMainTarget ? state.SelectedMainRotation?.Angle ?? 0 : state.SelectedSubRotation?.Angle ?? 0;
            var resolution = isMainTarget ? state.SelectedMainResolution : state.SelectedSubResolution;
            var refreshRate = isMainTarget ? state.SelectedMainRefreshRate : state.SelectedSubRefreshRate;

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

            var stateResult = _displayConfigurationService.GetCurrentState(selectedDisplay.Display.DeviceName);
            var outputInfo = stateResult.Succeeded
                ? $"设备: {selectedDisplay.Display.FriendlyName} ({selectedDisplay.Display.DeviceName})\n当前: {stateResult.State.Width}x{stateResult.State.Height} @ {stateResult.State.RefreshRate}Hz, {FormatRotationDisplay(_displayConfigurationService.OrientationToAngle(stateResult.State.Orientation))}"
                : $"设备: {selectedDisplay.Display.FriendlyName} ({selectedDisplay.Display.DeviceName})\n当前: 读取失败\n原因: {stateResult.ErrorMessage}";
            var startupInfo = $"旋转: {FormatRotationDisplay(rotation)}\n分辨率: {FormatTextOrFallback(resolution)}\n刷新率: {FormatRefreshRateDisplay(refreshRate)}";

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

        private void PersistSelectionState(DisplayConfigurationState state)
        {
            _logger.LogDebug("Persisting display selection state.");
            _appConfig.WriteSection(AppConfigDefaults.DisplaySectionName, new Dictionary<string, string>
            {
                ["displayconfigure"] = state.IsDisplayConfigurationEnabled.ToString().ToLowerInvariant(),
                ["mode"] = state.IsDualDisplay ? "dual" : "single",
                ["exitrestore"] = state.ExitRestore.ToString().ToLowerInvariant(),
                [MainDisplayIdConfigKey] = state.SelectedMainDisplay?.Display?.PersistentId ?? string.Empty,
                [SubDisplayIdConfigKey] = state.SelectedSubDisplay?.Display?.PersistentId ?? string.Empty,
                ["mainrotation"] = (state.SelectedMainRotation?.Angle ?? 0).ToString(),
                ["subrotation"] = (state.SelectedSubRotation?.Angle ?? 0).ToString(),
                ["mainresolution"] = state.SelectedMainResolution ?? string.Empty,
                ["subresolution"] = state.SelectedSubResolution ?? string.Empty,
                ["mainrefresh"] = state.SelectedMainRefreshRate ?? string.Empty,
                ["subrefresh"] = state.SelectedSubRefreshRate ?? string.Empty
            }, LegacyMainScreenConfigKey, LegacySubScreenConfigKey);
            SyncSpiceMonitorOverrides(state);
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

        private void SyncSpiceMonitorOverrides(DisplayConfigurationState state)
        {
            string mainMonitorValue = string.Empty;
            string subMonitorValue = string.Empty;

            if (state.IsDisplayConfigurationEnabled)
            {
                mainMonitorValue = state.SelectedMainDisplay?.Display?.DeviceName ?? string.Empty;
                subMonitorValue = state.IsDualDisplay
                    ? state.SelectedSubDisplay?.Display?.DeviceName ?? string.Empty
                    : string.Empty;
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
                    _logger.LogDebug("Spice monitor override sync skipped because active spice XML is missing.");
                    return;
                }

                // Pre-check: skip write if the monitor values are already set correctly.
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
                        && string.Equals(currentSubMonitor, subMonitorValue, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("Spice monitor override sync skipped because values are already current.");
                        return;
                    }
                }

                if (!_spiceXmlConfigEditor.ApplySpiceOptions(
                        spiceXmlPath,
                        new[]
                        {
                            new SpiceOptionUpdate(MainMonitorOptionName, mainMonitorValue, false),
                            new SpiceOptionUpdate(SubMonitorOptionName, subMonitorValue, false)
                        },
                        out var error))
                {
                    _logger.LogWarning("Failed to sync spice monitor overrides: {Error}", error);
                }
                else
                {
                    _logger.LogInformation("Spice monitor overrides synced.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync spice monitor overrides.");
            }
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

        private static DisplayChoiceOption GetDisplayByIndex(DisplayConfigurationState state, int index)
        {
            if (state.Displays.Count == 0)
            {
                return null;
            }

            if (index < 0 || index >= state.Displays.Count)
            {
                index = 0;
            }

            return state.Displays[index];
        }

        private static DisplayChoiceOption ResolveConfiguredDisplay(
            DisplayConfigurationState state,
            string persistentId,
            string legacyIndexText,
            int defaultIndex)
        {
            var selectedById = GetDisplayByPersistentId(state, persistentId);
            if (selectedById != null)
            {
                return selectedById;
            }

            if (!string.IsNullOrWhiteSpace(persistentId))
            {
                return GetDisplayByIndex(state, defaultIndex);
            }

            if (int.TryParse(legacyIndexText, out int legacyIndex))
            {
                return GetDisplayByIndex(state, legacyIndex) ?? GetDisplayByIndex(state, defaultIndex);
            }

            return GetDisplayByIndex(state, defaultIndex);
        }

        private static DisplayChoiceOption GetDisplayByPersistentId(DisplayConfigurationState state, string persistentId)
        {
            if (state?.Displays == null || string.IsNullOrWhiteSpace(persistentId))
            {
                return null;
            }

            return state.Displays.FirstOrDefault(display =>
                string.Equals(display?.Display?.PersistentId, persistentId, StringComparison.OrdinalIgnoreCase));
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

            if (!int.TryParse(refreshRate, out var refreshValue))
            {
                messages.Add($"{targetName}刷新率无效: {refreshRate}");
                return false;
            }

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
                return display.IsPrimary ? $"{displayId} - Primary" : displayId;
            }

            var label = $"{displayId} - {display.FriendlyName}";
            return display.IsPrimary ? $"{label} - Primary" : label;
        }

        private static string BuildTooltip(DisplayModeQueryResult result)
        {
            if (result == null || result.Succeeded || string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                return string.Empty;
            }

            return result.Modes.Count > 0
                ? $"{result.ErrorMessage}{SystemEnvironment.NewLine}已显示可读取到的显示模式，结果可能不完整。"
                : result.ErrorMessage;
        }

        private static string BuildDiagnosticsMessage(IReadOnlyList<string> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return "未知错误。";
            }

            const int maxMessageCount = 3;
            var visibleMessages = messages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Take(maxMessageCount)
                .ToList();

            if (visibleMessages.Count == 0)
            {
                return "未知错误。";
            }

            if (messages.Count > maxMessageCount)
            {
                visibleMessages.Add($"其余 {messages.Count - maxMessageCount} 项请查看当前设置。");
            }

            return string.Join(SystemEnvironment.NewLine, visibleMessages);
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
            return string.IsNullOrWhiteSpace(refreshRate) ? "未设置" : $"{refreshRate}Hz";
        }

        private readonly record struct DisplayModeOptions(
            IReadOnlyList<string> Resolutions,
            IReadOnlyList<string> RefreshRates,
            string SelectedResolution,
            string SelectedRefreshRate,
            string Tooltip);


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
