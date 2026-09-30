using System;
using System.Collections.Generic;
using System.Threading;

namespace LazyBootstrap.Services
{
    internal sealed class DisplaySettingsRequest
    {
        public DisplaySettingsRequest(string targetName, string deviceName, int angle, int width, int height, int refreshRate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

            TargetName = targetName;
            DeviceName = deviceName;
            Angle = angle;
            Width = width;
            Height = height;
            RefreshRate = refreshRate;
        }

        public string TargetName { get; }

        public string DeviceName { get; }

        public int Angle { get; }

        public int Width { get; }

        public int Height { get; }

        public int RefreshRate { get; }
    }

    internal sealed class DisplaySettingsTransactionResult
    {
        public DisplaySettingsTransactionResult(bool succeeded, IReadOnlyDictionary<string, DisplayState> restoreStates, IReadOnlyList<string> messages)
        {
            Succeeded = succeeded;
            RestoreStates = restoreStates ?? new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
            Messages = messages ?? Array.Empty<string>();
        }

        public bool Succeeded { get; }

        public bool Cancelled { get; init; }

        public string UserMessage { get; init; }

        public IReadOnlyDictionary<string, DisplayState> RestoreStates { get; }

        public IReadOnlyList<string> Messages { get; }
    }


    internal sealed class DisplaySettingsTransactionCoordinator
    {
        private readonly WindowsDisplayConfigurationService _displayConfigurationService;

        public DisplaySettingsTransactionCoordinator(WindowsDisplayConfigurationService displayConfigurationService)
        {
            ArgumentNullException.ThrowIfNull(displayConfigurationService);
            _displayConfigurationService = displayConfigurationService;
        }

        public DisplaySettingsTransactionResult Apply(IReadOnlyList<DisplaySettingsRequest> requests, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requests);

            var messages = new List<string>();
            var restoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
            var normalizedRequests = NormalizeRequests(requests, messages);
            if (messages.Count > 0)
            {
                return new DisplaySettingsTransactionResult(false, restoreStates, messages);
            }

            foreach (var request in normalizedRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stateResult = _displayConfigurationService.GetCurrentState(request.DeviceName);
                if (!stateResult.Succeeded)
                {
                    messages.Add($"{request.TargetName}无法备份当前状态: {stateResult.ErrorMessage}");
                    return new DisplaySettingsTransactionResult(false, new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase), messages);
                }

                restoreStates[request.DeviceName] = stateResult.State;
            }

            var appliedRequests = new List<DisplaySettingsRequest>();
            foreach (var request in normalizedRequests)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    messages.Add("已取消显示器配置，正在还原已应用的设置。");
                    return new DisplaySettingsTransactionResult(false, RollbackAppliedRequests(appliedRequests, restoreStates, messages), messages) { Cancelled = true };
                }
                var applyResult = _displayConfigurationService.ApplyDisplaySettings(
                    request.DeviceName,
                    request.Angle,
                    request.Width,
                    request.Height,
                    request.RefreshRate);

                if (applyResult.Succeeded)
                {
                    appliedRequests.Add(request);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        messages.Add("已取消显示器配置，正在还原已应用的设置。");
                        return new DisplaySettingsTransactionResult(false, RollbackAppliedRequests(appliedRequests, restoreStates, messages), messages) { Cancelled = true };
                    }
                    continue;
                }

                messages.Add($"{request.TargetName}应用失败: {applyResult.ErrorMessage}");
                var pendingRestoreStates = RollbackAppliedRequests(appliedRequests, restoreStates, messages);
                return new DisplaySettingsTransactionResult(false, pendingRestoreStates, messages);
            }

            return new DisplaySettingsTransactionResult(true, restoreStates, messages);
        }

        public DisplaySettingsTransactionResult Restore(IReadOnlyDictionary<string, DisplayState> states)
        {
            var pending = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
            var messages = new List<string>();
            foreach (var state in states.Values)
            {
                var result = _displayConfigurationService.RestoreDisplaySettings(state);
                if (result.Succeeded) continue;
                pending[state.DeviceName] = state;
                messages.Add($"还原 {state.DeviceName} 失败: {result.ErrorMessage}");
            }
            return new DisplaySettingsTransactionResult(pending.Count == 0, pending, messages);
        }

        private static IReadOnlyList<DisplaySettingsRequest> NormalizeRequests(IReadOnlyList<DisplaySettingsRequest> requests, List<string> messages)
        {
            var normalizedRequests = new List<DisplaySettingsRequest>(requests.Count);
            var seenDevices = new Dictionary<string, DisplaySettingsRequest>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                if (seenDevices.TryGetValue(request.DeviceName, out var existingRequest))
                {
                    messages.Add($"显示器选择冲突: {existingRequest.TargetName}与{request.TargetName}选择了同一台显示器 {request.DeviceName}。");
                    continue;
                }

                seenDevices[request.DeviceName] = request;
                normalizedRequests.Add(request);
            }

            return normalizedRequests;
        }

        private Dictionary<string, DisplayState> RollbackAppliedRequests(
            IReadOnlyList<DisplaySettingsRequest> appliedRequests,
            IReadOnlyDictionary<string, DisplayState> restoreStates,
            List<string> messages)
        {
            var pendingRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in appliedRequests)
            {
                if (!restoreStates.TryGetValue(request.DeviceName, out var restoreState))
                {
                    messages.Add($"{request.TargetName}回滚失败: 未找到原始显示器状态。");
                    continue;
                }

                var restoreResult = _displayConfigurationService.RestoreDisplaySettings(restoreState);
                if (!restoreResult.Succeeded)
                {
                    messages.Add($"{request.TargetName}回滚失败: {restoreResult.ErrorMessage}");
                    pendingRestoreStates[request.DeviceName] = restoreState;
                }
            }

            return pendingRestoreStates;
        }
    }
}
