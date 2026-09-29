using System;
using System.Threading.Tasks;

namespace LazyBootstrap.Services
{
    // Owned by the UI thread. Keeps stop/close cleanup behind the workflow's final snapshot handoff.
    internal sealed class LaunchWorkflowLifetime
    {
        private Task _workflow = Task.CompletedTask;
        private Task _stopping;
        public bool IsStopping => _stopping != null;
        public bool IsBusy => IsStopping || !_workflow.IsCompleted;

        public Task RunAsync(Func<Task> run)
        {
            if (IsBusy) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workflow = completion.Task;
            _ = RunCoreAsync(run, completion);
            return completion.Task;
        }

        private static async Task RunCoreAsync(Func<Task> run, TaskCompletionSource completion)
        {
            try { await run(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }

        public Task StopAsync(Action cancel, Func<Task> cleanup)
        {
            if (_stopping != null) return _stopping;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = completion.Task;
            _ = StopCoreAsync(_workflow, cancel, cleanup, completion);
            return completion.Task;
        }

        private async Task StopCoreAsync(Task workflow, Action cancel, Func<Task> cleanup, TaskCompletionSource completion)
        {
            Exception failure = null;
            try
            {
                cancel();
                try { await workflow; }
                finally { await cleanup(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { _stopping = null; }
            if (failure == null) completion.SetResult();
            else completion.SetException(failure);
        }
    }
}
