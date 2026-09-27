using System;
using System.Collections.Generic;
using System.Linq;

namespace LazyBootstrap.Services
{
    internal sealed class DisplayCatalog
    {
        public IReadOnlyList<DisplayInfo> Displays { get; private set; } = Array.Empty<DisplayInfo>();
        public string StatusMessage { get; private set; } = string.Empty;

        public void Update(DisplayDiscoveryResult result)
        {
            StatusMessage = result.ErrorMessage;
            if (result.Status == DisplayDiscoveryStatus.Failed) return;
            if (result.Status == DisplayDiscoveryStatus.Complete)
            {
                Displays = result.Displays.ToArray();
                return;
            }
            // A partial read can add outputs, but cannot prove an old output was disconnected.
            var merged = Displays.ToDictionary(display => display.DeviceName, StringComparer.OrdinalIgnoreCase);
            foreach (var display in result.Displays) merged[display.DeviceName] = display;
            Displays = merged.Values.ToArray();
        }

        public static DisplayInfo Resolve(IReadOnlyList<DisplayInfo> displays, string persistentId,
            string legacyIndex, int defaultIndex, string previousName = "")
        {
            if (!string.IsNullOrWhiteSpace(persistentId))
            {
                var match = displays.FirstOrDefault(display =>
                    string.Equals(display.PersistentId, persistentId, StringComparison.OrdinalIgnoreCase));
                // Older configurations could store a GDI name before the monitor identity was available.
                if (match == null && persistentId.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                    match = displays.FirstOrDefault(display => string.Equals(display.DeviceName, persistentId, StringComparison.OrdinalIgnoreCase));
                return match ?? new DisplayInfo
                {
                    PersistentId = persistentId,
                    FriendlyName = string.IsNullOrWhiteSpace(previousName) ? "已保存的显示器" : previousName
                };
            }
            int index = int.TryParse(legacyIndex, out int legacy) ? legacy : defaultIndex;
            // Keep a legacy index pending if startup sees fewer outputs than the stored index.
            if (index >= displays.Count && !string.IsNullOrWhiteSpace(legacyIndex)) return null;
            return displays.Count == 0 ? null : displays[Math.Clamp(index, 0, displays.Count - 1)];
        }
    }
}
