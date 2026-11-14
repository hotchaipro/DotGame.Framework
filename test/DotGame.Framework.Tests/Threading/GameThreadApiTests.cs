// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Tests.Platform;
using DotGame.Framework.Threading;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Threading;

/// <summary>
/// <see cref="GameThread"/>: posting work to the game thread and awaiting it from a worker, with
/// one rule for faults and for cancellation (finding C11). The blocking
/// <c>InvokeOnGameThread</c> overloads are going away (decision 6), so nothing here pins them.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class GameThreadApiTests : IDisposable
{
    private readonly List<Exception?> _fatal = [];

    public GameThreadApiTests()
    {
        FatalError.SetUnwindForTests(true);
        Trace.FatalException += this.OnFatal;
    }

    public void Dispose()
    {
        Trace.FatalException -= this.OnFatal;
        FatalError.SetUnwindForTests(false);
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

    public static TheoryData<string> Overloads => ["Action", "Func<T>", "Func<Task>", "Func<Task<T>>"];

    public static TheoryData<string> AsyncOverloads => ["Func<Task>", "Func<Task<T>>"];

    // Calls the GameThread.InvokeAsync overload named with a body that runs synchronously, as
    // the delegate's own body rather than after an await
    private static Task Invoke(
        string overload,
        Action body)
    {
        switch (overload)
        {
            case "Action":
                return GameThread.InvokeAsync(body);

            case "Func<T>":
                Func<int> value = () =>
                {
                    body();
                    return 1;
                };
                return GameThread.InvokeAsync(value);

            case "Func<Task>":
                Func<Task> task = async () =>
                {
                    await Task.Yield();
                    body();
                };
                return GameThread.InvokeAsync(task);

            default:
                Func<Task<int>> taskOfValue = async () =>
                {
                    await Task.Yield();
                    body();
                    return 1;
                };
                return GameThread.InvokeAsync(taskOfValue);
        }
    }

    // ── GameThread.Post ───────────────────────────────────────────────

    [Fact]
    public void IsGameThread_FollowsTheGameThread()
    {
        Assert.False(GameThread.IsCurrent);

        OnGameThread(() =>
        {
            Assert.True(GameThread.IsCurrent);
            OnOtherThread(() => Assert.False(GameThread.IsCurrent));
        });
    }

    [Fact]
    public void Post_WaitsForThePump_EvenOnTheGameThread()
    {
        OnGameThread(() =>
        {
            bool ranAction = false;
            bool ranFunction = false;

            GameThread.Post(() => ranAction = true);
            GameThread.Post(new Func<Task>(() =>
            {
                ranFunction = true;
                return Task.CompletedTask;
            }));
            Assert.False(ranAction);
            Assert.False(ranFunction);

            GameHost.DrainContinuations();

            Assert.True(ranAction);
            Assert.True(ranFunction);
        });
    }

    [Fact]
    public void Post_FromAWorker_RunsOnTheGameThread()
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int ranOn = 0;

            OnOtherThread(() => GameThread.Post(() => ranOn = Environment.CurrentManagedThreadId));
            Assert.Equal(0, ranOn);

            GameHost.DrainContinuations();

            Assert.Equal(gameThread, ranOn);
        });
    }

    [Fact]
    public void Post_ActionThatThrows_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From a posted action");

            GameThread.Post(() => throw fault);

            Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => GameHost.DrainContinuations()));
            Assert.Equal([fault], this._fatal);
        });
    }

    [Fact]
    public void Post_FunctionThatThrows_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From a posted function");

            GameThread.Post(new Func<Task>(() => throw fault));

            Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => PumpUntil(() => false)));
            Assert.Equal([fault], this._fatal);
        });
    }

    [Fact]
    public void Post_FunctionThatFaultsAfterAnAwait_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("After an await");

            GameThread.Post(new Func<Task>(async () =>
            {
                await Task.Yield();
                throw fault;
            }));

            Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => PumpUntil(() => false)));
            Assert.Equal([fault], this._fatal);
        });
    }

    // C11: token.ThrowIfCancellationRequested() throws OperationCanceledException, not its subclass
    [Fact]
    public void Post_Cancellation_IsNotFatal_FromAnAction()
    {
        OnGameThread(() =>
        {
            GameThread.Post(() => throw new OperationCanceledException());
            GameThread.Post(() => throw new TaskCanceledException());

            GameHost.DrainContinuations();

            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void Post_Cancellation_IsNotFatal_FromAFunction()
    {
        OnGameThread(() =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool ranAll = false;

            GameThread.Post(new Func<Task>(() => throw new OperationCanceledException()));
            GameThread.Post(new Func<Task>(() => throw new TaskCanceledException()));
            GameThread.Post(new Func<Task>(async () =>
            {
                await Task.Yield();
                canceled.Token.ThrowIfCancellationRequested();
            }));
            GameThread.Post(new Func<Task>(async () =>
            {
                await Task.Delay(Timeout.Infinite, canceled.Token);
            }));
            GameThread.Post(() => ranAll = true);

            PumpUntil(() => ranAll);
            GameHost.DrainContinuations();

            Assert.Empty(this._fatal);
        });
    }

    // ── GameThread.InvokeAsync on the game thread ─────────────────────

    [Fact]
    public void InvokeAsync_OnTheGameThread_RunsItNow()
    {
        OnGameThread(() =>
        {
            bool ran = false;

            var action = GameThread.InvokeAsync(() => ran = true);
            Assert.True(ran);
            Assert.True(action.IsCompletedSuccessfully);

            var value = GameThread.InvokeAsync(() => 7);
            Assert.True(value.IsCompletedSuccessfully);
            Assert.Equal(7, value.GetAwaiter().GetResult());

            Func<Task<int>> function = () => Task.FromResult(8);
            var functionValue = GameThread.InvokeAsync(function);
            Assert.True(functionValue.IsCompletedSuccessfully);
            Assert.Equal(8, functionValue.GetAwaiter().GetResult());
        });
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public void InvokeAsync_OnTheGameThread_RestoresAClearedContext(
        string overload)
    {
        OnGameThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            SynchronizationContext? seen = null;

            var task = Invoke(overload, () => seen = SynchronizationContext.Current);
            PumpUntil(() => task.IsCompleted);

            Assert.Same(GameSynchronizationContext.GameThread, seen);
        });
    }

    [Fact]
    public void InvokeAsync_OnTheGameThread_ReportsAThrowThroughTheTask()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From the body");

            var task = GameThread.InvokeAsync(() => throw fault);

            Assert.True(task.IsFaulted);
            Assert.Same(fault, task.Exception!.InnerException);
        });
    }

    // ── GameThread.InvokeAsync from a worker ──────────────────────────

    [Theory]
    [MemberData(nameof(Overloads))]
    public void InvokeAsync_FromAWorker_RunsItOnTheGameThread_AtThePump(
        string overload)
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int ranOn = 0;

            Task? task = null;
            OnOtherThread(() => task = Invoke(overload, () => ranOn = Environment.CurrentManagedThreadId));
            Assert.Equal(0, ranOn);

            PumpUntil(() => task!.IsCompleted);

            Assert.Equal(gameThread, ranOn);
            Assert.True(task!.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void InvokeAsync_FromAWorker_ReturnsTheResult()
    {
        OnGameThread(() =>
        {
            Task<int>? value = null;
            Task<int>? functionValue = null;

            OnOtherThread(() =>
            {
                value = GameThread.InvokeAsync(() => 7);
                Func<Task<int>> function = async () =>
                {
                    await Task.Yield();
                    return 8;
                };
                functionValue = GameThread.InvokeAsync(function);
            });

            PumpUntil(() => value!.IsCompleted && functionValue!.IsCompleted);

            Assert.Equal(7, value!.GetAwaiter().GetResult());
            Assert.Equal(8, functionValue!.GetAwaiter().GetResult());
        });
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public void InvokeAsync_FromAWorker_ReportsAFaultThroughTheTask_NotAsFatal(
        string overload)
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From the body");

            Task? task = null;
            OnOtherThread(() => task = Invoke(overload, () => throw fault));
            PumpUntil(() => task!.IsCompleted);

            Assert.True(task!.IsFaulted);
            Assert.Same(fault, task.Exception!.InnerException);
            Assert.Empty(this._fatal);
        });
    }

    [Theory]
    [MemberData(nameof(AsyncOverloads))]
    public void InvokeAsync_FromAWorker_ReportsCancellationAsCancellation(
        string overload)
    {
        OnGameThread(() =>
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            Task? task = null;
            OnOtherThread(() => task = Invoke(overload, () => source.Token.ThrowIfCancellationRequested()));
            PumpUntil(() => task!.IsCompleted);

            Assert.True(task!.IsCanceled);
            var canceled = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            Assert.Equal(source.Token, canceled.CancellationToken);
            Assert.Empty(this._fatal);
        });
    }

    [Theory]
    [InlineData("Action")]
    [InlineData("Func<T>")]
    public void InvokeAsync_FromAWorker_ReportsCancellationAsCancellation_FromASynchronousBody(
        string overload)
    {
        OnGameThread(() =>
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            Task? task = null;
            OnOtherThread(() => task = Invoke(overload, () => source.Token.ThrowIfCancellationRequested()));
            PumpUntil(() => task!.IsCompleted);

            Assert.True(task!.IsCanceled);
        });
    }

    // ── A worker's continuation stays off the game thread ──────────────

    [Theory]
    [MemberData(nameof(Overloads))]
    public void AWorkerAwaitingInvokeAsync_DoesNotContinueOnTheGameThread(
        string overload)
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int bodyRanOn = 0;
            int continuedOn = 0;
            bool continuedOnTheGameThread = true;

            // NOTE: The completion happens on the game thread, with the game's context current,
            // and the task source doesn't ask for its continuations to run asynchronously. The
            // worker's context-free continuation must still not run inline there, where it would
            // take frame time and could touch nothing it thinks it can.
            var worker = Task.Run(async () =>
            {
                await Invoke(overload, () => bodyRanOn = Environment.CurrentManagedThreadId);
                continuedOn = Environment.CurrentManagedThreadId;
                continuedOnTheGameThread = GameSynchronizationContext.IsGameThread;
            });

            PumpUntil(() => worker.IsCompleted);

            Assert.True(worker.IsCompletedSuccessfully);
            Assert.Equal(gameThread, bodyRanOn);
            Assert.NotEqual(gameThread, continuedOn);
            Assert.False(continuedOnTheGameThread);
        });
    }
}
