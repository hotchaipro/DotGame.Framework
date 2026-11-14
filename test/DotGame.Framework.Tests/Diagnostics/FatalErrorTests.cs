// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Tests.Platform;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Diagnostics;

/// <summary>
/// What every abort site reports, and what a failure the game runs on without reports. Ending the
/// process itself can't run here: <see cref="FatalError.SetUnwindForTests"/> unwinds instead, as
/// the browser does. <see cref="FatalPathTests"/> covers who calls it.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class FatalErrorTests : IDisposable
{
    private const uint Tag = 0x30000001;

    private readonly List<FatalExceptionEventArgs> _fatal = [];
    private readonly List<NonFatalExceptionEventArgs> _nonFatal = [];

    public FatalErrorTests()
    {
        FatalError.SetUnwindForTests(true);
        NonFatalError.ResetForTests();
        Trace.FatalException += this.OnFatal;
        Trace.NonFatalException += this.OnNonFatal;
    }

    public void Dispose()
    {
        Trace.FatalException -= this.OnFatal;
        Trace.NonFatalException -= this.OnNonFatal;
        FatalError.SetUnwindForTests(false);
        NonFatalError.ResetForTests();
    }

    private void OnFatal(
        object? sender,
        FatalExceptionEventArgs e)
    {
        lock (this._fatal)
        {
            this._fatal.Add(e);
        }
    }

    private void OnNonFatal(
        object? sender,
        NonFatalExceptionEventArgs e)
    {
        lock (this._nonFatal)
        {
            this._nonFatal.Add(e);
        }
    }

    // ── Fatal ──────────────────────────────────────────────────────────

    [Fact]
    public void Terminate_RaisesTheEvent_ThenUnwindsWithTheException()
    {
        var fault = new InvalidOperationException("Fatal");

        var thrown = Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, fault));

        Assert.Same(fault, thrown);
        var raised = Assert.Single(this._fatal);
        Assert.Equal(Tag, raised.Tag);
        Assert.Same(fault, raised.Exception);
    }

    [Fact]
    public void Terminate_ThroughTheHost_DoesTheSame()
    {
        var fault = new InvalidOperationException("Fatal");

        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => GameHost.Terminate(Tag, fault)));

        Assert.Same(fault, Assert.Single(this._fatal).Exception);
    }

    [Fact]
    public void Terminate_GivesANeverThrownException_ACallStack()
    {
        var fault = new InvalidOperationException("Never thrown");
        Assert.Null(fault.StackTrace);
        string? stackWhenRaised = null;

        void Capture(object? sender, FatalExceptionEventArgs e) => stackWhenRaised = e.Exception?.StackTrace;

        Trace.FatalException += Capture;
        try
        {
            Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, fault));
        }
        finally
        {
            Trace.FatalException -= Capture;
        }

        // The crash report has a stack by the time it's raised, not only once it's rethrown
        Assert.Contains(nameof(Terminate_GivesANeverThrownException_ACallStack), stackWhenRaised);
    }

    [Fact]
    public void Terminate_KeepsTheStackOfAThrownException()
    {
        InvalidOperationException fault;
        try
        {
            ThrowFromHere();
            throw new InvalidOperationException("Unreachable");
        }
        catch (InvalidOperationException e)
        {
            fault = e;
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, fault));

        Assert.Contains(nameof(ThrowFromHere), thrown.StackTrace);
    }

    private static void ThrowFromHere()
    {
        throw new InvalidOperationException("Thrown");
    }

    [Fact]
    public void Terminate_WithNoException_StillReportsAndUnwinds_NamingTheTag()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(0x12ab34cd, null));

        Assert.Contains("0x12ab34cd", thrown.Message);
        var raised = Assert.Single(this._fatal);
        Assert.Equal(0x12ab34cdu, raised.Tag);
        Assert.Null(raised.Exception);
    }

    [Fact]
    public void TheFirstFatal_IsTheOneReported()
    {
        var first = new InvalidOperationException("First");
        var second = new ArgumentException("Second");

        Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, first));

        // On the browser the unwind comes back here from the host's global error handler, and
        // mustn't replace the first, richest report; it still unwinds
        Assert.Same(second, Assert.Throws<ArgumentException>(() => FatalError.Terminate(Tag + 1, second)));

        Assert.Same(first, Assert.Single(this._fatal).Exception);
    }

    [Fact]
    public void AHandlerThatThrows_DoesNotStopTheUnwind()
    {
        var fault = new InvalidOperationException("Fatal");
        void Throwing(object? sender, FatalExceptionEventArgs e) => throw new ArgumentException("From a handler");

        Trace.FatalException += Throwing;
        try
        {
            Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, fault)));
        }
        finally
        {
            Trace.FatalException -= Throwing;
        }
    }

    [Fact]
    public void AnExceptionWithAnInnerOne_IsNotUnwrapped()
    {
        var fault = new InvalidOperationException("Outer", new ArgumentException("Inner"));

        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, fault)));

        Assert.Same(fault, Assert.Single(this._fatal).Exception);
    }

    [Fact]
    public void SetUnwindForTests_ArmsItAgain()
    {
        Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, new InvalidOperationException("First")));

        FatalError.SetUnwindForTests(true);
        Assert.Throws<InvalidOperationException>(() => FatalError.Terminate(Tag, new InvalidOperationException("Second")));

        Assert.Equal(2, this._fatal.Count);
    }

    // ── Non-fatal ──────────────────────────────────────────────────────

    [Fact]
    public void NonFatal_RaisesTheEvent_AndDoesNotThrow()
    {
        var fault = new InvalidOperationException("Non-fatal");

        NonFatalError.Report(Tag, fault);

        var raised = Assert.Single(this._nonFatal);
        Assert.Equal(Tag, raised.Tag);
        Assert.Same(fault, raised.Exception);
        Assert.Empty(this._fatal);
    }

    [Fact]
    public void NonFatal_EachSiteReportsOnce()
    {
        NonFatalError.Report(Tag, new InvalidOperationException("One"));
        NonFatalError.Report(Tag, new InvalidOperationException("Again"));
        NonFatalError.Report(Tag + 1, new InvalidOperationException("Elsewhere"));

        Assert.Equal(2, this._nonFatal.Count);
        Assert.Equal(Tag, this._nonFatal[0].Tag);
        Assert.Equal(Tag + 1, this._nonFatal[1].Tag);
    }

    [Fact]
    public void NonFatal_ThroughTheHost_DoesTheSame()
    {
        var fault = new InvalidOperationException("Non-fatal");

        GameHost.ReportNonFatal(Tag, fault);

        Assert.Same(fault, Assert.Single(this._nonFatal).Exception);
    }

    [Fact]
    public void NonFatal_AHandlerThatThrows_IsNotFatal()
    {
        void Throwing(object? sender, NonFatalExceptionEventArgs e) => throw new ArgumentException("From a handler");

        Trace.NonFatalException += Throwing;
        try
        {
            NonFatalError.Report(Tag, new InvalidOperationException("Non-fatal"));
        }
        finally
        {
            Trace.NonFatalException -= Throwing;
        }

        Assert.Empty(this._fatal);
    }

    [Fact]
    public void NonFatal_FromAnyThread_ReportsASiteOnce()
    {
        const int Threads = 8;

        var threads = new Thread[Threads];
        for (int i = 0; i < Threads; i++)
        {
            threads[i] = new Thread(() => NonFatalError.Report(Tag, new InvalidOperationException("Non-fatal")));
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Single(this._nonFatal);
    }

    [Fact]
    public void ResetForTests_ForgetsTheSites()
    {
        NonFatalError.Report(Tag, new InvalidOperationException("One"));

        NonFatalError.ResetForTests();
        NonFatalError.Report(Tag, new InvalidOperationException("Two"));

        Assert.Equal(2, this._nonFatal.Count);
    }

#if DEBUG
    [Fact]
    public void AnAudioFailure_EndsADebugBuild()
    {
        var fault = new InvalidOperationException("Bad track");

        OnOtherThread(() => Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => GameHost.ReportAudioFailure(Tag, fault))));

        Assert.Same(fault, Assert.Single(this._fatal).Exception);
        Assert.Empty(this._nonFatal);
    }
#else
    [Fact]
    public void AnAudioFailure_IsReported_AndTheGameRunsOn()
    {
        var fault = new InvalidOperationException("Bad track");

        OnOtherThread(() => GameHost.ReportAudioFailure(Tag, fault));

        Assert.Same(fault, Assert.Single(this._nonFatal).Exception);
        Assert.Empty(this._fatal);
    }
#endif
}
