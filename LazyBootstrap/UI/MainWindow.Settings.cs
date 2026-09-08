using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Services;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;
using static LazyBootstrap.Controls.ControlHelpers;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private readonly SettingsState _settingsState = new();

        private sealed class SettingsState
        {
            public List<ServerPresetItem> ServerPresets { get; } = new List<ServerPresetItem>();

            public List<NetworkAdapterOption> NetworkAdapters { get; } = new List<NetworkAdapterOption>();

            public List<AsioDriverOption> AsioDrivers { get; } = new List<AsioDriverOption>();

            public bool NoAsphyxia { get; set; }

            public bool AutoLaunch { get; set; }

            public bool StartWithWindows { get; set; }

            public bool DisableSpiceFso { get; set; }

            public bool UseSystemSpiceConfig { get; set; }

            public bool GpuCompatLayerEnabled { get; set; }

            public bool IsSpiceConfigAvailable { get; set; } = true;

            public string SpiceConfigEmptyStateMessage { get; set; } = "未找到任何spice2x配置文件";

            public string ActiveServerPreset { get; set; } = string.Empty;

            public string ServerAddress { get; set; } = string.Empty;

            public string PcbId { get; set; } = string.Empty;

            public ServerPresetItem SelectedServerPreset { get; set; }

            public string NetworkAdapterIp { get; set; } = string.Empty;

            public string NetworkAdapterSubnet { get; set; } = string.Empty;

            public NetworkAdapterOption SelectedNetworkAdapter { get; set; }

            public string GpuCompatLayerRenderMode { get; set; } = "dx9on12";

            public bool Windowed { get; set; }

            public string DllInjection { get; set; } = string.Empty;

            public bool NetDump { get; set; }

            public bool DisableSubDisplay { get; set; }

            public int WindowModeIndex { get; set; }

            public bool PCoreOptimization { get; set; }

            public bool SubBorderless { get; set; }

            public bool ShowCursorTouchSim { get; set; }

            public bool WindowTopMost { get; set; }

            public string WindowSize { get; set; } = string.Empty;

            public bool SingleAdapter { get; set; }

            public bool NvidiaPerformanceProfile { get; set; }

            public bool SubWindowTopMost { get; set; }

            public bool SubForceRender { get; set; }

            public string ConfiguredAsioDriverName { get; set; } = string.Empty;

            public AsioDriverOption SelectedAsioDriver { get; set; }

            public bool Asio2Ch { get; set; }

            public int VolumeBoostIndex { get; set; }

            public int ResampleIndex { get; set; }

            public bool WasapiShared { get; set; }

            public bool LowLatencySharedAudio { get; set; }

            public bool CardIo { get; set; }

            public bool HidSmartCard { get; set; }
        }

        private sealed class AsioDriverOption
        {
            public AsioDriverOption(string displayName, string driverName)
            {
                DisplayName = displayName ?? string.Empty;
                DriverName = driverName ?? string.Empty;
            }

            public string DisplayName { get; }

            public string DriverName { get; }

            public override string ToString() => DisplayName;
        }

        private sealed class NetworkAdapterOption
        {
            public NetworkAdapterOption(string displayName, string ipAddress, string subnetMask)
            {
                DisplayName = displayName ?? string.Empty;
                IpAddress = ipAddress ?? string.Empty;
                SubnetMask = subnetMask ?? string.Empty;
            }

            public string DisplayName { get; }

            public string IpAddress { get; }

            public string SubnetMask { get; }

            public override string ToString() => DisplayName;
        }

        private bool _isLoadingSettings;
        private bool _isSyncingSettingsUi;
        private bool _isUpdatingGpuCompatLayerUi;
        private bool _isUpdatingServerPresetUi;
        private bool _isUpdatingAsioDriverUi;
        private bool _isUpdatingNetworkUi;
        private bool _pendingNativeTouchDeprecatedDialog;
        private string _pendingNativeTouchMigrationError = string.Empty;

        /// <summary>Loads the initial (startup) settings and applies them to the UI.</summary>
        private async Task InitializeSettingsStartupAsync()
        {
            await LoadSettingsStateAsync(_settingsState);
            DisableDeprecatedNativeTouchAtStartup();
            ApplyStartupSettingsToUi();
        }

        private void DisableDeprecatedNativeTouchAtStartup()
        {
            const string optionName = "sdvxnativetouch";
            string spiceXmlPath = _paths.ResolveSpiceXmlPath(_settingsState.UseSystemSpiceConfig);
            bool enabledOptionFound = false;
            _pendingNativeTouchDeprecatedDialog = false;
            _pendingNativeTouchMigrationError = string.Empty;

            try
            {
                // File.Exists also returns false on access errors; distinguish those from missing files.
                try
                {
                    File.GetAttributes(spiceXmlPath);
                }
                catch (FileNotFoundException)
                {
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }

                if (!_spiceXmlConfigEditor.TryLoadOptionsContext(
                        spiceXmlPath,
                        LoadOptions.PreserveWhitespace,
                        false,
                        out var context,
                        out var message,
                        out var warning))
                {
                    // Missing files, game entries and options do not require migration.
                    if (!warning && !string.IsNullOrWhiteSpace(message))
                    {
                        throw new IOException(message);
                    }

                    return;
                }

                var enabledOptions = context.OptionsElement.Elements("option")
                    .Where(option => string.Equals(option.Attribute("name")?.Value, optionName, StringComparison.Ordinal)
                        && string.Equals(option.Attribute("value")?.Value, "/ENABLED", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (enabledOptions.Count == 0)
                {
                    return;
                }

                enabledOptionFound = true;
                // The lookup normally keeps the first duplicate, which may already be disabled.
                context.OptionLookup[optionName] = enabledOptions[0];
                foreach (var option in enabledOptions.Skip(1))
                {
                    option.SetAttributeValue("value", string.Empty);
                }

                _spiceXmlConfigEditor.ApplyUpdates(context, [new SpiceOptionUpdate(optionName, string.Empty)]);
                _pendingNativeTouchDeprecatedDialog = true;
                _logger.LogInformation("Deprecated native touch option disabled in active spice config: {SpiceXmlPath}", spiceXmlPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deprecated native touch option migration failed. EnabledOptionFound={EnabledOptionFound}, SpiceXmlPath={SpiceXmlPath}", enabledOptionFound, spiceXmlPath);
                _pendingNativeTouchMigrationError = enabledOptionFound
                    ? "检测到已弃用的“原生触控输入”选项，但自动关闭失败。\n\n请检查配置文件是否被占用以及写入权限，然后重新启动程序。"
                    : "无法读取当前 spice2x 配置，未能检查已弃用的“原生触控输入”选项。\n\n请检查配置文件是否有效、是否被占用以及读取权限，然后重新启动程序。";
                _pendingNativeTouchMigrationError += $"\n\n配置文件：{spiceXmlPath}";
            }
        }

        /// <summary>Warms up deferred settings options (ASIO/network/...) and applies them.</summary>
        private async Task WarmSettingsDeferredAsync()
        {
            await LoadDeferredSettingsStateAsync(_settingsState);
            ApplyDeferredSettingsToUi();
        }

        private void InitializeSettingsComponents()
        {
            InitializeGpuCompatLayerControls();
            InitializeNetworkBindings();
            InitializeStartupSettingsBindings();
            InitializeSpiceSettingsBindings();
            InitializeServerPresetBindings();
        }

        private void OnSettingsPageSelected()
        {
            ApplyServerPresetStateToUi();
        }

        private async void OnEditConfigClick(object sender, RoutedEventArgs e)
        {
            try
            {
                using var busy = BeginBusy(BusyPresentation.GlobalOverlay, "spicecfg 运行中...");
                await EditConfigAsync(_settingsState);
                ApplyStartupSettingsToUi();
                ApplyDeferredSettingsToUi();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edit spicecfg workflow failed.");
            }
        }

        private async void OnSelectGpuCompatLayerRenderModeClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
            {
                return;
            }

            var mode = btn.Tag?.ToString() ?? btn.CommandParameter?.ToString() ?? string.Empty;
            string normalizedMode = GpuCompatLayerConfigurator.NormalizeRenderMode(mode);
            if (string.Equals(
                    GpuCompatLayerConfigurator.NormalizeRenderMode(_settingsState.GpuCompatLayerRenderMode),
                    normalizedMode,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settingsState.GpuCompatLayerRenderMode = normalizedMode;
                UpdateGpuCompatLayerStatus();
                return;
            }

            _settingsState.GpuCompatLayerRenderMode = normalizedMode;
            await PersistGpuCompatLayerRenderModeAsync(_settingsState);
            UpdateGpuCompatLayerStatus();
        }

        private async void OnAddServerPresetClick(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox { Watermark = "预设名" };
            var urlBox = new TextBox { Watermark = "http://SERVERURL:PORT" };
            var pcbBox = new TextBox { Watermark = "PCBID" };
            var content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "请填写预设信息" },
                    nameBox,
                    urlBox,
                    pcbBox
                }
            };

            if (!await ShowDialogAsync("新建服务器预设", content, "创建", "取消"))
            {
                return;
            }

            await AddServerPresetAsync(
                _settingsState,
                nameBox.Text,
                urlBox.Text,
                pcbBox.Text);
            ApplyServerPresetStateToUi();
        }

        private async void OnDeleteServerPresetClick(object sender, RoutedEventArgs e)
        {
            string validationError = GetServerPresetDeletionError(_settingsState);
            if (!string.IsNullOrEmpty(validationError))
            {
                ShowWarningToast("删除预设", validationError);
                return;
            }

            var preset = _settingsState.SelectedServerPreset;
            if (!await ShowDialogAsync(
                    "删除服务器预设",
                    $"确定删除预设「{preset.Name}」？",
                    "删除",
                    "取消",
                    NotificationType.Warning))
            {
                return;
            }

            await DeleteServerPresetAsync(_settingsState);
            ApplyServerPresetStateToUi();
        }

        private async void OnOpenNetworkAdapterPickerClick(object sender, RoutedEventArgs e)
        {
            var choices = GetNetworkAdapterChoices(_settingsState);
            var selectedChoice = choices.FirstOrDefault(choice =>
                                     string.Equals(choice.IpAddress, _settingsState.NetworkAdapterIp ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(choice.SubnetMask, _settingsState.NetworkAdapterSubnet ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                 ?? choices.FirstOrDefault();
            var adapterListBox = new ListBox
            {
                ItemsSource = choices,
                SelectedItem = selectedChoice,
                MinHeight = 240,
                MaxHeight = 360
            };
            var content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "请选择要读取参数的网卡。" },
                    adapterListBox
                }
            };

            if (!await ShowDialogAsync("选择网卡", content, "确定", "取消"))
            {
                return;
            }

            if (adapterListBox.SelectedItem is not NetworkAdapterOption choice)
            {
                ShowWarningToast("选择网卡", "请选择一个网卡。");
                return;
            }

            _settingsState.SelectedNetworkAdapter = choice;
            _settingsState.NetworkAdapterIp = choice.IpAddress;
            _settingsState.NetworkAdapterSubnet = choice.SubnetMask;
            await PersistNetworkSettingsAsync(_settingsState);
            ApplyNetworkAdapterStateFromState();
        }

        private async void OnOpenAsioControlPanelClick(object sender, RoutedEventArgs e)
        {
            await OpenAsioControlPanelAsync(_settingsState);
        }

        private void ApplyStartupSettingsToUi()
        {
            bool previousLoadingState = _isLoadingSettings;
            _isLoadingSettings = true;

            try
            {
                if (NoAsphyxiaToggleSwitch != null)
                {
                    NoAsphyxiaToggleSwitch.IsChecked = _settingsState.NoAsphyxia;
                }

                if (AutoLaunchToggleSwitch != null)
                {
                    AutoLaunchToggleSwitch.IsChecked = _settingsState.AutoLaunch;
                }

                if (StartWithWindowsToggleSwitch != null)
                {
                    StartWithWindowsToggleSwitch.IsChecked = _settingsState.StartWithWindows;
                }

                if (DisableSpiceFsoToggleSwitch != null)
                {
                    DisableSpiceFsoToggleSwitch.IsChecked = _settingsState.DisableSpiceFso;
                }

                if (UseSystemSpiceConfigToggleSwitch != null)
                {
                    UseSystemSpiceConfigToggleSwitch.IsChecked = _settingsState.UseSystemSpiceConfig;
                }

                UpdateGpuCompatLayerStatus();
                ApplyServerPresetStateToUi();
                ApplySettingsAvailabilityStateToUi();
            }
            finally
            {
                _isLoadingSettings = previousLoadingState;
            }
        }

        private void ApplyDeferredSettingsToUi()
        {
            bool previousLoadingState = _isLoadingSettings;
            _isLoadingSettings = true;

            try
            {
                ApplySettingsAvailabilityStateToUi();
                if (!_settingsState.IsSpiceConfigAvailable)
                {
                    return;
                }

                ApplySpiceSettingsFromState();
                ApplySpiceTextInputsFromState();
                ApplyAsioDriverChoicesFromState();
                ApplyNetworkAdapterStateFromState();
                ApplyServerPresetStateToUi();
                UpdateGpuCompatLayerStatus();
            }
            finally
            {
                _isLoadingSettings = previousLoadingState;
            }
        }

        private void ApplySettingsAvailabilityStateToUi()
        {
            bool isSpiceConfigAvailable = _settingsState.IsSpiceConfigAvailable;

            if (GameSettingsLayout != null)
            {
                GameSettingsLayout.IsVisible = isSpiceConfigAvailable;
            }

            if (SettingsEmptyStatePanel != null)
            {
                SettingsEmptyStatePanel.IsVisible = !isSpiceConfigAvailable;
            }

            if (SettingsEmptyStateTextBlock != null)
            {
                SettingsEmptyStateTextBlock.Text = _settingsState.SpiceConfigEmptyStateMessage ?? string.Empty;
            }

            if (SettingsMoreFeaturesHintTextBlock != null)
            {
                SettingsMoreFeaturesHintTextBlock.IsVisible = isSpiceConfigAvailable;
            }

            UpdateUseSystemSpiceConfigSwitchVisibility();
        }

        private void ApplySpiceSettingsFromState()
        {
            if (WindowedToggleSwitch != null)
            {
                WindowedToggleSwitch.IsChecked = _settingsState.Windowed;
            }

            if (NetDumpToggleSwitch != null)
            {
                NetDumpToggleSwitch.IsChecked = _settingsState.NetDump;
            }

            if (DisableSubDisplayToggleSwitch != null)
            {
                DisableSubDisplayToggleSwitch.IsChecked = _settingsState.DisableSubDisplay;
            }

            if (WindowModeComboBox != null)
            {
                WindowModeComboBox.SelectedIndex = Math.Clamp(_settingsState.WindowModeIndex, 0, Math.Max(0, WindowModeComboBox.ItemCount - 1));
            }

            if (PCoreOptimizationToggleSwitch != null)
            {
                PCoreOptimizationToggleSwitch.IsChecked = _settingsState.PCoreOptimization;
            }

            if (SubBorderlessToggleSwitch != null)
            {
                SubBorderlessToggleSwitch.IsChecked = _settingsState.SubBorderless;
            }

            if (ShowCursorTouchSimToggleSwitch != null)
            {
                ShowCursorTouchSimToggleSwitch.IsChecked = _settingsState.ShowCursorTouchSim;
            }

            if (WindowTopMostToggleSwitch != null)
            {
                WindowTopMostToggleSwitch.IsChecked = _settingsState.WindowTopMost;
            }

            if (SingleAdapterToggleSwitch != null)
            {
                SingleAdapterToggleSwitch.IsChecked = _settingsState.SingleAdapter;
            }

            if (NvidiaPerformanceProfileToggleSwitch != null)
            {
                NvidiaPerformanceProfileToggleSwitch.IsChecked = _settingsState.NvidiaPerformanceProfile;
            }

            if (SubWindowTopMostToggleSwitch != null)
            {
                SubWindowTopMostToggleSwitch.IsChecked = _settingsState.SubWindowTopMost;
            }

            if (SubForceRenderToggleSwitch != null)
            {
                SubForceRenderToggleSwitch.IsChecked = _settingsState.SubForceRender;
            }

            if (Asio2ChToggleSwitch != null)
            {
                Asio2ChToggleSwitch.IsChecked = _settingsState.Asio2Ch;
            }

            if (VolumeBoostComboBox != null)
            {
                VolumeBoostComboBox.SelectedIndex = Math.Clamp(_settingsState.VolumeBoostIndex, 0, Math.Max(0, VolumeBoostComboBox.ItemCount - 1));
            }

            if (ResampleComboBox != null)
            {
                ResampleComboBox.SelectedIndex = Math.Clamp(_settingsState.ResampleIndex, 0, Math.Max(0, ResampleComboBox.ItemCount - 1));
            }

            if (WasapiSharedToggleSwitch != null)
            {
                WasapiSharedToggleSwitch.IsChecked = _settingsState.WasapiShared;
            }

            if (LowLatencySharedAudioToggleSwitch != null)
            {
                LowLatencySharedAudioToggleSwitch.IsChecked = _settingsState.LowLatencySharedAudio;
            }

            if (CardIoToggleSwitch != null)
            {
                CardIoToggleSwitch.IsChecked = _settingsState.CardIo;
            }

            if (HidSmartCardToggleSwitch != null)
            {
                HidSmartCardToggleSwitch.IsChecked = _settingsState.HidSmartCard;
            }
        }

        private void ApplySpiceTextInputsFromState()
        {
            SetTextBoxTextIfNeeded(DllInjectionTextBox, _settingsState.DllInjection);
            SetTextBoxTextIfNeeded(WindowSizeTextBox, _settingsState.WindowSize);
        }

        private void ApplyAsioDriverChoicesFromState()
        {
            if (AsioDriverComboBox == null)
            {
                return;
            }

            var choices = _settingsState.AsioDrivers.Count > 0
                ? _settingsState.AsioDrivers.ToList()
                : new List<AsioDriverOption> { new("无", string.Empty) };

            var selectedDriverName = _settingsState.SelectedAsioDriver?.DriverName
                ?? _settingsState.ConfiguredAsioDriverName
                ?? string.Empty;

            var targetChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.DriverName, selectedDriverName, StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault()
                ?? new AsioDriverOption("无", string.Empty);

            _isUpdatingAsioDriverUi = true;
            try
            {
                AsioDriverComboBox.Items.Clear();
                foreach (var choice in choices)
                {
                    AsioDriverComboBox.Items.Add(choice);
                }

                AsioDriverComboBox.SelectedItem = targetChoice;
            }
            finally
            {
                _isUpdatingAsioDriverUi = false;
            }

            UpdateAsioControlPanelButtonState();
        }

        private void ApplyNetworkAdapterStateFromState()
        {
            var networkIp = NormalizeNetworkValue(_settingsState.NetworkAdapterIp);
            var subnetMask = NormalizeNetworkValue(_settingsState.NetworkAdapterSubnet);

            _isUpdatingNetworkUi = true;
            try
            {
                SetTextBoxTextIfNeeded(NetworkAdapterIpTextBox, networkIp);
                SetTextBoxTextIfNeeded(NetworkAdapterSubnetTextBox, subnetMask);
            }
            finally
            {
                _isUpdatingNetworkUi = false;
            }

            if (OpenNetworkAdapterPickerButton != null)
            {
                bool hasSelectableChoice = _settingsState.NetworkAdapters.Count > 1;
                var selectedAdapter = _settingsState.SelectedNetworkAdapter;

                OpenNetworkAdapterPickerButton.Content = hasSelectableChoice ? "选择" : "无可用网卡";
                OpenNetworkAdapterPickerButton.IsEnabled = true;
                ToolTip.SetTip(
                    OpenNetworkAdapterPickerButton,
                    hasSelectableChoice
                        ? $"当前配置：{selectedAdapter?.DisplayName ?? BuildCurrentNetworkAdapterDisplayName(networkIp, subnetMask)}"
                        : "未检测到可用网卡");
            }
        }

        private async void OnGpuCompatLayerToggleChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings || _isUpdatingGpuCompatLayerUi)
                {
                    return;
                }

                _settingsState.GpuCompatLayerEnabled = GpuCompatLayerToggleSwitch?.IsChecked == true;
                await PersistGpuCompatLayerToggleAsync(_settingsState);
                UpdateGpuCompatLayerStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persist compatibility layer toggle failed.");
            }
        }

        private void UpdateGpuCompatLayerStatus()
        {
            bool modulesDirectoryExists = HasGpuCompatLayerModulesDirectory();
            bool gpuCompatLayerEnabled = _settingsState.GpuCompatLayerEnabled;

            if (GpuCompatLayerRenderModeBusyArea != null)
            {
                GpuCompatLayerRenderModeBusyArea.IsBusy = gpuCompatLayerEnabled;
            }

            if (GpuCompatLayerStatusTextBlock != null)
            {
                if (!modulesDirectoryExists && !gpuCompatLayerEnabled)
                {
                    GpuCompatLayerStatusTextBlock.Text = "未找到modules目录，无法启用显卡兼容层。";
                    GpuCompatLayerStatusTextBlock.IsVisible = true;
                }
                else
                {
                    GpuCompatLayerStatusTextBlock.Text = string.Empty;
                    GpuCompatLayerStatusTextBlock.IsVisible = false;
                }
            }

            _isUpdatingGpuCompatLayerUi = true;
            try
            {
                if (GpuCompatLayerToggleSwitch != null)
                {
                    GpuCompatLayerToggleSwitch.IsChecked = gpuCompatLayerEnabled;
                    GpuCompatLayerToggleSwitch.IsEnabled = gpuCompatLayerEnabled || modulesDirectoryExists;
                }

                bool chipsEnabled = !gpuCompatLayerEnabled && modulesDirectoryExists;
                string renderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(_settingsState.GpuCompatLayerRenderMode);
                if (GpuCompatLayerDx9on12RadioButton != null)
                {
                    GpuCompatLayerDx9on12RadioButton.IsChecked = string.Equals(renderMode, "dx9on12", StringComparison.OrdinalIgnoreCase);
                }

                if (GpuCompatLayerDx9on12ExternalRadioButton != null)
                {
                    GpuCompatLayerDx9on12ExternalRadioButton.IsChecked = string.Equals(renderMode, "dx9on12_external", StringComparison.OrdinalIgnoreCase);
                }

                if (GpuCompatLayerDxvkRadioButton != null)
                {
                    GpuCompatLayerDxvkRadioButton.IsChecked = string.Equals(renderMode, "dxvk", StringComparison.OrdinalIgnoreCase);
                }

                if (GpuCompatLayerDx9on12RadioButton != null) GpuCompatLayerDx9on12RadioButton.IsEnabled = chipsEnabled;
                if (GpuCompatLayerDx9on12ExternalRadioButton != null) GpuCompatLayerDx9on12ExternalRadioButton.IsEnabled = chipsEnabled;
                if (GpuCompatLayerDxvkRadioButton != null) GpuCompatLayerDxvkRadioButton.IsEnabled = chipsEnabled;
            }
            finally
            {
                _isUpdatingGpuCompatLayerUi = false;
            }
        }

        private async void OnServerPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings || _isSyncingSettingsUi || _isUpdatingServerPresetUi)
                {
                    return;
                }

                if (ServerPresetComboBox?.SelectedItem is not ServerPresetItem preset)
                {
                    return;
                }

                _settingsState.SelectedServerPreset = preset;
                await PersistSelectedServerPresetAsync(_settingsState);
                ApplyServerPresetStateToUi();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persist server preset selection failed.");
            }
        }

        private void ApplyServerPresetStateToUi()
        {
            _isUpdatingServerPresetUi = true;
            _isSyncingSettingsUi = true;
            try
            {
                if (ServerPresetComboBox != null)
                {
                    ReplaceComboBoxItems(ServerPresetComboBox, _settingsState.ServerPresets);
                    var selectedPreset = FindServerPreset(_settingsState.SelectedServerPreset);
                    if (selectedPreset != null && !ReferenceEquals(ServerPresetComboBox.SelectedItem, selectedPreset))
                    {
                        ServerPresetComboBox.SelectedItem = selectedPreset;
                    }
                }

                SetTextBoxTextIfNeeded(ServerAddressTextBox, _settingsState.ServerAddress);
                SetTextBoxTextIfNeeded(PcbIdTextBox, _settingsState.PcbId);
            }
            finally
            {
                _isSyncingSettingsUi = false;
                _isUpdatingServerPresetUi = false;
            }
        }

        private ServerPresetItem FindServerPreset(ServerPresetItem selected)
        {
            if (selected == null)
            {
                return null;
            }

            var presets = _settingsState.ServerPresets;
            for (int i = 0; i < presets.Count; i++)
            {
                if (ReferenceEquals(presets[i], selected))
                {
                    return presets[i];
                }
            }

            for (int i = 0; i < presets.Count; i++)
            {
                if (string.Equals(presets[i].Name, selected.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return presets[i];
                }
            }

            return null;
        }

        private void UpdateUseSystemSpiceConfigSwitchVisibility()
        {
            bool show = _settingsState.IsSpiceConfigAvailable;
            if (UseSystemSpiceConfigRow != null)
            {
                UseSystemSpiceConfigRow.IsVisible = show;
            }
            else if (UseSystemSpiceConfigToggleSwitch != null)
            {
                UseSystemSpiceConfigToggleSwitch.IsVisible = show;
            }
        }

        private void UpdateAsioControlPanelButtonState()
        {
            if (OpenAsioControlPanelButton == null)
            {
                return;
            }

            var selectedDriverName = _settingsState.SelectedAsioDriver?.DriverName
                ?? _settingsState.ConfiguredAsioDriverName
                ?? string.Empty;

            OpenAsioControlPanelButton.IsEnabled = OperatingSystem.IsWindows()
                && !string.IsNullOrWhiteSpace(selectedDriverName);
        }

        private Task PersistSpice() => PersistSpiceSettingsAsync(_settingsState);

        private void BindToggleSwitch(ToggleSwitch toggle, Action<bool> setValue, Func<Task> persist)
        {
            if (toggle is null) return;
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (_isLoadingSettings) return;
                setValue(toggle.IsChecked == true);
                await persist();
            };
        }

        private static string NormalizeNetworkValue(string value) => (value ?? string.Empty).Trim();

        private static string BuildCurrentNetworkAdapterDisplayName(string ipAddress, string subnetMask)
        {
            var normalizedIpAddress = NormalizeNetworkValue(ipAddress);
            var normalizedSubnetMask = NormalizeNetworkValue(subnetMask);
            if (string.IsNullOrEmpty(normalizedIpAddress) && string.IsNullOrEmpty(normalizedSubnetMask))
            {
                return "无";
            }

            if (string.IsNullOrEmpty(normalizedIpAddress))
            {
                return $"{normalizedSubnetMask}（当前配置）";
            }

            if (string.IsNullOrEmpty(normalizedSubnetMask))
            {
                return $"{normalizedIpAddress}（当前配置）";
            }

            return $"{normalizedIpAddress} / {normalizedSubnetMask}（当前配置）";
        }

        private void InitializeGpuCompatLayerControls()
        {
            if (GpuCompatLayerToggleSwitch != null)
            {
                GpuCompatLayerToggleSwitch.IsCheckedChanged -= OnGpuCompatLayerToggleChanged;
                GpuCompatLayerToggleSwitch.IsCheckedChanged += OnGpuCompatLayerToggleChanged;
            }
        }

        private void InitializeNetworkBindings()
        {
            if (ServerAddressTextBox != null)
            {
                ServerAddressTextBox.Watermark = "http://SERVER:PORT";
            }
            if (PcbIdTextBox != null)
            {
                PcbIdTextBox.Watermark = "根据实际需要填写，否则留空";
            }
            if (NetworkAdapterIpTextBox != null)
            {
                NetworkAdapterIpTextBox.Watermark = string.Empty;
                NetworkAdapterIpTextBox.TextChanged += async (s, e) =>
                {
                    if (_isLoadingSettings || _isUpdatingNetworkUi) return;
                    _settingsState.NetworkAdapterIp = NetworkAdapterIpTextBox.Text ?? string.Empty;
                    _settingsState.NetworkAdapterSubnet = NetworkAdapterSubnetTextBox?.Text ?? string.Empty;
                    await PersistNetworkSettingsAsync(_settingsState);
                    ApplyNetworkAdapterStateFromState();
                };
            }
            if (NetworkAdapterSubnetTextBox != null)
            {
                NetworkAdapterSubnetTextBox.Watermark = string.Empty;
                NetworkAdapterSubnetTextBox.TextChanged += async (s, e) =>
                {
                    if (_isLoadingSettings || _isUpdatingNetworkUi) return;
                    _settingsState.NetworkAdapterIp = NetworkAdapterIpTextBox?.Text ?? string.Empty;
                    _settingsState.NetworkAdapterSubnet = NetworkAdapterSubnetTextBox.Text ?? string.Empty;
                    await PersistNetworkSettingsAsync(_settingsState);
                    ApplyNetworkAdapterStateFromState();
                };
            }

            if (OpenNetworkAdapterPickerButton != null)
            {
                OpenNetworkAdapterPickerButton.Content = "加载中...";
                OpenNetworkAdapterPickerButton.IsEnabled = false;
                ToolTip.SetTip(OpenNetworkAdapterPickerButton, "正在读取网卡配置...");
            }
        }

        private void InitializeStartupSettingsBindings()
        {
            BindToggleSwitch(WindowedToggleSwitch,
                v => _settingsState.Windowed = v,
                () => PersistSpiceSettingsAsync(_settingsState));

            if (NoAsphyxiaToggleSwitch != null)
            {
                NoAsphyxiaToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.NoAsphyxia = NoAsphyxiaToggleSwitch.IsChecked == true;
                    await PersistLauncherSettingsAsync(_settingsState);
                };
            }

            if (AutoLaunchToggleSwitch != null)
            {
                AutoLaunchToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.AutoLaunch = AutoLaunchToggleSwitch.IsChecked == true;
                    await PersistLauncherSettingsAsync(_settingsState);
                };
            }

            if (StartWithWindowsToggleSwitch != null)
            {
                StartWithWindowsToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    bool requestedValue = StartWithWindowsToggleSwitch.IsChecked == true;
                    await SetStartWithWindowsAsync(_settingsState, requestedValue);
                    ApplyStartupSettingsToUi();
                };
            }

            if (DisableSpiceFsoToggleSwitch != null)
            {
                DisableSpiceFsoToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.DisableSpiceFso = DisableSpiceFsoToggleSwitch.IsChecked == true;
                    await PersistFsoToggleAsync(_settingsState);
                    ApplyStartupSettingsToUi();
                };
            }

            if (UseSystemSpiceConfigToggleSwitch != null)
            {
                UseSystemSpiceConfigToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    try
                    {
                        bool requestedValue = UseSystemSpiceConfigToggleSwitch.IsChecked == true;
                        await SetUseSystemSpiceConfigAsync(_settingsState, requestedValue);
                        ApplyStartupSettingsToUi();
                        ApplyDeferredSettingsToUi();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Persist use system spice config toggle failed.");
                        ApplyStartupSettingsToUi();
                        ApplyDeferredSettingsToUi();
                    }
                };
            }
        }

        private void InitializeSpiceSettingsBindings()
        {
            if (DllInjectionTextBox != null)
            {
                DllInjectionTextBox.Watermark = "example.dll";
                DllInjectionTextBox.TextChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.DllInjection = DllInjectionTextBox.Text ?? string.Empty;
                    await PersistSpiceSettingsAsync(_settingsState);
                };
            }

            BindToggleSwitch(NetDumpToggleSwitch, v => _settingsState.NetDump = v, PersistSpice);
            BindToggleSwitch(DisableSubDisplayToggleSwitch, v => _settingsState.DisableSubDisplay = v, PersistSpice);

            if (WindowModeComboBox != null)
            {
                WindowModeComboBox.SelectionChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.WindowModeIndex = WindowModeComboBox.SelectedIndex < 0 ? 0 : WindowModeComboBox.SelectedIndex;
                    await PersistSpice();
                };
            }

            BindToggleSwitch(PCoreOptimizationToggleSwitch, v => _settingsState.PCoreOptimization = v, PersistSpice);
            BindToggleSwitch(SubBorderlessToggleSwitch, v => _settingsState.SubBorderless = v, PersistSpice);
            BindToggleSwitch(ShowCursorTouchSimToggleSwitch, v => _settingsState.ShowCursorTouchSim = v, PersistSpice);
            BindToggleSwitch(WindowTopMostToggleSwitch, v => _settingsState.WindowTopMost = v, PersistSpice);
            BindToggleSwitch(SingleAdapterToggleSwitch, v => _settingsState.SingleAdapter = v, PersistSpice);
            BindToggleSwitch(NvidiaPerformanceProfileToggleSwitch, v => _settingsState.NvidiaPerformanceProfile = v, PersistSpice);
            BindToggleSwitch(SubWindowTopMostToggleSwitch, v => _settingsState.SubWindowTopMost = v, PersistSpice);
            BindToggleSwitch(SubForceRenderToggleSwitch, v => _settingsState.SubForceRender = v, PersistSpice);
            BindToggleSwitch(CardIoToggleSwitch, v => _settingsState.CardIo = v, PersistSpice);
            BindToggleSwitch(HidSmartCardToggleSwitch, v => _settingsState.HidSmartCard = v, PersistSpice);

            if (WindowSizeTextBox != null)
            {
                WindowSizeTextBox.TextChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.WindowSize = WindowSizeTextBox.Text ?? string.Empty;
                    await PersistSpice();
                };
            }
            if (AsioDriverComboBox != null)
            {
                ApplyAsioDriverChoicesFromState();

                AsioDriverComboBox.DropDownOpened += (s, e) =>
                {
                    ApplyAsioDriverChoicesFromState();
                };

                AsioDriverComboBox.SelectionChanged += async (_, _) =>
                {
                    if (_isLoadingSettings || _isUpdatingAsioDriverUi) return;
                    if (AsioDriverComboBox.SelectedItem is AsioDriverOption choice)
                    {
                        _settingsState.SelectedAsioDriver = choice;
                        _settingsState.ConfiguredAsioDriverName = choice.DriverName;
                    }
                    else
                    {
                        _settingsState.SelectedAsioDriver = null;
                        _settingsState.ConfiguredAsioDriverName = string.Empty;
                    }

                    await PersistSpice();
                    UpdateAsioControlPanelButtonState();
                };
            }
            BindToggleSwitch(Asio2ChToggleSwitch, v => _settingsState.Asio2Ch = v, PersistSpice);
            if (VolumeBoostComboBox != null)
            {
                VolumeBoostComboBox.SelectionChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.VolumeBoostIndex = VolumeBoostComboBox.SelectedIndex < 0 ? 0 : VolumeBoostComboBox.SelectedIndex;
                    await PersistSpice();
                };
            }
            if (ResampleComboBox != null)
            {
                ResampleComboBox.SelectionChanged += async (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    _settingsState.ResampleIndex = ResampleComboBox.SelectedIndex < 0 ? 0 : ResampleComboBox.SelectedIndex;
                    await PersistSpice();
                };
            }
            BindToggleSwitch(WasapiSharedToggleSwitch, v => _settingsState.WasapiShared = v, PersistSpice);
            BindToggleSwitch(LowLatencySharedAudioToggleSwitch, v => _settingsState.LowLatencySharedAudio = v, PersistSpice);
        }

        private void InitializeServerPresetBindings()
        {
            if (ServerAddressTextBox != null)
            {
                ServerAddressTextBox.TextChanged += async (s, e) =>
                {
                    if (_isLoadingSettings || _isSyncingSettingsUi) return;
                    _settingsState.ServerAddress = ServerAddressTextBox.Text ?? string.Empty;
                    _settingsState.PcbId = PcbIdTextBox?.Text ?? string.Empty;
                    await PersistServerEndpointAsync(_settingsState);
                    ApplyServerPresetStateToUi();
                };
            }
            if (PcbIdTextBox != null)
            {
                PcbIdTextBox.TextChanged += async (s, e) =>
                {
                    if (_isLoadingSettings || _isSyncingSettingsUi) return;
                    _settingsState.ServerAddress = ServerAddressTextBox?.Text ?? string.Empty;
                    _settingsState.PcbId = PcbIdTextBox.Text ?? string.Empty;
                    await PersistServerEndpointAsync(_settingsState);
                    ApplyServerPresetStateToUi();
                };
            }
        }

        private const string NonePresetName = "无";
        private const string AsphyxiaPresetName = "Asphyxia";
        private const string AsphyxiaDefaultUrl = "http://localhost:8083";
        private const string UseSystemConfigKey = "use-system-config";
        private const string DisableFsoConfigKey = "disable-fso";
        private const string AutoLaunchConfigKey = "auto-launch";
        private const string MissingSpiceConfigMessage = "未找到任何spice2x配置文件";

        private SpiceXmlConfigEditor _spiceXmlConfigEditor = null!;
        private GpuCompatLayerConfigurator _gpuCompatLayerConfigurator = null!;
        private WindowsAppCompatLayerService _appCompatLayerService = null!;
        private WindowsStartupService _windowsStartupService = null!;

        private sealed record SpiceOptionDescriptor(
            string XmlName,
            Func<SettingsState, string> GetXmlValue,
            Action<SettingsState, string> ApplyXmlValue);

        private static SpiceOptionDescriptor CreateBooleanOptionDescriptor(string name,
            Func<SettingsState, bool> getter,
            Action<SettingsState, bool> setter,
            string enabledValue) => new(name,
                state => getter(state) ? enabledValue : string.Empty,
                (state, xmlValue) => setter(state, string.Equals(xmlValue, enabledValue, StringComparison.OrdinalIgnoreCase)));

        private static SpiceOptionDescriptor CreateStringOptionDescriptor(string name,
            Func<SettingsState, string> getter,
            Action<SettingsState, string> setter) => new(name,
                state => getter(state) ?? string.Empty,
                (state, xmlValue) => setter(state, xmlValue ?? string.Empty));

        private static readonly SpiceOptionDescriptor[] GeneralSpiceOptions =
        [
            CreateBooleanOptionDescriptor("w", state => state.Windowed, (state, value) => state.Windowed = value, "/ENABLED"),
            CreateStringOptionDescriptor("k", state => state.DllInjection, (state, value) => state.DllInjection = value),
            CreateBooleanOptionDescriptor("sp2x-processefficiency", state => state.PCoreOptimization, (state, value) => state.PCoreOptimization = value, "pcores"),
            CreateBooleanOptionDescriptor("sp2x-sdvxnosub", state => state.DisableSubDisplay, (state, value) => state.DisableSubDisplay = value, "/ENABLED"),
            new("sp2x-windowborder",
                state => state.WindowModeIndex switch { 1 => "1", 2 => "2", _ => "" },
                (state, value) => state.WindowModeIndex = value switch { "1" => 1, "2" => 2, _ => 0 }),
            CreateBooleanOptionDescriptor("sdvxwsubborderless", state => state.SubBorderless, (state, value) => state.SubBorderless = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("s", state => state.ShowCursorTouchSim, (state, value) => state.ShowCursorTouchSim = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("sp2x-windowalwaysontop", state => state.WindowTopMost, (state, value) => state.WindowTopMost = value, "/ENABLED"),
            CreateStringOptionDescriptor("sp2x-windowsize", state => state.WindowSize, (state, value) => state.WindowSize = value),
            CreateBooleanOptionDescriptor("graphics-force-single-adapter", state => state.SingleAdapter, (state, value) => state.SingleAdapter = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("sp2x-nvprofile", state => state.NvidiaPerformanceProfile, (state, value) => state.NvidiaPerformanceProfile = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("sdvxwsubtop", state => state.SubWindowTopMost, (state, value) => state.SubWindowTopMost = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("sp2x-sdvxsubredraw", state => state.SubForceRender, (state, value) => state.SubForceRender = value, "/ENABLED"),
            new("sp2x-sdvxasio",
                state => state.SelectedAsioDriver?.DriverName ?? state.ConfiguredAsioDriverName ?? "",
                (state, value) => state.ConfiguredAsioDriverName = value ?? ""),
            CreateBooleanOptionDescriptor("sdvxasio2ch", state => state.Asio2Ch, (state, value) => state.Asio2Ch = value, "/ENABLED"),
            new("volumeboost",
                state => state.VolumeBoostIndex switch
                {
                    1 => "3",
                    2 => "6",
                    3 => "9",
                    4 => "12",
                    5 => "15",
                    6 => "20",
                    7 => "25",
                    8 => "30",
                    _ => ""
                },
                (state, value) => state.VolumeBoostIndex = value switch
                {
                    "3" => 1,
                    "6" => 2,
                    "9" => 3,
                    "12" => 4,
                    "15" => 5,
                    "20" => 6,
                    "25" => 7,
                    "30" => 8,
                    _ => 0
                }),
            new("resample",
                state => state.ResampleIndex switch
                {
                    1 => "44100",
                    2 => "48000",
                    3 => "88200",
                    4 => "96000",
                    5 => "176400",
                    6 => "192000",
                    _ => ""
                },
                (state, value) => state.ResampleIndex = value switch
                {
                    "44100" => 1,
                    "48000" => 2,
                    "88200" => 3,
                    "96000" => 4,
                    "176400" => 5,
                    "192000" => 6,
                    _ => 0
                }),
            CreateBooleanOptionDescriptor("wasapishared", state => state.WasapiShared, (state, value) => state.WasapiShared = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("sp2x-lowlatencysharedaudio", state => state.LowLatencySharedAudio, (state, value) => state.LowLatencySharedAudio = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("cardio", state => state.CardIo, (state, value) => state.CardIo = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("scard", state => state.HidSmartCard, (state, value) => state.HidSmartCard = value, "/ENABLED"),
            CreateBooleanOptionDescriptor("netdump", state => state.NetDump, (state, value) => state.NetDump = value, "/ENABLED"),
        ];

        private static readonly SpiceOptionDescriptor[] ExtraSpiceOptions =
        [
            new("network",
                state => state.NetworkAdapterIp ?? "",
                (state, value) => state.NetworkAdapterIp = NormalizeNetworkValue(value)),
            new("subnet",
                state => state.NetworkAdapterSubnet ?? "",
                (state, value) => state.NetworkAdapterSubnet = NormalizeNetworkValue(value)),
            CreateStringOptionDescriptor("url", state => state.ServerAddress, (state, value) => state.ServerAddress = value),
            CreateStringOptionDescriptor("p", state => state.PcbId, (state, value) => state.PcbId = value),
        ];

        private static readonly SpiceOptionDescriptor[] SpiceOptions =
            [.. GeneralSpiceOptions, .. ExtraSpiceOptions];

        private sealed class DeferredSettingsResult
        {
            public required Dictionary<string, string> SpiceOptionValues { get; init; }

            public required List<AsioDriverOption> AsioDrivers { get; init; }

            public required AsioDriverOption SelectedAsioDriver { get; init; }

            public required List<NetworkAdapterOption> NetworkAdapters { get; init; }

            public required NetworkAdapterOption SelectedNetworkAdapter { get; init; }
        }

        private void InitializeSettingsServices(
            SpiceXmlConfigEditor spiceXmlConfigEditor,
            GpuCompatLayerConfigurator gpuCompatLayerConfigurator,
            WindowsAppCompatLayerService appCompatLayerService,
            WindowsStartupService windowsStartupService)
        {
            _spiceXmlConfigEditor = spiceXmlConfigEditor ?? throw new ArgumentNullException(nameof(spiceXmlConfigEditor));
            _gpuCompatLayerConfigurator = gpuCompatLayerConfigurator ?? throw new ArgumentNullException(nameof(gpuCompatLayerConfigurator));
            _appCompatLayerService = appCompatLayerService ?? throw new ArgumentNullException(nameof(appCompatLayerService));
            _windowsStartupService = windowsStartupService ?? throw new ArgumentNullException(nameof(windowsStartupService));
        }
        private Task LoadSettingsStateAsync(SettingsState settings)
        {
            _logger.LogInformation("Settings startup initialization started.");
            settings.NoAsphyxia = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, "noasphyxia", false);
            settings.AutoLaunch = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, AutoLaunchConfigKey, false);
            settings.StartWithWindows = _windowsStartupService.IsEnabled(_paths.GetLauncherExecutablePath());
            settings.DisableSpiceFso = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, DisableFsoConfigKey, false);
            settings.UseSystemSpiceConfig = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, UseSystemConfigKey, false);
            settings.GpuCompatLayerRenderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(_appConfig.ReadString(AppConfigBootstrapper.SettingSectionName, "cl-rendermode", "dx9on12"));
            settings.IsSpiceConfigAvailable = IsSpiceConfigAvailable(settings.UseSystemSpiceConfig);
            settings.SpiceConfigEmptyStateMessage = MissingSpiceConfigMessage;
            RefreshGpuCompatLayerState(settings);

            LoadServerPresets(settings);
            _logger.LogInformation("Settings startup initialization completed. SpiceConfigAvailable={SpiceConfigAvailable}", settings.IsSpiceConfigAvailable);
            return Task.CompletedTask;
        }

        private Task SetStartWithWindowsAsync(SettingsState settings, bool requestedValue)
        {
            ArgumentNullException.ThrowIfNull(settings);

            bool previousValue = settings.StartWithWindows;
            string executablePath = _paths.GetLauncherExecutablePath();
            if (_windowsStartupService.TrySetEnabled(executablePath, requestedValue, out var error))
            {
                settings.StartWithWindows = requestedValue;
                _logger.LogInformation("Windows startup setting persisted. Enabled={Enabled}", requestedValue);
                return Task.CompletedTask;
            }

            settings.StartWithWindows = previousValue;
            _logger.LogWarning(
                "Windows startup setting failed. Requested={Requested}, Error={Error}",
                requestedValue,
                error);
            ShowErrorToast(
                "开机自启动设置失败",
                string.IsNullOrWhiteSpace(error) ? "无法更新 Windows 计划任务。" : error);
            return Task.CompletedTask;
        }

        private async Task LoadDeferredSettingsStateAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Deferred settings warm-up started.");

            if (!RefreshSpiceConfigAvailability(settings))
            {
                settings.AsioDrivers.Clear();
                settings.NetworkAdapters.Clear();
                _logger.LogWarning("Deferred settings warm-up skipped because the active spice config is unavailable.");
                return;
            }

            var currentConfiguredAsioDriverName = settings.ConfiguredAsioDriverName;
            var currentNetworkIp = settings.NetworkAdapterIp;
            var currentNetworkSubnet = settings.NetworkAdapterSubnet;

            var deferredState = await Task.Run(() =>
            {
                string spiceXmlPath = _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig);
                var optionValues = ReadSpiceOptionValues(spiceXmlPath);

                var asioDriverValue = optionValues.TryGetValue("sp2x-sdvxasio", out var asioVal) ? asioVal : string.Empty;
                var asioDrivers = BuildAsioDriverOptions(asioDriverValue);
                var selectedAsioDriver = asioDrivers.FirstOrDefault(choice =>
                                             string.Equals(choice.DriverName, asioDriverValue, StringComparison.OrdinalIgnoreCase))
                                         ?? asioDrivers.FirstOrDefault()
                                         ?? new AsioDriverOption("无", string.Empty);

                var networkIp = optionValues.TryGetValue("network", out var netIp) ? netIp : string.Empty;
                var networkSubnet = optionValues.TryGetValue("subnet", out var netSub) ? netSub : string.Empty;
                var networkAdapters = BuildNetworkAdapterOptions(networkIp, networkSubnet);
                var selectedNetworkAdapter = networkAdapters.FirstOrDefault(choice =>
                                                   string.Equals(choice.IpAddress, networkIp, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(choice.SubnetMask, networkSubnet, StringComparison.OrdinalIgnoreCase))
                                               ?? networkAdapters.FirstOrDefault()
                                               ?? new NetworkAdapterOption("无", string.Empty, string.Empty);

                return new DeferredSettingsResult
                {
                    SpiceOptionValues = optionValues,
                    AsioDrivers = asioDrivers,
                    SelectedAsioDriver = selectedAsioDriver,
                    NetworkAdapters = networkAdapters,
                    SelectedNetworkAdapter = selectedNetworkAdapter
                };
            });

            ApplyDeferredSettingsResult(settings, deferredState, currentConfiguredAsioDriverName, currentNetworkIp, currentNetworkSubnet);
            _logger.LogInformation(
                "Deferred settings warm-up completed. AsioDriverCount={AsioDriverCount}, NetworkAdapterCount={NetworkAdapterCount}",
                settings.AsioDrivers.Count,
                settings.NetworkAdapters.Count);
        }

        private Task PersistLauncherSettingsAsync(SettingsState settings)
        {
            try
            {
                _appConfig.WriteString(AppConfigBootstrapper.SettingSectionName, "noasphyxia", settings.NoAsphyxia.ToString().ToLowerInvariant());
                _appConfig.WriteString(AppConfigBootstrapper.SettingSectionName, AutoLaunchConfigKey, settings.AutoLaunch.ToString().ToLowerInvariant());
                _logger.LogInformation("Launcher settings persisted.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist launcher settings.");
                ShowErrorToast("保存设置失败", ex.Message);
                settings.NoAsphyxia = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, "noasphyxia", false);
                settings.AutoLaunch = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, AutoLaunchConfigKey, false);
            }

            return Task.CompletedTask;
        }

        private Task PersistServerEndpointAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Server endpoint persistence started.");

            settings.ServerAddress = (settings.ServerAddress ?? string.Empty).Trim();
            settings.PcbId = (settings.PcbId ?? string.Empty).Trim();

            if (!TryApplySpiceUpdates(
                    _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig),
                    settings,
                    reloadSettingsOnSuccess: false,
                    new SpiceOptionUpdate("url", settings.ServerAddress, false),
                    new SpiceOptionUpdate("p", settings.PcbId, false)))
            {
                ReloadRuntimeState(settings);
                return Task.CompletedTask;
            }

            SyncSelectedServerPresetFromCurrentFields(settings);
            SaveServerPresets(settings);
            _logger.LogInformation("Server endpoint persistence completed.");
            return Task.CompletedTask;
        }

        private IReadOnlyList<NetworkAdapterOption> GetNetworkAdapterChoices(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return BuildNetworkAdapterOptions(settings.NetworkAdapterIp, settings.NetworkAdapterSubnet);
        }

        private Task PersistNetworkSettingsAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Network settings persistence started.");

            settings.NetworkAdapterIp = NormalizeNetworkValue(settings.NetworkAdapterIp);
            settings.NetworkAdapterSubnet = NormalizeNetworkValue(settings.NetworkAdapterSubnet);

            if (!TryApplySpiceUpdates(
                    _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig),
                    settings,
                    false,
                    new SpiceOptionUpdate("network", settings.NetworkAdapterIp, false),
                    new SpiceOptionUpdate("subnet", settings.NetworkAdapterSubnet, false)))
            {
                ReloadRuntimeState(settings);
                return Task.CompletedTask;
            }

            SyncSelectedNetworkAdapter(settings, settings.NetworkAdapterIp, settings.NetworkAdapterSubnet);
            _logger.LogInformation("Network settings persistence completed.");
            return Task.CompletedTask;
        }

        private Task PersistFsoToggleAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("FSO toggle persistence started.");

            try
            {
                _appConfig.WriteString(
                    AppConfigBootstrapper.SettingSectionName,
                    DisableFsoConfigKey,
                    settings.DisableSpiceFso.ToString().ToLowerInvariant());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist FSO setting.");
                ShowErrorToast("保存设置失败", ex.Message);
                settings.DisableSpiceFso = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, DisableFsoConfigKey, false);
                return Task.CompletedTask;
            }

            string spicePath = _paths.GetSpicePath();
            if (!File.Exists(spicePath))
            {
                _logger.LogWarning("FSO registry update skipped because spice64.exe was not found: {SpicePath}", spicePath);
                ShowWarningToast("FSO 设置已保存", $"未找到 spice64.exe，启动游戏前会再次尝试应用：{spicePath}");
                return Task.CompletedTask;
            }

            if (_appCompatLayerService.TrySetFsoDisabled(spicePath, settings.DisableSpiceFso, out var error))
            {
                _logger.LogInformation("FSO registry setting applied. Disabled={Disabled}", settings.DisableSpiceFso);
                return Task.CompletedTask;
            }

            _logger.LogWarning("FSO registry setting failed: {Error}", error);
            ShowErrorToast("FSO 设置失败", string.IsNullOrWhiteSpace(error) ? "未知错误" : error);
            bool actualDisabled = _appCompatLayerService.IsFsoDisabled(spicePath);
            settings.DisableSpiceFso = actualDisabled;
            try
            {
                _appConfig.WriteString(AppConfigBootstrapper.SettingSectionName, DisableFsoConfigKey, actualDisabled.ToString().ToLowerInvariant());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore FSO setting after registry failure.");
            }

            return Task.CompletedTask;
        }

        private Task PersistSpiceSettingsAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Spice settings persistence started.");

            if (!TryApplySpiceUpdates(
                    _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig),
                    settings,
                    false,
                    BuildSpiceOptionUpdates(settings).ToArray()))
            {
                ReloadRuntimeState(settings);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Spice settings persistence completed.");
            return Task.CompletedTask;
        }

        private Task PersistGpuCompatLayerToggleAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("GPU compatibility layer toggle persistence started.");

            if (settings.GpuCompatLayerEnabled && !GetGpuCompatLayerRuntimeState().IsFullyApplied)
            {
                return ConfirmAndEnableGpuCompatLayerAsync(settings);
            }

            var renderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(settings.GpuCompatLayerRenderMode);
            string spiceXmlPath = _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig);
            if (_gpuCompatLayerConfigurator.TryToggleGpuCompatLayer(
                    settings.GpuCompatLayerEnabled,
                    renderMode,
                    spiceXmlPath,
                    out var error))
            {
                settings.GpuCompatLayerRenderMode = renderMode;
                RefreshGpuCompatLayerState(settings);
                _logger.LogInformation("GPU compatibility layer toggle persistence completed.");
                return Task.CompletedTask;
            }

            _logger.LogWarning("GPU compatibility layer toggle persistence failed.");
            ShowErrorToast("兼容层切换失败", string.IsNullOrWhiteSpace(error) ? "未知错误" : error);
            RefreshGpuCompatLayerState(settings);
            return Task.CompletedTask;
        }

        private Task PersistGpuCompatLayerRenderModeAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("GPU compatibility layer render mode persistence started.");

            var renderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(settings.GpuCompatLayerRenderMode);

            string spiceXmlPath = _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig);
            if (_gpuCompatLayerConfigurator.TryPersistGpuCompatLayerRenderMode(
                    renderMode,
                    settings.GpuCompatLayerEnabled,
                    spiceXmlPath,
                    out var error))
            {
                settings.GpuCompatLayerRenderMode = renderMode;
                RefreshGpuCompatLayerState(settings);
                _logger.LogInformation("GPU compatibility layer render mode persistence completed.");
                return Task.CompletedTask;
            }

            _logger.LogWarning("GPU compatibility layer render mode persistence failed.");
            ShowErrorToast("兼容模式切换失败", string.IsNullOrWhiteSpace(error) ? "未知错误" : error);
            RefreshGpuCompatLayerState(settings);
            return Task.CompletedTask;
        }

        private async Task EditConfigAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("spicecfg editor launch requested.");

            if (_appConfig.IsReadOnlySession)
            {
                _logger.LogWarning("spicecfg editor launch skipped because config.toml is in a read-only session.");
                ShowWarningToast("配置文件无法保存", "config.toml 当前无法读取，本次会话的配置修改仅保存在内存中。");
                return;
            }

            string spicePath = _paths.GetSpicePath();

            if (!File.Exists(spicePath))
            {
                _logger.LogWarning("spicecfg editor launch failed because spice64.exe was not found: {SpicePath}", spicePath);
                ShowErrorToast("无法启动 spice 配置", $"未找到程序: {spicePath}");
                return;
            }

            if (!File.Exists(_paths.ConfigFilePath))
            {
                _logger.LogWarning("spicecfg editor launch failed because config.toml was not found: {ConfigPath}", _paths.ConfigFilePath);
                ShowErrorToast("无法启动 spice 配置", $"未找到配置文件: {_paths.ConfigFilePath}");
                return;
            }

            string arguments = Spice64CommandLine.BuildConfigEditorArguments(settings.UseSystemSpiceConfig);
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = spicePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(spicePath)
                });

                if (process == null)
                {
                    _logger.LogWarning("spicecfg editor process creation returned null.");
                    ShowErrorToast("无法启动 spice 配置", "创建进程失败。");
                    return;
                }

                _logger.LogInformation("spicecfg editor process started. ProcessId={ProcessId}", process.Id);
                await process.WaitForExitAsync();
                _logger.LogInformation("spicecfg editor process exited. ExitCode={ExitCode}", process.ExitCode);
                await LoadSettingsStateAsync(settings);
                await LoadDeferredSettingsStateAsync(settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "spicecfg editor launch failed.");
                ShowErrorToast("启动 spice 配置失败", ex.Message);
            }
        }

        private async Task SetUseSystemSpiceConfigAsync(SettingsState settings, bool requestedValue)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (requestedValue && !settings.UseSystemSpiceConfig)
            {
                _logger.LogInformation("Use system spice config confirmation dialog opened.");
                var confirmed = await ShowDialogAsync(
                    "切换为系统配置",
                    "开启后将失去下列功能：\n- 更新后自动应用 Patch\n- 与其他 BEMANI 游戏的配置隔离\n\n是否继续开启？",
                    "开启",
                    "取消",
                    NotificationType.Warning);

                if (!confirmed)
                {
                    _logger.LogInformation("Use system spice config enable was cancelled.");
                    settings.UseSystemSpiceConfig = false;
                    return;
                }
            }

            settings.UseSystemSpiceConfig = requestedValue;
            await PersistUseSystemSpiceConfigAsync(settings);
        }

        private Task PersistUseSystemSpiceConfigAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Spice config mode persistence started.");

            try
            {
                _appConfig.WriteString(
                    AppConfigBootstrapper.SettingSectionName,
                    UseSystemConfigKey,
                    settings.UseSystemSpiceConfig.ToString().ToLowerInvariant());
                ReloadRuntimeState(settings);
                _logger.LogInformation("Spice config mode persistence completed. SpiceConfigAvailable={SpiceConfigAvailable}", settings.IsSpiceConfigAvailable);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist use-system-config.");
                ShowErrorToast("保存设置失败", ex.Message);
                settings.UseSystemSpiceConfig = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, UseSystemConfigKey, false);
            }

            return Task.CompletedTask;
        }

        private async Task ConfirmAndEnableGpuCompatLayerAsync(SettingsState settings)
        {
            _logger.LogInformation("GPU compatibility layer confirmation dialog opened.");
            var confirmed = await ShowDialogAsync(
                "启用显卡兼容层",
                "即将启用显卡兼容层，请确认你的显卡为 AMD 或者 Intel ，否则请勿开启。\n你确定要继续吗？",
                "确认",
                "取消",
                NotificationType.Warning);

            if (!confirmed)
            {
                _logger.LogInformation("GPU compatibility layer enable was cancelled.");
                settings.GpuCompatLayerEnabled = false;
                return;
            }

            var renderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(settings.GpuCompatLayerRenderMode);
            string spiceXmlPath = _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig);
            if (_gpuCompatLayerConfigurator.TryToggleGpuCompatLayer(
                    true,
                    renderMode,
                    spiceXmlPath,
                    out var error))
            {
                settings.GpuCompatLayerRenderMode = renderMode;
                RefreshGpuCompatLayerState(settings);
                _logger.LogInformation("GPU compatibility layer enable completed after confirmation.");
                return;
            }

            _logger.LogWarning("GPU compatibility layer enable failed after confirmation.");
            ShowErrorToast("兼容层切换失败", string.IsNullOrWhiteSpace(error) ? "未知错误" : error);
            RefreshGpuCompatLayerState(settings);
        }

        private bool HasGpuCompatLayerModulesDirectory()
        {
            return Directory.Exists(Path.Combine(_paths.GetContentsDirectoryPath(), "modules"));
        }

        private Task OpenAsioControlPanelAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var driverName = settings.SelectedAsioDriver?.DriverName ?? settings.ConfiguredAsioDriverName;
            if (string.IsNullOrWhiteSpace(driverName))
            {
                _logger.LogInformation("ASIO control panel open skipped because no driver is selected.");
                return Task.CompletedTask;
            }

            _logger.LogInformation("ASIO control panel open requested.");
            if (!AsioDriverRegistry.TryOpenControlPanel(driverName, out var errorMessage))
            {
                _logger.LogWarning("ASIO control panel open failed.");
                ShowWarningToast(
                    "ASIO 控制面板",
                    string.IsNullOrWhiteSpace(errorMessage) ? "无法打开当前选择的 ASIO 驱动控制面板。" : errorMessage);
            }

            return Task.CompletedTask;
        }

        private async Task AddServerPresetAsync(SettingsState settings, string name, string serverUrl, string pcbId)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var presetName = (name ?? string.Empty).Trim();
            serverUrl = (serverUrl ?? string.Empty).Trim();
            pcbId = (pcbId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(presetName))
            {
                _logger.LogWarning("Add server preset rejected because the name is empty.");
                ShowErrorToast("新建预设失败", "预设名不能为空。");
                return;
            }

            if (settings.ServerPresets.Any(preset => string.Equals(preset.Name, presetName, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Add server preset rejected because the name already exists.");
                ShowErrorToast("新建预设失败", $"已存在同名预设：{presetName}");
                return;
            }

            var newPreset = new ServerPresetItem
            {
                Name = presetName,
                ServerUrl = serverUrl,
                PcbId = pcbId
            };

            settings.ServerPresets.Add(newPreset);
            settings.SelectedServerPreset = newPreset;
            await PersistSelectedServerPresetAsync(settings);
            _logger.LogInformation("Server preset added. PresetCount={PresetCount}", settings.ServerPresets.Count);
            ShowInfoToast("新建预设", $"已创建预设：{presetName}");
        }

        private string GetServerPresetDeletionError(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var preset = settings.SelectedServerPreset;
            if (preset == null)
            {
                return "请先选择要删除的预设。";
            }

            if (string.Equals(preset.Name, NonePresetName, StringComparison.OrdinalIgnoreCase))
            {
                return "「无」是默认项，不可删除。";
            }

            if (string.Equals(preset.Name, AsphyxiaPresetName, StringComparison.OrdinalIgnoreCase))
            {
                return "Asphyxia 是内置预设，不可删除。";
            }

            return string.Empty;
        }

        private async Task DeleteServerPresetAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var preset = settings.SelectedServerPreset;
            if (preset == null || !string.IsNullOrEmpty(GetServerPresetDeletionError(settings)))
            {
                return;
            }
            settings.ServerPresets.Remove(preset);
            var fallback = settings.ServerPresets.FirstOrDefault(item => string.Equals(item.Name, NonePresetName, StringComparison.OrdinalIgnoreCase))
                ?? settings.ServerPresets.FirstOrDefault();

            settings.SelectedServerPreset = fallback;
            await PersistSelectedServerPresetAsync(settings);
            _logger.LogInformation("Server preset deleted. PresetCount={PresetCount}", settings.ServerPresets.Count);
            ShowInfoToast("删除预设", $"已删除预设：{preset.Name}");
        }

        private async Task PersistSelectedServerPresetAsync(SettingsState settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _logger.LogInformation("Selected server preset persistence started.");

            var preset = settings.SelectedServerPreset;
            if (preset == null)
            {
                _logger.LogWarning("Selected server preset persistence skipped because no preset is selected.");
                return;
            }

            settings.ActiveServerPreset = preset.Name ?? NonePresetName;
            if (string.Equals(preset.Name, NonePresetName, StringComparison.OrdinalIgnoreCase))
            {
                settings.ServerAddress = string.Empty;
                settings.PcbId = string.Empty;
            }
            else
            {
                settings.ServerAddress = (preset.ServerUrl ?? string.Empty).Trim();
                settings.PcbId = (preset.PcbId ?? string.Empty).Trim();
            }

            await PersistServerEndpointAsync(settings);
            _logger.LogInformation("Selected server preset persistence completed.");
        }

        private void LoadServerPresets(SettingsState settings)
        {
            var result = _appConfig.LoadServerPresets(NonePresetName, AsphyxiaPresetName, AsphyxiaDefaultUrl);
            if (result.Mutated)
            {
                _appConfig.SaveServerPresets(result.Presets, result.ActivePreset, NonePresetName);
            }

            settings.ServerPresets.Clear();
            foreach (var preset in result.Presets)
            {
                settings.ServerPresets.Add(preset);
            }

            var selectedPreset = settings.ServerPresets.FirstOrDefault(p => string.Equals(p.Name, result.ActivePreset, StringComparison.OrdinalIgnoreCase))
                ?? settings.ServerPresets.FirstOrDefault(p => string.Equals(p.Name, NonePresetName, StringComparison.OrdinalIgnoreCase))
                ?? settings.ServerPresets.FirstOrDefault();

            settings.SelectedServerPreset = selectedPreset;
            settings.ActiveServerPreset = selectedPreset?.Name ?? NonePresetName;
            _logger.LogInformation("Server presets loaded. PresetCount={PresetCount}", settings.ServerPresets.Count);
        }

        private void LoadSpiceSettings(SettingsState settings)
        {
            if (!settings.IsSpiceConfigAvailable)
            {
                _logger.LogWarning("Spice settings load skipped because the active spice config is unavailable.");
                return;
            }

            string spiceXmlPath = _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig);
            var optionValues = ReadSpiceOptionValues(spiceXmlPath);
            ApplySpiceOptionValues(settings, optionValues);
            SyncSelectedServerPresetFromCurrentFields(settings);
            _logger.LogInformation("Spice settings loaded from active config.");
        }

        private void ReloadRuntimeState(SettingsState settings)
        {
            _logger.LogInformation("Settings runtime state reload started.");
            if (!RefreshSpiceConfigAvailability(settings))
            {
                ApplyUnavailableSpiceState(settings);
                _logger.LogWarning("Settings runtime state reload ended with unavailable spice config.");
                return;
            }

            RefreshGpuCompatLayerState(settings);
            LoadSpiceSettings(settings);
            RefreshAsioDrivers(settings, settings.ConfiguredAsioDriverName);
            RefreshNetworkAdapters(settings, settings.NetworkAdapterIp, settings.NetworkAdapterSubnet);
            _logger.LogInformation("Settings runtime state reload completed.");
        }

        private void RefreshGpuCompatLayerState(SettingsState settings)
        {
            var runtimeState = GetGpuCompatLayerRuntimeState();
            var configuredRenderMode = SyncGpuCompatLayerConfigToRuntimeState(runtimeState);

            settings.GpuCompatLayerRenderMode = string.IsNullOrWhiteSpace(runtimeState.DetectedRenderMode)
                ? configuredRenderMode
                : runtimeState.DetectedRenderMode;
            settings.GpuCompatLayerEnabled = runtimeState.IsFullyApplied;
            _logger.LogDebug("GPU compatibility layer runtime state refreshed. FullyApplied={FullyApplied}, InconsistentFiles={InconsistentFiles}", runtimeState.IsFullyApplied, runtimeState.HasInconsistentFiles);
        }

        private void ApplyUnavailableSpiceState(SettingsState settings)
        {
            settings.AsioDrivers.Clear();
            settings.NetworkAdapters.Clear();
            var emptyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ApplySpiceOptionValues(settings, emptyValues);
            settings.SelectedAsioDriver = null;
            settings.SelectedNetworkAdapter = null;
            SyncSelectedServerPresetFromCurrentFields(settings);
        }

        private void SyncSelectedServerPresetFromCurrentFields(SettingsState settings)
        {
            var serverUrl = (settings.ServerAddress ?? string.Empty).Trim();
            var pcbId = (settings.PcbId ?? string.Empty).Trim();

            var matchedPreset = settings.ServerPresets.FirstOrDefault(preset =>
                !string.Equals(preset.Name, NonePresetName, StringComparison.OrdinalIgnoreCase)
                && string.Equals((preset.ServerUrl ?? string.Empty).Trim(), serverUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals((preset.PcbId ?? string.Empty).Trim(), pcbId, StringComparison.OrdinalIgnoreCase));

            var fallbackPreset = settings.ServerPresets.FirstOrDefault(preset => string.Equals(preset.Name, NonePresetName, StringComparison.OrdinalIgnoreCase))
                ?? settings.ServerPresets.FirstOrDefault();
            var selectedPreset = matchedPreset ?? fallbackPreset;

            settings.SelectedServerPreset = selectedPreset;
            settings.ActiveServerPreset = selectedPreset?.Name ?? NonePresetName;
        }

        private void RefreshAsioDrivers(SettingsState settings, string selectedDriverName)
        {
            var choices = BuildAsioDriverOptions(selectedDriverName);
            settings.AsioDrivers.Clear();
            foreach (var choice in choices)
            {
                settings.AsioDrivers.Add(choice);
            }

            var selectedOption = settings.AsioDrivers.FirstOrDefault(choice => string.Equals(choice.DriverName, selectedDriverName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? settings.AsioDrivers.FirstOrDefault();
            settings.SelectedAsioDriver = selectedOption;
        }

        private void RefreshNetworkAdapters(SettingsState settings, string selectedIpAddress, string selectedSubnetMask)
        {
            var choices = BuildNetworkAdapterOptions(selectedIpAddress, selectedSubnetMask);
            settings.NetworkAdapters.Clear();
            foreach (var choice in choices)
            {
                settings.NetworkAdapters.Add(choice);
            }

            var selectedOption = settings.NetworkAdapters.FirstOrDefault(choice =>
                    string.Equals(choice.IpAddress, selectedIpAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(choice.SubnetMask, selectedSubnetMask ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? settings.NetworkAdapters.FirstOrDefault();
            settings.SelectedNetworkAdapter = selectedOption;
        }

        private void SyncSelectedNetworkAdapter(SettingsState settings, string selectedIpAddress, string selectedSubnetMask)
        {
            var selectedOption = settings.NetworkAdapters.FirstOrDefault(choice =>
                    string.Equals(choice.IpAddress, selectedIpAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(choice.SubnetMask, selectedSubnetMask ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            if (selectedOption == null
                && string.IsNullOrWhiteSpace(selectedIpAddress)
                && string.IsNullOrWhiteSpace(selectedSubnetMask))
            {
                selectedOption = settings.NetworkAdapters.FirstOrDefault(choice =>
                    string.IsNullOrWhiteSpace(choice.IpAddress)
                    && string.IsNullOrWhiteSpace(choice.SubnetMask));
            }

            settings.SelectedNetworkAdapter = selectedOption;
        }

        private Dictionary<string, string> ReadSpiceOptionValues(string spiceXmlPath)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!_spiceXmlConfigEditor.TryLoadOptionsContext(
                    spiceXmlPath, LoadOptions.PreserveWhitespace, false, out var context, out _, out _))
            {
                _logger.LogWarning("Failed to load active spice config options.");
                return values;
            }

            foreach (var option in SpiceOptions)
            {
                values[option.XmlName] = context.GetOptionValue(option.XmlName) ?? string.Empty;
            }

            return values;
        }

        private void ApplySpiceOptionValues(SettingsState settings, Dictionary<string, string> values)
        {
            foreach (var option in SpiceOptions)
            {
                if (values.TryGetValue(option.XmlName, out var xmlValue))
                {
                    option.ApplyXmlValue(settings, xmlValue);
                }
                else
                {
                    option.ApplyXmlValue(settings, string.Empty);
                }
            }
        }

        private void ApplyDeferredSettingsResult(
            SettingsState settings,
            DeferredSettingsResult deferredState,
            string currentConfiguredAsioDriverName,
            string currentNetworkIp,
            string currentNetworkSubnet)
        {
            ApplySpiceOptionValues(settings, deferredState.SpiceOptionValues);

            settings.AsioDrivers.Clear();
            foreach (var option in deferredState.AsioDrivers)
            {
                settings.AsioDrivers.Add(option);
            }

            settings.NetworkAdapters.Clear();
            foreach (var option in deferredState.NetworkAdapters)
            {
                settings.NetworkAdapters.Add(option);
            }

            settings.SelectedAsioDriver = deferredState.SelectedAsioDriver;
            settings.SelectedNetworkAdapter = deferredState.SelectedNetworkAdapter;
            if (!string.IsNullOrWhiteSpace(currentConfiguredAsioDriverName))
            {
                settings.ConfiguredAsioDriverName = currentConfiguredAsioDriverName;
            }

            if (!string.IsNullOrWhiteSpace(currentNetworkIp) || !string.IsNullOrWhiteSpace(currentNetworkSubnet))
            {
                settings.NetworkAdapterIp = currentNetworkIp;
                settings.NetworkAdapterSubnet = currentNetworkSubnet;
            }

            SyncSelectedServerPresetFromCurrentFields(settings);
        }

        private static List<AsioDriverOption> BuildAsioDriverOptions(string selectedDriverName)
        {
            var choices = new List<AsioDriverOption> { new("无", string.Empty) };
            foreach (var driverName in AsioDriverRegistry.GetInstalledDriverNames())
            {
                choices.Add(new AsioDriverOption(driverName, driverName));
            }

            if (!string.IsNullOrWhiteSpace(selectedDriverName)
                && choices.All(choice => !string.Equals(choice.DriverName, selectedDriverName, StringComparison.OrdinalIgnoreCase)))
            {
                choices.Add(new AsioDriverOption($"{selectedDriverName}（当前配置）", selectedDriverName));
            }

            return choices;
        }

        private static List<NetworkAdapterOption> BuildNetworkAdapterOptions(string selectedIpAddress, string selectedSubnetMask)
        {
            var choices = new List<NetworkAdapterOption> { new("无", string.Empty, string.Empty) };
            foreach (var adapter in NetworkAdapterDiscovery.GetAvailableAdapters())
            {
                choices.Add(new NetworkAdapterOption(adapter.DisplayName, adapter.IpAddress, adapter.SubnetMask));
            }

            if ((!string.IsNullOrWhiteSpace(selectedIpAddress) || !string.IsNullOrWhiteSpace(selectedSubnetMask))
                && choices.All(choice =>
                    !string.Equals(choice.IpAddress, selectedIpAddress, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(choice.SubnetMask, selectedSubnetMask, StringComparison.OrdinalIgnoreCase)))
            {
                choices.Add(new NetworkAdapterOption($"{selectedIpAddress} / {selectedSubnetMask}（当前配置）".Trim(), selectedIpAddress, selectedSubnetMask));
            }

            return choices;
        }

        private GpuCompatLayerRuntimeState GetGpuCompatLayerRuntimeState()
        {
            try
            {
                return GpuCompatLayerConfigurator.DetectRuntimeState(
                    _paths.GetContentsDirectoryPath(),
                    _paths.GetBundledLibsDirectoryPath());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to detect compatibility layer runtime state.");
                return new GpuCompatLayerRuntimeState(false, string.Empty, false);
            }
        }


        private bool RefreshSpiceConfigAvailability(SettingsState settings)
        {
            bool isSpiceConfigAvailable = IsSpiceConfigAvailable(settings.UseSystemSpiceConfig);
            settings.IsSpiceConfigAvailable = isSpiceConfigAvailable;
            settings.SpiceConfigEmptyStateMessage = MissingSpiceConfigMessage;

            if (!isSpiceConfigAvailable)
            {
                _logger.LogWarning("Active spice config is unavailable.");
            }

            return isSpiceConfigAvailable;
        }

        private bool IsSpiceConfigAvailable(bool useSystemSpiceConfig)
        {
            if (!useSystemSpiceConfig)
            {
                return true;
            }

            string spiceXmlPath = _paths.ResolveSpiceXmlPath(useSystemSpiceConfig);

            try
            {
                return _spiceXmlConfigEditor.TryLoadOptionsContext(
                    spiceXmlPath,
                    LoadOptions.None,
                    false,
                    out _,
                    out _,
                    out _);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "System spice config validation failed for {SpiceXmlPath}.", spiceXmlPath);
                return false;
            }
        }

        private string SyncGpuCompatLayerConfigToRuntimeState(GpuCompatLayerRuntimeState runtimeState)
        {
            var configuredRenderMode = GpuCompatLayerConfigurator.NormalizeRenderMode(
                _appConfig.ReadString(AppConfigBootstrapper.SettingSectionName, "cl-rendermode", "dx9on12"));
            var detectedRenderMode = string.IsNullOrWhiteSpace(runtimeState.DetectedRenderMode)
                ? string.Empty
                : GpuCompatLayerConfigurator.NormalizeRenderMode(runtimeState.DetectedRenderMode);
            var targetCompatEnabled = runtimeState.IsFullyApplied;

            try
            {
                var currentCompatEnabled = _appConfig.ReadBool(AppConfigBootstrapper.SettingSectionName, "compatlayer", false);
                if (currentCompatEnabled != targetCompatEnabled)
                {
                    _appConfig.WriteString(
                        AppConfigBootstrapper.SettingSectionName,
                        "compatlayer",
                        targetCompatEnabled ? "true" : "false");
                }

                if (!string.IsNullOrWhiteSpace(detectedRenderMode)
                    && !string.Equals(configuredRenderMode, detectedRenderMode, StringComparison.OrdinalIgnoreCase))
                {
                    _appConfig.WriteString(AppConfigBootstrapper.SettingSectionName, "cl-rendermode", detectedRenderMode);
                    configuredRenderMode = detectedRenderMode;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync compatibility runtime state back to config.toml.");
            }

            return configuredRenderMode;
        }

        private void SaveServerPresets(SettingsState settings)
        {
            _appConfig.SaveServerPresets(settings.ServerPresets, settings.ActiveServerPreset, NonePresetName);
        }

        private bool TryApplySpiceUpdates(
            string spiceXmlPath,
            SettingsState settings,
            bool reloadSettingsOnSuccess = true,
            params SpiceOptionUpdate[] updates)
        {
            int updateCount = updates?.Length ?? 0;
            _logger.LogDebug("Applying spice option updates. UpdateCount={UpdateCount}", updateCount);
            if (!_spiceXmlConfigEditor.ApplySpiceOptions(spiceXmlPath, updates, out var error))
            {
                _logger.LogWarning("Failed to apply spice option updates. UpdateCount={UpdateCount}", updateCount);
                ShowErrorToast("写入配置失败", error);
                return false;
            }

            if (reloadSettingsOnSuccess
                && string.Equals(
                    spiceXmlPath,
                    _paths.ResolveSpiceXmlPath(settings.UseSystemSpiceConfig),
                    StringComparison.OrdinalIgnoreCase))
            {
                LoadSpiceSettings(settings);
            }

            _logger.LogInformation("Spice option updates applied. UpdateCount={UpdateCount}", updateCount);
            return true;
        }

        private static IEnumerable<SpiceOptionUpdate> BuildSpiceOptionUpdates(SettingsState settings)
        {
            foreach (var option in GeneralSpiceOptions)
            {
                yield return new SpiceOptionUpdate(option.XmlName, option.GetXmlValue(settings), false);
            }
        }

    }
}
