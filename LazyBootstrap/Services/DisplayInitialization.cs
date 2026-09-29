using System;

namespace LazyBootstrap.Services
{
    internal enum DisplayRefreshOutcome { Completed, Failed, Canceled, Deferred }

    // UI-thread-owned intent: ordinary refreshes cannot save configuration unless the user enabled it.
    internal sealed class DisplayInitialization
    {
        public long Generation { get; private set; }
        public bool IsPending { get; private set; }

        public void Begin() { Generation++; IsPending = true; }
        public void Cancel() { Generation++; IsPending = false; }

        public bool TrySave(long generation, DisplayRefreshOutcome outcome, bool targetsReady, Action save)
        {
            if (!IsPending || generation != Generation || outcome != DisplayRefreshOutcome.Completed || !targetsReady)
                return false;
            save();
            IsPending = false;
            return true;
        }
    }
}
