using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Controls;
using LazyBootstrap.Platform;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private bool _isWindowCloseAnimationRunning;
        private bool _allowImmediateWindowClose;

        private const int WindowFadeDurationMs = 480;
        private const int WindowFadeFrameDelayMs = 8;
        private const int ExStyleIndex = -20;
        private const int LayeredWindowStyle = 0x00080000;
        private const uint LayeredWindowAlphaFlag = 0x2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        private void SetRuntimeInstallProgress(string statusText, double progressValue)
        {
            double normalizedProgressValue = Math.Clamp(progressValue, 0d, 100d);
            if (RuntimeStatusText != null) RuntimeStatusText.Text = statusText ?? string.Empty;
            if (RuntimeProgressBar != null) RuntimeProgressBar.Value = normalizedProgressValue;
            var pct = $"{Math.Clamp((int)Math.Round(normalizedProgressValue), 0, 100)}%";
            if (RuntimeProgressValueText != null) RuntimeProgressValueText.Text = pct;
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowImmediateWindowClose)
            {
                PerformFinalWindowCloseCleanup();
                return;
            }

            e.Cancel = true;

            if (_isWindowCloseAnimationRunning || !IsVisible)
            {
                return;
            }

            _ = PlayWindowFadeOutAndCloseAsync();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            DisposeDisplayRefresh();
            Opened -= OnWindowOpened;
            Closed -= OnWindowClosed;
            Closing -= OnWindowClosing;
            if (MainSideMenu != null)
            {
                MainSideMenu.SelectionChanged -= OnMainSideMenuSelectionChanged;
            }

            StopDisplayAnimation();
        }

        internal async Task PlayWindowFadeOutAndCloseAsync()
        {
            if (_isWindowCloseAnimationRunning)
            {
                return;
            }

            _isWindowCloseAnimationRunning = true;

            try
            {
                if (!await TryPlayNativeWindowFadeAsync(byte.MaxValue, 0))
                {
                    await CreateWindowOpacityAnimation(Math.Clamp(Opacity, 0d, 1d), 0d).RunAsync(this, CancellationToken.None);
                }
            }
            finally
            {
                Hide();
                _allowImmediateWindowClose = true;
                _isWindowCloseAnimationRunning = false;
                Close();
            }
        }

        private async Task<bool> TryPlayNativeWindowFadeAsync(byte fromAlpha, byte toAlpha)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            if (!TryGetWindowHandle(out var hwnd))
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
                if (!TryGetWindowHandle(out hwnd))
                {
                    return false;
                }
            }

            EnsureLayeredWindowStyle(hwnd);
            SetWindowAlpha(hwnd, fromAlpha);
            await AnimateNativeWindowAlphaAsync(hwnd, fromAlpha, toAlpha);
            SetWindowAlpha(hwnd, toAlpha);
            return true;
        }

        private static async Task AnimateNativeWindowAlphaAsync(IntPtr hwnd, byte fromAlpha, byte toAlpha)
        {
            var start = Stopwatch.StartNew();

            while (true)
            {
                var progress = Math.Clamp(start.Elapsed.TotalMilliseconds / WindowFadeDurationMs, 0d, 1d);
                var easedProgress = AnimationEasing.EaseInOutCubic(progress);
                var currentAlpha = (byte)Math.Round(fromAlpha + ((toAlpha - fromAlpha) * easedProgress));
                SetWindowAlpha(hwnd, currentAlpha);

                if (progress >= 1d)
                {
                    break;
                }

                await Task.Delay(WindowFadeFrameDelayMs);
            }
        }

        private bool TryGetWindowHandle(out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            var platformHandle = TryGetPlatformHandle();
            if (platformHandle?.Handle is not IntPtr handle || handle == IntPtr.Zero)
            {
                return false;
            }

            hwnd = handle;
            return true;
        }

        private static void SetWindowAlpha(IntPtr hwnd, byte alpha)
        {
            SetLayeredWindowAttributes(hwnd, 0, alpha, LayeredWindowAlphaFlag);
        }

        private static void EnsureLayeredWindowStyle(IntPtr hwnd)
        {
            var exStyle = GetWindowExStyle(hwnd);
            var layeredExStyle = new IntPtr(exStyle.ToInt64() | LayeredWindowStyle);
            if (layeredExStyle != exStyle)
            {
                SetWindowExStyle(hwnd, layeredExStyle);
            }
        }

        private static nint GetWindowExStyle(nint hwnd) =>
            nint.Size == 8
                ? GetWindowLongPtr64(hwnd, ExStyleIndex)
                : GetWindowLong32(hwnd, ExStyleIndex);

        private static void SetWindowExStyle(nint hwnd, nint exStyle)
        {
            if (nint.Size == 8)
            {
                SetWindowLongPtr64(hwnd, ExStyleIndex, exStyle);
            }
            else
            {
                SetWindowLong32(hwnd, ExStyleIndex, (int)exStyle);
            }
        }

        private static Animation CreateWindowOpacityAnimation(double fromOpacity, double toOpacity)
        {
            return new Animation
            {
                Duration = TimeSpan.FromMilliseconds(WindowFadeDurationMs),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter { Property = OpacityProperty, Value = fromOpacity }
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter { Property = OpacityProperty, Value = toOpacity }
                        }
                    }
                }
            };
        }

        private void PerformFinalWindowCloseCleanup()
        {
            AsioDriverRegistry.DisposeControlPanelDrivers();
            ReleaseLaunchControls();
            try
            {
                HandleLaunchClosingAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final window close cleanup failed.");
            }
        }
    }
}
