// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Tests.Platform;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// The queue a music track's expensive work runs on: one at a time, in order, never on the caller,
/// and past any failure.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class AsyncOperationQueueTests : IDisposable
{
    private const uint Tag = 0x20000001;

    private readonly List<Exception> _nonFatal = [];
    private readonly List<Exception?> _fatal = [];

    public AsyncOperationQueueTests()
    {
        FatalError.SetUnwindForTests(true);
        NonFatalError.ResetForTests();
        Trace.NonFatalException += this.OnNonFatal;
        Trace.FatalException += this.OnFatal;
    }

    public void Dispose()
    {
        Trace.NonFatalException -= this.OnNonFatal;
        Trace.FatalException -= this.OnFatal;
        FatalError.SetUnwindForTests(false);
        NonFatalError.ResetForTests();
    }

    private void OnNonFatal(
        object? sender,
        NonFatalExceptionEventArgs e)
    {
        lock (this._nonFatal)
        {
            this._nonFatal.Add(e.Exception);
        }
    }

    private void OnFatal(
        object? sender,
        FatalExceptionEventArgs e)
    {
        lock (this._fatal)
        {
            this._fatal.Add(e.Exception);
        }
    }

    [Fact]
    public async Task OperationsRunInOrder_OneAtATime()
    {
        var queue = new AsyncOperationQueue();
        var ran = new List<int>();
        int running = 0;
        int mostRunning = 0;

        for (int i = 0; i < 50; i++)
        {
            int index = i;
            _ = queue.Enqueue(
                async () =>
                {
                    mostRunning = Math.Max(mostRunning, Interlocked.Increment(ref running));
                    await Task.Yield();
                    lock (ran)
                    {
                        ran.Add(index);
                    }

                    Interlocked.Decrement(ref running);
                },
                Tag);
        }

        await queue.Current;

        Assert.Equal(1, mostRunning);
        Assert.Equal(50, ran.Count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(i, ran[i]);
        }
    }

    [Fact]
    public async Task Enqueue_NeverRunsTheOperationOnTheCaller()
    {
        var queue = new AsyncOperationQueue();
        object gate = new();
        bool ran = false;
        Task operation;

        // NOTE: A lock is reentrant, so an operation run inline on this thread would get in here
        lock (gate)
        {
            operation = queue.Enqueue(
                () =>
                {
                    lock (gate)
                    {
                        ran = true;
                    }

                    return Task.CompletedTask;
                },
                Tag);

            Assert.False(ran);
            Assert.False(operation.IsCompleted);
        }

        await operation;
        Assert.True(ran);
    }

    [Fact]
    public async Task AnOperationAfterASlowOne_WaitsForIt()
    {
        var queue = new AsyncOperationQueue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool ranSecond = false;

        var first = queue.Enqueue(() => gate.Task, Tag);
        var second = queue.Enqueue(
            () =>
            {
                ranSecond = true;
                return Task.CompletedTask;
            },
            Tag);

        Assert.False(first.IsCompleted);
        Assert.False(ranSecond);

        gate.SetResult();
        await second;

        Assert.True(first.IsCompleted);
        Assert.True(ranSecond);
    }

    [Fact]
    public async Task Current_IsEverythingQueuedSoFar()
    {
        var queue = new AsyncOperationQueue();
        Assert.True(queue.Current.IsCompleted);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = queue.Enqueue(() => gate.Task, Tag);

        Assert.Same(operation, queue.Current);

        gate.SetResult();
        await queue.Current;
        Assert.True(operation.IsCompleted);
    }

    [Fact]
    public async Task AnOperationReturningNoTask_IsFine()
    {
        var queue = new AsyncOperationQueue();

        await queue.Enqueue(() => null!, Tag);

        Assert.Empty(this._fatal);
        Assert.Empty(this._nonFatal);
    }

    [Fact]
    public void Enqueue_RefusesANullOperation()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = new AsyncOperationQueue().Enqueue(null!, Tag); });
    }

    [Fact]
    public async Task ALongChain_DoesNotOverflowTheStack()
    {
        var queue = new AsyncOperationQueue();
        int ran = 0;

        for (int i = 0; i < 20000; i++)
        {
            _ = queue.Enqueue(
                () =>
                {
                    ran++;
                    return Task.CompletedTask;
                },
                Tag);
        }

        await queue.Current;

        Assert.Equal(20000, ran);
    }

    public static TheoryData<bool> ThrownOrFaulted => [true, false];

    [Theory]
    [MemberData(nameof(ThrownOrFaulted))]
    public async Task AFailure_DoesNotStopTheQueue(
        bool thrown)
    {
        var queue = new AsyncOperationQueue();
        var fault = new InvalidOperationException("Bad track");
        bool ranNext = false;

        var failing = queue.Enqueue(
            () => thrown ? throw fault : Task.FromException(fault),
            Tag);
        await queue.Enqueue(
            () =>
            {
                ranNext = true;
                return Task.CompletedTask;
            },
            Tag);

        Assert.True(failing.IsCompleted);
        Assert.True(ranNext);
    }

#if DEBUG
    [Fact]
    public async Task AFailure_EndsADebugBuild()
    {
        var queue = new AsyncOperationQueue();
        var fault = new InvalidOperationException("Bad track");

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.Enqueue(() => throw fault, Tag));

        Assert.Equal([fault], this._fatal);
        Assert.Empty(this._nonFatal);
    }
#else
    [Fact]
    public async Task AFailure_IsReported_AndTheGameRunsOn()
    {
        var queue = new AsyncOperationQueue();
        var fault = new InvalidOperationException("Bad track");

        await queue.Enqueue(() => throw fault, Tag);

        Assert.Equal([fault], this._nonFatal);
        Assert.Empty(this._fatal);
    }
#endif
}
