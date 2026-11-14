// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Tests.Platform;
using DotGame.Framework.Threading;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;
using Trace = DotGame.Framework.Diagnostics.Trace;

namespace DotGame.Framework.Tests.Threading;

/// <summary>
/// The queue every await on the game thread goes through: when it runs, what stops it, where its
/// faults go, and what's left of it after a session.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class GameSynchronizationContextTests : IDisposable
{
    private readonly List<Exception?> _fatal = [];

    public GameSynchronizationContextTests()
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

    private static GameSynchronizationContext Context => GameSynchronizationContext.GameThread;

    // ── The pump ───────────────────────────────────────────────────────

    [Fact]
    public void Post_WaitsForThePump_ThenRunsInOrder()
    {
        OnGameThread(() =>
        {
            var ran = new List<int>();

            Context.Post(_ => ran.Add(1), null);
            Context.Post(_ => ran.Add(2), null);
            Context.Post(_ => ran.Add(3), null);
            Assert.Empty(ran);

            GameHost.DrainContinuations();

            Assert.Equal([1, 2, 3], ran);
        });
    }

    [Fact]
    public void Post_PassesItsState()
    {
        OnGameThread(() =>
        {
            object state = new();
            object? seen = null;

            Context.Post(s => seen = s, state);
            GameHost.DrainContinuations();

            Assert.Same(state, seen);
        });
    }

    [Fact]
    public void BudgetedPump_RunsAtLeastOne_ThenLeavesTheRestForTheNextFrame()
    {
        OnGameThread(() =>
        {
            var ran = new List<int>();

            // Longer than the largest budget there is, 8 ms
            Context.Post(_ => { Spin(TimeSpan.FromMilliseconds(12)); ran.Add(1); }, null);
            Context.Post(_ => ran.Add(2), null);

            Context.ExecutePendingContinuations();
            Assert.Equal([1], ran);

            Context.ExecutePendingContinuations();
            Assert.Equal([1, 2], ran);
        });
    }

    [Fact]
    public void BudgetedPump_WithNothingQueued_DoesNothing()
    {
        OnGameThread(() =>
        {
            Context.ExecutePendingContinuations();
            Context.ExecutePendingContinuations();
        });
    }

    [Fact]
    public void Drain_IgnoresTheBudget()
    {
        OnGameThread(() =>
        {
            var ran = new List<int>();

            Context.Post(_ => { Spin(TimeSpan.FromMilliseconds(12)); ran.Add(1); }, null);
            Context.Post(_ => { Spin(TimeSpan.FromMilliseconds(12)); ran.Add(2); }, null);
            Context.Post(_ => ran.Add(3), null);

            GameHost.DrainContinuations();

            Assert.Equal([1, 2, 3], ran);
        });
    }

    [Fact]
    public void WorkPostedDuringAPump_RunsNoLaterThanTheNextPump()
    {
        OnGameThread(() =>
        {
            bool ranInner = false;

            Context.Post(_ => Context.Post(_ => ranInner = true, null), null);

            GameHost.DrainContinuations();
            GameHost.DrainContinuations();

            Assert.True(ranInner);
        });
    }

    [Fact]
    public void BudgetedPump_DoesNotRunWhatIsPostedDuringIt()
    {
        OnGameThread(() =>
        {
            int runs = 0;

            void Repost(object? state)
            {
                if (++runs < 100_000)
                {
                    Context.Post(Repost, null);
                }
            }

            Context.Post(Repost, null);
            Context.ExecutePendingContinuations();

            Assert.Equal(1, runs);
        });
    }

    [Fact]
    public void Drain_ReturnsEvenIfAContinuationKeepsPostingItself()
    {
        OnGameThread(() =>
        {
            const int GivesUpAfter = 20_000_000;
            int runs = 0;

            void Repost(object? state)
            {
                if (++runs < GivesUpAfter)
                {
                    Context.Post(Repost, null);
                }
            }

            Context.Post(Repost, null);
            GameHost.DrainContinuations();

            Assert.True(runs < GivesUpAfter);
        });
    }

    // ── Awaiting ───────────────────────────────────────────────────────

    [Fact]
    public void Await_ResumesOnTheGameThread_AfterWorkOnAnother()
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int workedOn = 0;
            int resumedOn = 0;

            async Task Body()
            {
                await Task.Run(() => workedOn = Environment.CurrentManagedThreadId);
                resumedOn = Environment.CurrentManagedThreadId;
            }

            var body = Body();
            PumpUntil(() => body.IsCompleted);

            Assert.NotEqual(gameThread, workedOn);
            Assert.Equal(gameThread, resumedOn);
        });
    }

    [Fact]
    public void TaskYield_ResumesAtThePump_NotInline()
    {
        OnGameThread(() =>
        {
            bool resumed = false;

            async Task Body()
            {
                await Task.Yield();
                resumed = true;
            }

            var body = Body();
            Assert.False(resumed);

            GameHost.DrainContinuations();

            Assert.True(resumed);
            Assert.True(body.IsCompletedSuccessfully);
        });
    }

    // ── Faults ─────────────────────────────────────────────────────────

    [Fact]
    public void AContinuationThatThrows_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From a continuation");

            Context.Post(_ => throw fault, null);

            var thrown = Assert.Throws<InvalidOperationException>(() => GameHost.DrainContinuations());
            Assert.Same(fault, thrown);
            Assert.Equal([fault], this._fatal);
        });
    }

    [Fact]
    public void AnAsyncVoidMethodThatFaults_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From an async void handler");

            async void Handler()
            {
                await Task.Yield();
                throw fault;
            }

            Handler();

            var thrown = Assert.Throws<InvalidOperationException>(() => PumpUntil(() => false));
            Assert.Same(fault, thrown);
            Assert.Equal([fault], this._fatal);
        });
    }

    [Fact]
    public void ACanceledContinuation_IsNotFatal_AndTheQueueContinues()
    {
        OnGameThread(() =>
        {
            bool ranNext = false;

            Context.Post(_ => throw new OperationCanceledException(), null);
            Context.Post(_ => throw new TaskCanceledException(), null);
            Context.Post(_ => ranNext = true, null);

            GameHost.DrainContinuations();

            Assert.True(ranNext);
            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void AnAsyncVoidMethodThatIsCanceled_IsNotFatal()
    {
        OnGameThread(() =>
        {
            async void Handler()
            {
                await Task.Yield();
                throw new OperationCanceledException();
            }

            Handler();

            GameHost.DrainContinuations();
            GameHost.DrainContinuations();

            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void Post_AndSend_RefuseANullCallback()
    {
        Assert.Throws<ArgumentNullException>(() => Context.Post(null!, null));
        Assert.Throws<ArgumentNullException>(() => Context.Send(null!, null));
    }

    // ── Send ───────────────────────────────────────────────────────────

    [Fact]
    public void Send_OnTheGameThread_RunsInline()
    {
        OnGameThread(() =>
        {
            bool ran = false;

            Context.Send(_ => ran = true, null);

            Assert.True(ran);
        });
    }

    [Fact]
    public void Send_FromAWorker_RunsOnTheGameThread_AtThePump()
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int ranOn = 0;

            var worker = Task.Run(() => Context.Send(_ => ranOn = Environment.CurrentManagedThreadId, null));
            PumpUntil(() => worker.IsCompleted);

            Assert.True(worker.IsCompletedSuccessfully);
            Assert.Equal(gameThread, ranOn);
        });
    }

    [Fact]
    public void Send_FromAWorker_ReportsAFaultToTheSender_NotAsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From a sent callback");
            Exception? seen = null;

            var worker = Task.Run(() =>
            {
                try
                {
                    Context.Send(_ => throw fault, null);
                }
                catch (Exception e)
                {
                    seen = e;
                }
            });
            PumpUntil(() => worker.IsCompleted);

            Assert.Same(fault, (seen as AggregateException)?.InnerException ?? seen);
            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void Send_FromAWorker_RaisesWorkPosted()
    {
        OnGameThread(() =>
        {
            int posted = 0;
            GameHost.WorkPosted = () => Interlocked.Increment(ref posted);
            try
            {
                var worker = Task.Run(() => Context.Send(_ => { }, null));
                PumpUntil(() => worker.IsCompleted);

                Assert.Equal(1, posted);
            }
            finally
            {
                GameHost.WorkPosted = null;
            }
        });
    }

    // ── WorkPosted ─────────────────────────────────────────────────────

    [Fact]
    public void Post_RaisesWorkPosted_OnThePostingThread()
    {
        OnGameThread(() =>
        {
            int raisedOn = 0;
            GameHost.WorkPosted = () => raisedOn = Environment.CurrentManagedThreadId;
            try
            {
                int workerId = 0;
                OnOtherThread(() =>
                {
                    workerId = Environment.CurrentManagedThreadId;
                    Context.Post(_ => { }, null);
                });

                Assert.Equal(workerId, raisedOn);
            }
            finally
            {
                GameHost.WorkPosted = null;
            }
        });
    }

    // ── The game thread ────────────────────────────────────────────────

    [Fact]
    public void IsGameThread_IsTrueOnlyOnTheGameThread_ForItsSession()
    {
        Assert.False(GameSynchronizationContext.IsGameThread);

        OnGameThread(() =>
        {
            Assert.True(GameSynchronizationContext.IsGameThread);
            Assert.Same(Context, SynchronizationContext.Current);

            OnOtherThread(() => Assert.False(GameSynchronizationContext.IsGameThread));
        });

        Assert.False(GameSynchronizationContext.IsGameThread);
    }

    [Fact]
    public void AfterShutdown_TheThreadIsNoLongerTheGameThread()
    {
        OnOtherThread(() =>
        {
            GameHost.InitializeGameThread();
            GameHost.ShutdownGameThread();

            Assert.False(GameSynchronizationContext.IsGameThread);
            Assert.Null(SynchronizationContext.Current);
        });
    }

    [Fact]
    public void VerifyCurrent_PassesOnTheGameThread()
    {
        OnGameThread(() =>
        {
            GameThread.VerifyCurrent(0x10000001);

            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void VerifyCurrent_IsFatalOnAnotherThread()
    {
        OnOtherThread(() =>
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => GameThread.VerifyCurrent(0x10000002));

            Assert.Contains("10000002", thrown.Message);
            Assert.Equal([thrown], this._fatal);
        });
    }

    [Fact]
    public void VerifyCurrent_IsFatalOnTheGameThread_WithoutItsContext()
    {
        OnGameThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);

            Assert.Throws<InvalidOperationException>(() => GameThread.VerifyCurrent(0x10000003));

            Assert.Single(this._fatal);
        });
    }

    [Fact]
    public void EnsureContext_RestoresWhatTheBrowserRuntimeClears()
    {
        OnGameThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);

            GameHost.EnsureGameThreadContext();

            Assert.Same(Context, SynchronizationContext.Current);
            GameThread.VerifyCurrent(0x10000004);
        });
    }

    [Fact]
    public void EnsureContext_IsFatalOnAnotherThread()
    {
        OnOtherThread(() =>
        {
            Assert.Throws<InvalidOperationException>(() => GameHost.EnsureGameThreadContext());

            Assert.Single(this._fatal);
        });
    }

    [Fact]
    public void Pump_RestoresTheContext_WhenWorkLosesIt()
    {
        OnGameThread(() =>
        {
            var seen = new List<SynchronizationContext?>();
            Context.Post(_ => SynchronizationContext.SetSynchronizationContext(null), null);
            Context.Post(_ => seen.Add(SynchronizationContext.Current), null);

            Context.ExecutePendingContinuations();
            Context.ExecutePendingContinuations();

            Assert.Equal([Context], seen);
            Assert.Same(Context, SynchronizationContext.Current);
            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void CreateCopy_IsTheSameContext()
    {
        Assert.Same(Context, Context.CreateCopy());
    }

    // ── Between sessions ───────────────────────────────────────────────

    [Fact]
    public void Shutdown_DiscardsWhatIsQueued_SoTheNextSessionDoesNotRunIt()
    {
        bool ran = false;

        OnGameThread(() => Context.Post(_ => ran = true, null));

        OnGameThread(() =>
        {
            GameHost.DrainContinuations();

            Assert.False(ran);
        });
    }

    [Fact]
    public void AnotherSession_CanTakeOverOnANewThread()
    {
        int first = 0;
        int second = 0;

        OnGameThread(() =>
        {
            first = Environment.CurrentManagedThreadId;
            Assert.True(GameSynchronizationContext.IsGameThread);
        });

        OnGameThread(() =>
        {
            second = Environment.CurrentManagedThreadId;
            Assert.True(GameSynchronizationContext.IsGameThread);
        });

        Assert.NotEqual(first, second);
        Assert.Empty(this._fatal);
    }

    [Fact]
    public void Initialize_WaitsForTheOutgoingSession_ToHandOver()
    {
        // An Android relaunch can start its new render thread before the old one has finished
        using var outgoingInitialized = new ManualResetEventSlim();
        using var incomingStarted = new ManualResetEventSlim();
        bool incomingBecameGameThread = false;

        var outgoing = new Thread(() =>
        {
            GameHost.InitializeGameThread();
            outgoingInitialized.Set();
            incomingStarted.Wait();
            GameHost.ShutdownGameThread();
        });

        var incoming = new Thread(() =>
        {
            outgoingInitialized.Wait();
            incomingStarted.Set();
            GameHost.InitializeGameThread();
            incomingBecameGameThread = GameSynchronizationContext.IsGameThread;
            GameHost.ShutdownGameThread();
        });

        outgoing.Start();
        incoming.Start();
        outgoing.Join();
        incoming.Join();

        Assert.True(incomingBecameGameThread);
        Assert.Empty(this._fatal);
    }

    [Fact]
    public void APostAfterShutdown_DoesNotRunInTheNextSession()
    {
        bool ran = false;

        OnGameThread(() => { });
        OnOtherThread(() => Context.Post(_ => ran = true, null));

        OnGameThread(() =>
        {
            GameHost.DrainContinuations();

            Assert.False(ran);
        });
    }

    [Fact]
    public void Shutdown_LeavesNoTaskWaitingForever()
    {
        Task? pending = null;

        OnGameThread(() =>
        {
            OnOtherThread(() => pending = GameThread.InvokeAsync(() => { }));
        });

        Assert.True(pending!.IsCompleted);
    }

    private static void Spin(
        TimeSpan duration)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < duration)
        {
            Thread.SpinWait(100);
        }
    }
}
