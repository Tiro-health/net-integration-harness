using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tiro.Health.SmartWebMessaging;

namespace Tiro.Health.SmartWebMessaging.Tests
{
    /// <summary>
    /// Tiro.Health.SmartWebMessaging targets net48/netstandard2.0 where Task.WaitAsync
    /// (.NET 6+) is unavailable; <see cref="TaskExtensions.WaitAsync"/> is our polyfill.
    /// These tests run on net8 (test project) but reference the polyfill explicitly via
    /// the namespace import, so the same code path is exercised.
    /// </summary>
    [TestClass]
    public sealed class TestTaskExtensions
    {
        [TestMethod]
        public async Task WaitAsync_AlreadyCompletedTask_ReturnsImmediately()
        {
            var task = Task.CompletedTask;
            using var cts = new CancellationTokenSource();
            // Polyfill returns the original task when it's already complete.
            await TaskExtensions.WaitAsync(task, cts.Token);
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }

        [TestMethod]
        public async Task WaitAsync_NonCancellableToken_ReturnsOriginalTask()
        {
            var tcs = new TaskCompletionSource<int>();
            // Default token can never be cancelled — polyfill takes a fast path.
            var waitTask = TaskExtensions.WaitAsync(tcs.Task, CancellationToken.None);
            Assert.IsFalse(waitTask.IsCompleted);
            tcs.SetResult(7);
            var result = await waitTask;
            Assert.AreEqual(7, result);
        }

        [TestMethod]
        public async Task WaitAsync_PreCancelledToken_ThrowsOperationCanceled()
        {
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
                await TaskExtensions.WaitAsync(tcs.Task, cts.Token));
        }

        [TestMethod]
        public async Task WaitAsync_TokenCancelledMidWait_ThrowsOperationCanceled_AndUnderlyingTaskKeepsRunning()
        {
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource();
            var waitTask = TaskExtensions.WaitAsync(tcs.Task, cts.Token);
            Assert.IsFalse(waitTask.IsCompleted);

            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waitTask);

            // The polyfill cancels the *wait wrapper*, not the underlying task.
            Assert.IsFalse(tcs.Task.IsCanceled, "Underlying task must NOT be cancelled by WaitAsync.");
            Assert.IsFalse(tcs.Task.IsCompleted, "Underlying task is still pending.");
        }

        [TestMethod]
        public async Task WaitAsync_TaskCompletesBeforeCancellation_Resolves()
        {
            var tcs = new TaskCompletionSource<string>();
            using var cts = new CancellationTokenSource();
            var waitTask = TaskExtensions.WaitAsync(tcs.Task, cts.Token);
            tcs.SetResult("done");
            var result = await waitTask;
            Assert.AreEqual("done", result);
        }

        [TestMethod]
        public async Task WaitAsync_FaultedTask_PropagatesException()
        {
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource();
            var waitTask = TaskExtensions.WaitAsync(tcs.Task, cts.Token);
            tcs.SetException(new InvalidOperationException("nope"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await waitTask);
        }

        [TestMethod]
        public async Task WaitAsync_NonGeneric_TaskCompletesBeforeCancellation_Resolves()
        {
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource();
            var waitTask = TaskExtensions.WaitAsync((Task)tcs.Task, cts.Token);
            tcs.SetResult(true);
            await waitTask;
            Assert.IsTrue(waitTask.IsCompletedSuccessfully);
        }

        [TestMethod]
        public async Task WaitAsync_NonGeneric_TokenCancelledMidWait_ThrowsOperationCanceled()
        {
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource();
            var waitTask = TaskExtensions.WaitAsync((Task)tcs.Task, cts.Token);
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waitTask);
        }

        [TestMethod]
        public async Task WaitAsync_NullTask_ThrowsArgumentNull()
        {
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await TaskExtensions.WaitAsync((Task<int>)null!, cts.Token));
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await TaskExtensions.WaitAsync((Task)null!, cts.Token));
        }
    }
}
