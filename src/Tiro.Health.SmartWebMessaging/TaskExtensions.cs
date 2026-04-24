using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// Polyfills for awaiter helpers missing from <c>net48</c>/<c>netstandard2.0</c>
    /// (e.g. <c>Task.WaitAsync(CancellationToken)</c>, introduced in .NET 6).
    /// </summary>
    public static class TaskExtensions
    {
        /// <summary>
        /// Returns a task that completes when <paramref name="task"/> completes,
        /// or faults with <see cref="OperationCanceledException"/> when
        /// <paramref name="cancellationToken"/> is cancelled — whichever happens first.
        /// The underlying <paramref name="task"/> is NOT cancelled; only the wait wrapper is.
        /// </summary>
        public static Task WaitAsync(this Task task, CancellationToken cancellationToken)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (task.IsCompleted || !cancellationToken.CanBeCanceled) return task;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            task.ContinueWith(completed =>
            {
                registration.Dispose();
                if (completed.IsFaulted) tcs.TrySetException(completed.Exception.InnerExceptions);
                else if (completed.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(true);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>
        /// Returns a task that completes with the result of <paramref name="task"/>,
        /// or faults with <see cref="OperationCanceledException"/> when
        /// <paramref name="cancellationToken"/> is cancelled — whichever happens first.
        /// The underlying <paramref name="task"/> is NOT cancelled; only the wait wrapper is.
        /// </summary>
        public static Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (task.IsCompleted || !cancellationToken.CanBeCanceled) return task;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            task.ContinueWith(completed =>
            {
                registration.Dispose();
                if (completed.IsFaulted) tcs.TrySetException(completed.Exception.InnerExceptions);
                else if (completed.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(completed.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return tcs.Task;
        }
    }
}
