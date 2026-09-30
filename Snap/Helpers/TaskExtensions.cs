using Snap.Services;

namespace Snap.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Runs <paramref name="task"/> without awaiting it. A failure is written to snap.log and
    /// shown in the status bar (<see cref="Log.UserError"/>) instead of going unobserved.
    /// Cancellation is ignored. Use this instead of <c>_ = task</c> and instead of async void
    /// methods that are not top-level event handlers (#13).
    /// </summary>
    public static void SafeFireAndForget(this Task task, string where, string userMessage = "処理に失敗しました")
    {
        task.ContinueWith(
            t =>
            {
                var ex = t.Exception?.GetBaseException();
                if (ex is OperationCanceledException) return;
                Log.UserError(where, userMessage, ex);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
