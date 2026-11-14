// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Graphics;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;
using Trace = DotGame.Framework.Diagnostics.Trace;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// A fault in game code takes one path on every platform: <see cref="Trace.FatalException"/>, then
/// the end of the process (here, an unwind).
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class FatalPathTests : IDisposable
{
    private readonly List<Exception?> _fatal = [];

    public FatalPathTests()
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
        this._fatal.Add(e.Exception);
    }

    [Fact]
    public void ThrowFromUpdate_IsFatal()
    {
        Assert.SkipWhen(Debugger.IsAttached, "Left unhandled for the debugger");

        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromUpdate = true };
            var host = Started(game);

            var thrown = Assert.Throws<InvalidOperationException>(() => host.Tick(At(16), canDraw: true));

            Assert.Same(game.Fault, thrown);
            Assert.Equal([game.Fault], this._fatal);
        });
    }

    [Fact]
    public void ThrowFromDraw_IsFatal()
    {
        Assert.SkipWhen(Debugger.IsAttached, "Left unhandled for the debugger");

        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromDraw = true };
            var host = Started(game);

            Assert.Throws<InvalidOperationException>(() => host.Tick(At(16), canDraw: true));

            Assert.Equal([game.Fault], this._fatal);
        });
    }

    [Fact]
    public void ThrowFromClientBoundsChanged_IsFatal()
    {
        Assert.SkipWhen(Debugger.IsAttached, "Left unhandled for the debugger");

        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromBoundsChanged = true };
            var host = Started(game);
            var display = new DisplayInfo(new RectangleInt(0, 0, 800, 600), new RectangleInt(0, 0, 800, 600), 144f, 120);

            Assert.Throws<InvalidOperationException>(() => host.SetDisplay(display));

            Assert.Equal([game.Fault], this._fatal);
        });
    }

    [Fact]
    public void PauseHookThatThrows_FaultsTheTask()
    {
        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromPause = true };
            var host = Started(game);

            var task = host.PauseAsync();

            Assert.True(task.IsFaulted);
            Assert.Same(game.Fault, task.Exception!.InnerException);
            Assert.Empty(this._fatal);
        });
    }

    [Fact]
    public void ResumeHookThatThrows_FaultsTheTask()
    {
        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromResume = true };
            var host = Started(game);
            Assert.True(host.PauseAsync().IsCompletedSuccessfully);

            var task = host.ResumeAsync();

            Assert.True(task.IsFaulted);
            Assert.Same(game.Fault, task.Exception!.InnerException);
        });
    }

    [Fact]
    public void StopHookThatThrows_FaultsTheTask()
    {
        OnGameThread(() =>
        {
            var game = new FaultingGame { ThrowFromStop = true };
            var host = Started(game);

            var task = host.StopAsync();

            Assert.True(task.IsFaulted);
            Assert.Same(game.Fault, task.Exception!.InnerException);
        });
    }

    [Fact]
    public void SafeAsync_FunctionThatThrowsBeforeAnAwait_IsFatal()
    {
        var fault = new InvalidOperationException("Before the first await");

        SafeAsync.Invoke(0x11111111, () => throw fault);

        Assert.Equal([fault], this._fatal);
    }

    [Fact]
    public void SafeAsync_Action_FunctionThatThrowsBeforeAnAwait_IsFatal()
    {
        var fault = new InvalidOperationException("Before the first await");
        var action = SafeAsync.Action(0x22222222, () => throw fault);

        action();

        Assert.Equal([fault], this._fatal);
    }

    [Fact]
    public void SafeAsync_Cancellation_IsNotFatal()
    {
        SafeAsync.Invoke(0x33333333, () => throw new OperationCanceledException());

        Assert.Empty(this._fatal);
    }

    private static GameHost Started(
        FaultingGame game)
    {
        var host = new GameHost(game);
        host.BeginStartup(CreateServices());
        for (int i = 0; (i < 10) && !host.IsStarted; i++)
        {
            host.Tick(At(0), canDraw: true);
        }

        Assert.True(host.IsStarted);
        return host;
    }

    private sealed class FaultingGame : Game
    {
        public InvalidOperationException Fault { get; } = new("From the game");

        public bool ThrowFromUpdate { get; init; }

        public bool ThrowFromDraw { get; init; }

        public bool ThrowFromBoundsChanged { get; init; }

        public bool ThrowFromPause { get; init; }

        public bool ThrowFromResume { get; init; }

        public bool ThrowFromStop { get; init; }

        protected override void OnUpdate()
        {
            if (this.ThrowFromUpdate)
            {
                throw this.Fault;
            }
        }

        protected override void OnDraw()
        {
            if (this.ThrowFromDraw)
            {
                throw this.Fault;
            }
        }

        protected override void OnClientBoundsChanged()
        {
            if (this.ThrowFromBoundsChanged)
            {
                throw this.Fault;
            }
        }

        protected override Task OnPause()
        {
            return this.ThrowFromPause ? throw this.Fault : Task.CompletedTask;
        }

        protected override Task OnResume()
        {
            return this.ThrowFromResume ? throw this.Fault : Task.CompletedTask;
        }

        protected override Task OnStop()
        {
            return this.ThrowFromStop ? throw this.Fault : Task.CompletedTask;
        }
    }
}
