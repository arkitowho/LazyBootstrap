using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Services;
using static LazyBootstrap.Controls.ControlHelpers;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private readonly DisplayConfigurationState _displayState = new DisplayConfigurationState();

        private DisplayConfigurationRequest BuildDisplayConfigurationRequest() => new DisplayConfigurationRequest(
            _displayState.IsDisplayConfigurationEnabled,
            _displayState.IsDualDisplay,
            _displayState.ExitRestore,
            _displayState.SelectedMainDisplay,
            _displayState.SelectedSubDisplay,
            _displayState.SelectedMainRotation,
            _displayState.SelectedSubRotation,
            _displayState.SelectedMainResolution,
            _displayState.SelectedSubResolution,
            _displayState.SelectedMainRefreshRate,
            _displayState.SelectedSubRefreshRate);

        private DispatcherTimer _displayPulseTimer;
        private double _displayPulsePhase = 0d;
        private bool _isUpdatingDisplayLayoutUi;
        private bool _isDisplayLayoutInitialized;
        private bool _isLoadingDisplaySettings;

        /// <summary>Warms up the deferred display configuration options (invoked during the startup sequence).</summary>
        private Task WarmDisplayDeferredAsync()
            => WarmDisplayStateAsync(_displayState);

        private void InitializeExitRestoreBinding()
        {
            if (ExitRestoreToggleSwitch != null)
            {
                ExitRestoreToggleSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_isLoadingDisplaySettings) return;
                    bool enabled = ExitRestoreToggleSwitch.IsChecked == true;
                    _displayState.ExitRestore = enabled;
                    await PersistGeneralSettingsAsync(_displayState);
                };
            }
        }

        private void OnSelectMainDisplayClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _displayState.SelectMainDisplay();
            ApplyDisplayStateToUi();
        }

        private void OnSelectSubDisplayClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _displayState.SelectSubDisplay();
            ApplyDisplayStateToUi();
        }

        private async void OnOpenTouchPanelClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await OpenTouchPanelAsync();
        }

        private async void OnPreviewDisplaySettingsClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await PreviewDisplaySettingsAsync(_displayState);
            ApplyDisplayStateToUi();
        }

        private async Task HandleDisplayConfigurationChangedAsync(bool refreshMainOptions, bool refreshSubOptions)
        {
            await HandleConfigurationChangedAsync(_displayState, refreshMainOptions, refreshSubOptions);
            ApplyDisplayStateToUi();
        }

        private void InitializeDisplayLayoutControls()
        {
            if (!_isDisplayLayoutInitialized)
            {
                if (MainScreenComboBox != null)
                {
                    MainScreenComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(() =>
                        {
                            SyncMainSelectionFromUi();
                            _displayState.SelectedMainResolution = string.Empty;
                            _displayState.SelectedMainRefreshRate = string.Empty;
                        }, refreshMainOptions: true, refreshSubOptions: false);
                    };
                }

                if (SubScreenComboBox != null)
                {
                    SubScreenComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(() =>
                        {
                            SyncSubSelectionFromUi();
                            _displayState.SelectedSubResolution = string.Empty;
                            _displayState.SelectedSubRefreshRate = string.Empty;
                        }, refreshMainOptions: false, refreshSubOptions: true);
                    };
                }

                if (RotationComboBox != null)
                {
                    RotationComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncMainSelectionFromUi,
                            refreshMainOptions: true,
                            refreshSubOptions: false);
                    };
                }

                if (SubRotationComboBox != null)
                {
                    SubRotationComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncSubSelectionFromUi,
                            refreshMainOptions: false,
                            refreshSubOptions: true);
                    };
                }

                if (MainResolutionComboBox != null)
                {
                    MainResolutionComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncMainSelectionFromUi,
                            refreshMainOptions: true,
                            refreshSubOptions: false);
                    };
                }

                if (SubResolutionComboBox != null)
                {
                    SubResolutionComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncSubSelectionFromUi,
                            refreshMainOptions: false,
                            refreshSubOptions: true);
                    };
                }

                if (MainRefreshRateComboBox != null)
                {
                    MainRefreshRateComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncMainSelectionFromUi,
                            refreshMainOptions: false,
                            refreshSubOptions: false);
                    };
                }

                if (SubRefreshRateComboBox != null)
                {
                    SubRefreshRateComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplaySelectionChangedAsync(
                            SyncSubSelectionFromUi,
                            refreshMainOptions: false,
                            refreshSubOptions: false);
                    };
                }

                if (DisplayConfigEnabledToggleSwitch != null)
                {
                    DisplayConfigEnabledToggleSwitch.IsCheckedChanged += async (s, e) =>
                    {
                        await HandleDisplayGeneralSettingChangedAsync(() =>
                            _displayState.IsDisplayConfigurationEnabled = DisplayConfigEnabledToggleSwitch.IsChecked == true);
                    };
                }

                if (DisplayModeComboBox != null)
                {
                    DisplayModeComboBox.SelectionChanged += async (s, e) =>
                    {
                        await HandleDisplayGeneralSettingChangedAsync(() =>
                        {
                            bool isDualDisplay = DisplayModeComboBox.SelectedIndex == 1;
                            _displayState.IsDualDisplay = isDualDisplay;

                            if (!isDualDisplay && _displayState.SelectedTarget == DisplaySelectionTarget.Sub)
                            {
                                _displayState.SelectedTarget = DisplaySelectionTarget.None;
                                _displayState.ShowNoScreenSelected = true;
                                _displayState.ShowMainScreenConfig = false;
                                _displayState.ShowSubScreenConfig = false;
                            }
                        });
                    };
                }

                StartDisplayPulseAnimation();
                _isDisplayLayoutInitialized = true;
            }

            ApplyDisplayStateToUi();
        }

        private bool ShouldSkipDisplayLayoutInteraction()
        {
            return _isLoadingDisplaySettings || _isUpdatingDisplayLayoutUi;
        }

        private async Task HandleDisplaySelectionChangedAsync(Action updateState, bool refreshMainOptions, bool refreshSubOptions)
        {
            if (ShouldSkipDisplayLayoutInteraction())
            {
                return;
            }

            try
            {
                updateState?.Invoke();
                await HandleDisplayConfigurationChangedAsync(refreshMainOptions, refreshSubOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display configuration selection failed.");
                ShowErrorToast("显示器配置失败", ex.Message);
                ApplyDisplayStateToUi();
            }
        }

        private async Task HandleDisplayGeneralSettingChangedAsync(Action updateState)
        {
            if (ShouldSkipDisplayLayoutInteraction())
            {
                return;
            }

            bool wasEnabled = _displayState.IsDisplayConfigurationEnabled;
            bool wasDualDisplay = _displayState.IsDualDisplay;
            try
            {
                updateState?.Invoke();
                await PersistGeneralSettingsAsync(_displayState);
                ApplyDisplayStateToUi();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persist display configuration option failed.");
                _displayState.IsDisplayConfigurationEnabled = wasEnabled;
                _displayState.IsDualDisplay = wasDualDisplay;
                ShowErrorToast("显示器配置失败", ex.Message);
                ApplyDisplayStateToUi();
            }
        }

        private void SyncMainSelectionFromUi()
        {
            _displayState.SelectedMainDisplay = GetSelectedDisplayOption(MainScreenComboBox);
            _displayState.SelectedMainRotation = GetSelectedRotationOption(RotationComboBox);
            _displayState.SelectedMainResolution = GetSelectedComboBoxText(MainResolutionComboBox);
            _displayState.SelectedMainRefreshRate = GetSelectedComboBoxText(MainRefreshRateComboBox);
        }

        private void SyncSubSelectionFromUi()
        {
            _displayState.SelectedSubDisplay = GetSelectedDisplayOption(SubScreenComboBox);
            _displayState.SelectedSubRotation = GetSelectedRotationOption(SubRotationComboBox);
            _displayState.SelectedSubResolution = GetSelectedComboBoxText(SubResolutionComboBox);
            _displayState.SelectedSubRefreshRate = GetSelectedComboBoxText(SubRefreshRateComboBox);
        }

        private DisplayChoiceOption GetSelectedDisplayOption(ComboBox comboBox)
        {
            if (comboBox == null)
            {
                return null;
            }

            if (comboBox.SelectedItem is DisplayChoiceOption selectedDisplay
                && _displayState.Displays.Contains(selectedDisplay))
            {
                return selectedDisplay;
            }

            int selectedIndex = comboBox.SelectedIndex;
            return selectedIndex >= 0 && selectedIndex < _displayState.Displays.Count
                ? _displayState.Displays[selectedIndex]
                : null;
        }

        private RotationOption GetSelectedRotationOption(ComboBox comboBox)
        {
            if (comboBox == null)
            {
                return null;
            }

            if (comboBox.SelectedItem is RotationOption selectedRotation
                && _displayState.Rotations.Contains(selectedRotation))
            {
                return selectedRotation;
            }

            int selectedIndex = comboBox.SelectedIndex;
            return selectedIndex >= 0 && selectedIndex < _displayState.Rotations.Count
                ? _displayState.Rotations[selectedIndex]
                : null;
        }

        private static string GetSelectedComboBoxText(ComboBox comboBox)
        {
            return comboBox?.SelectedItem?.ToString() ?? string.Empty;
        }

        private void ApplyDisplayStateToUi()
        {
            bool previousLoadingState = _isLoadingDisplaySettings;
            bool previousDisplayUpdateState = _isUpdatingDisplayLayoutUi;
            _isLoadingDisplaySettings = true;
            _isUpdatingDisplayLayoutUi = true;

            try
            {
                ReplaceComboBoxItems(MainScreenComboBox, _displayState.Displays);
                ReplaceComboBoxItems(SubScreenComboBox, _displayState.Displays);
                ReplaceComboBoxItems(RotationComboBox, _displayState.Rotations);
                ReplaceComboBoxItems(SubRotationComboBox, _displayState.Rotations);
                ReplaceComboBoxItems(MainResolutionComboBox, _displayState.MainResolutions);
                ReplaceComboBoxItems(SubResolutionComboBox, _displayState.SubResolutions);
                ReplaceComboBoxItems(MainRefreshRateComboBox, _displayState.MainRefreshRates);
                ReplaceComboBoxItems(SubRefreshRateComboBox, _displayState.SubRefreshRates);

                if (DisplayConfigEnabledToggleSwitch != null)
                {
                    DisplayConfigEnabledToggleSwitch.IsChecked = _displayState.IsDisplayConfigurationEnabled;
                }

                if (DisplayModeComboBox != null)
                {
                    DisplayModeComboBox.SelectedIndex = _displayState.IsDualDisplay ? 1 : 0;
                }

                if (ExitRestoreToggleSwitch != null)
                {
                    ExitRestoreToggleSwitch.IsChecked = _displayState.ExitRestore;
                }

                SelectComboBoxItem(MainScreenComboBox, _displayState.SelectedMainDisplay);
                SelectComboBoxItem(SubScreenComboBox, _displayState.SelectedSubDisplay);
                SelectComboBoxItem(RotationComboBox, _displayState.SelectedMainRotation);
                SelectComboBoxItem(SubRotationComboBox, _displayState.SelectedSubRotation);

                SelectComboBoxItem(MainResolutionComboBox, _displayState.SelectedMainResolution);
                SelectComboBoxItem(SubResolutionComboBox, _displayState.SelectedSubResolution);
                SelectComboBoxItem(MainRefreshRateComboBox, _displayState.SelectedMainRefreshRate);
                SelectComboBoxItem(SubRefreshRateComboBox, _displayState.SelectedSubRefreshRate);

                if (MainOutputInfoTextBlock != null)
                {
                    MainOutputInfoTextBlock.Text = _displayState.MainOutputInfo;
                }

                if (SubOutputInfoTextBlock != null)
                {
                    SubOutputInfoTextBlock.Text = _displayState.SubOutputInfo;
                }

                if (MainStartupInfoTextBlock != null)
                {
                    MainStartupInfoTextBlock.Text = _displayState.MainStartupInfo;
                }

                if (SubStartupInfoTextBlock != null)
                {
                    SubStartupInfoTextBlock.Text = _displayState.SubStartupInfo;
                }

                ToolTip.SetTip(MainResolutionComboBox, string.IsNullOrWhiteSpace(_displayState.MainDiagnosticsTooltip) ? null : _displayState.MainDiagnosticsTooltip);
                ToolTip.SetTip(MainRefreshRateComboBox, string.IsNullOrWhiteSpace(_displayState.MainDiagnosticsTooltip) ? null : _displayState.MainDiagnosticsTooltip);
                ToolTip.SetTip(SubResolutionComboBox, string.IsNullOrWhiteSpace(_displayState.SubDiagnosticsTooltip) ? null : _displayState.SubDiagnosticsTooltip);
                ToolTip.SetTip(SubRefreshRateComboBox, string.IsNullOrWhiteSpace(_displayState.SubDiagnosticsTooltip) ? null : _displayState.SubDiagnosticsTooltip);

                SelectDisplayTarget(_displayState.SelectedTarget);
                UpdateDisplayLayoutControlsEnabled();
            }
            finally
            {
                _isLoadingDisplaySettings = previousLoadingState;
                _isUpdatingDisplayLayoutUi = previousDisplayUpdateState;
            }
        }

        private static void SelectComboBoxItem(ComboBox comboBox, object value)
        {
            if (comboBox == null)
            {
                return;
            }

            if (comboBox.Items.Count == 0)
            {
                comboBox.SelectedIndex = -1;
                return;
            }

            var selectedText = value?.ToString() ?? string.Empty;
            foreach (var item in comboBox.Items.Cast<object>())
            {
                if (ReferenceEquals(item, value)
                    || Equals(item, value)
                    || string.Equals(item?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            comboBox.SelectedIndex = 0;
        }

        private void UpdateDisplayLayoutControlsEnabled()
        {
            bool enabled = _displayState.IsDisplayConfigurationEnabled;
            bool isDualDisplay = _displayState.IsDualDisplay;
            bool subEnabled = enabled && isDualDisplay;
            var selectedTarget = _displayState.SelectedTarget;

            if (DisplayConfigDisabledMask != null)
            {
                DisplayConfigDisabledMask.IsBusy = !enabled;
            }

            if (MainScreenComboBox != null) MainScreenComboBox.IsEnabled = enabled;
            if (RotationComboBox != null) RotationComboBox.IsEnabled = enabled;
            if (MainResolutionComboBox != null) MainResolutionComboBox.IsEnabled = enabled;
            if (MainRefreshRateComboBox != null) MainRefreshRateComboBox.IsEnabled = enabled;
            if (PreviewDisplaySettingsButton != null) PreviewDisplaySettingsButton.IsEnabled = enabled;

            if (SelectMainScreenAreaButton != null)
            {
                SelectMainScreenAreaButton.IsVisible = enabled;
                SelectMainScreenAreaButton.IsEnabled = enabled;
            }

            if (SelectSubScreenAreaButton != null)
            {
                SelectSubScreenAreaButton.IsVisible = enabled && isDualDisplay;
                SelectSubScreenAreaButton.IsEnabled = subEnabled;
            }

            if (DotSubCore != null) DotSubCore.IsVisible = isDualDisplay;
            if (DotSubGlow != null) DotSubGlow.IsVisible = isDualDisplay;
            if (DotSubSelectedRing != null) DotSubSelectedRing.IsVisible = isDualDisplay && selectedTarget == DisplaySelectionTarget.Sub;

            if (SubScreenComboBox != null) SubScreenComboBox.IsEnabled = subEnabled;
            if (SubRotationComboBox != null) SubRotationComboBox.IsEnabled = subEnabled;
            if (SubResolutionComboBox != null) SubResolutionComboBox.IsEnabled = subEnabled;
            if (SubRefreshRateComboBox != null) SubRefreshRateComboBox.IsEnabled = subEnabled;

            if (!isDualDisplay && selectedTarget == DisplaySelectionTarget.Sub)
            {
                SelectDisplayTarget(DisplaySelectionTarget.None);
            }
        }

        private void StartDisplayPulseAnimation()
        {
            if (_displayPulseTimer != null)
            {
                return;
            }

            _displayPulseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _displayPulseTimer.Tick += (s, e) =>
            {
                _displayPulsePhase += 0.08;
                if (_displayPulsePhase > Math.PI * 2)
                {
                    _displayPulsePhase = 0;
                }

                double t = (Math.Sin(_displayPulsePhase) + 1d) / 2d;
                ApplyPulseVisual(DotMainGlow, 0.18, 0.58, 1.0, 1.45, t);
                ApplyPulseVisual(DotSubGlow, 0.18, 0.58, 1.0, 1.45, t);
            };
            _displayPulseTimer.Start();
        }

        private void StopDisplayAnimation()
        {
            _displayPulseTimer?.Stop();
            _displayPulseTimer = null;
        }

        private static void ApplyPulseVisual(Control control, double minOpacity, double maxOpacity, double minScale, double maxScale, double t)
        {
            if (control == null)
            {
                return;
            }

            control.Opacity = minOpacity + (maxOpacity - minOpacity) * t;
            double scale = minScale + (maxScale - minScale) * t;
            control.RenderTransformOrigin = Avalonia.RelativePoint.Center;
            control.RenderTransform = new ScaleTransform(scale, scale);
        }

        private void SelectDisplayTarget(DisplaySelectionTarget target)
        {
            bool isDualDisplay = _displayState.IsDualDisplay;
            if (target == DisplaySelectionTarget.Sub && !isDualDisplay)
            {
                target = DisplaySelectionTarget.None;
            }

            if (PanelNoScreenSelected != null) PanelNoScreenSelected.IsVisible = target == DisplaySelectionTarget.None;
            if (PanelMainScreenConfig != null) PanelMainScreenConfig.IsVisible = target == DisplaySelectionTarget.Main;
            if (PanelSubScreenConfig != null) PanelSubScreenConfig.IsVisible = isDualDisplay && target == DisplaySelectionTarget.Sub;

            if (DotMainSelectedRing != null) DotMainSelectedRing.IsVisible = target == DisplaySelectionTarget.Main;
            if (DotSubSelectedRing != null) DotSubSelectedRing.IsVisible = isDualDisplay && target == DisplaySelectionTarget.Sub;
        }

        private enum DisplaySelectionTarget
        {
            None,
            Main,
            Sub
        }

        private sealed class DisplayChoiceOption
        {
            public DisplayChoiceOption(DisplayInfo display, string displayName)
            {
                Display = display;
                DisplayName = displayName ?? string.Empty;
            }

            public DisplayInfo Display { get; }

            public string DisplayName { get; }

            public override string ToString() => DisplayName;
        }

        private sealed class RotationOption
        {
            public RotationOption(int angle)
            {
                Angle = angle;
                DisplayName = GetDisplayName(angle);
            }

            public int Angle { get; }

            public string DisplayName { get; }

            public static string GetDisplayName(int angle)
            {
                int normalizedAngle = ((angle % 360) + 360) % 360;
                return normalizedAngle switch
                {
                    0 => "横向",
                    90 => "纵向",
                    180 => "横向（翻转）",
                    270 => "纵向（翻转）",
                    _ => $"{normalizedAngle}°"
                };
            }

            public override string ToString() => DisplayName;
        }

        private sealed class DisplayConfigurationState
        {
            public List<DisplayChoiceOption> Displays { get; } = new List<DisplayChoiceOption>();
            public List<RotationOption> Rotations { get; } = new List<RotationOption>();
            public List<string> MainResolutions { get; } = new List<string>();
            public List<string> SubResolutions { get; } = new List<string>();
            public List<string> MainRefreshRates { get; } = new List<string>();
            public List<string> SubRefreshRates { get; } = new List<string>();
            public bool IsDisplayConfigurationEnabled { get; set; }
            public bool IsDualDisplay { get; set; }
            public bool ExitRestore { get; set; } = true;
            public DisplayChoiceOption SelectedMainDisplay { get; set; }
            public DisplayChoiceOption SelectedSubDisplay { get; set; }
            public RotationOption SelectedMainRotation { get; set; }
            public RotationOption SelectedSubRotation { get; set; }
            public string SelectedMainResolution { get; set; } = string.Empty;
            public string SelectedSubResolution { get; set; } = string.Empty;
            public string SelectedMainRefreshRate { get; set; } = string.Empty;
            public string SelectedSubRefreshRate { get; set; } = string.Empty;
            public string MainOutputInfo { get; set; } = string.Empty;
            public string SubOutputInfo { get; set; } = string.Empty;
            public string MainStartupInfo { get; set; } = string.Empty;
            public string SubStartupInfo { get; set; } = string.Empty;
            public string MainDiagnosticsTooltip { get; set; } = string.Empty;
            public string SubDiagnosticsTooltip { get; set; } = string.Empty;
            public DisplaySelectionTarget SelectedTarget { get; set; } = DisplaySelectionTarget.None;
            public bool ShowNoScreenSelected { get; set; } = true;
            public bool ShowMainScreenConfig { get; set; }
            public bool ShowSubScreenConfig { get; set; }

            public void SelectMainDisplay()
            {
                SelectedTarget = DisplaySelectionTarget.Main;
                ShowNoScreenSelected = false;
                ShowMainScreenConfig = true;
                ShowSubScreenConfig = false;
            }

            public void SelectSubDisplay()
            {
                if (!IsDualDisplay)
                {
                    SelectedTarget = DisplaySelectionTarget.None;
                    ShowNoScreenSelected = true;
                    ShowMainScreenConfig = false;
                    ShowSubScreenConfig = false;
                    return;
                }

                SelectedTarget = DisplaySelectionTarget.Sub;
                ShowNoScreenSelected = false;
                ShowMainScreenConfig = false;
                ShowSubScreenConfig = true;
            }
        }

        private sealed record DisplayConfigurationRequest(
            bool IsDisplayConfigurationEnabled,
            bool IsDualDisplay,
            bool ExitRestore,
            DisplayChoiceOption SelectedMainDisplay,
            DisplayChoiceOption SelectedSubDisplay,
            RotationOption SelectedMainRotation,
            RotationOption SelectedSubRotation,
            string SelectedMainResolution,
            string SelectedSubResolution,
            string SelectedMainRefreshRate,
            string SelectedSubRefreshRate);
    }
}
