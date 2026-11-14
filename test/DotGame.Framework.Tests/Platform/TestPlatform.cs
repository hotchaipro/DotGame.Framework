// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.ExceptionServices;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// A platform for <see cref="GameHost"/> tests: services that do nothing, and a game thread to
/// run each test on.
/// </summary>
internal static class TestPlatform
{
    /// <summary>
    /// Runs <paramref name="test"/> on a new game thread, as a platform's run loop would. The game
    /// thread is one per process, so tests that use it must not run in parallel.
    /// </summary>
    public static void OnGameThread(
        Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            GameHost.InitializeGameThread();
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                GameHost.ShutdownGameThread();
            }
        });

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a new thread that isn't the game thread, and waits for it.
    /// </summary>
    public static void OnOtherThread(
        Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Pumps the game thread until <paramref name="condition"/> holds, for work on another thread
    /// that needs the pump. Fails, rather than hanging, if it never does.
    /// </summary>
    public static void PumpUntil(
        Func<bool> condition)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The condition never held.");
            }

            GameHost.DrainContinuations();
            Thread.Yield();
        }
    }

    public static Strategy<IContentServiceStrategy> CreateContentStrategy(
        ContentManifest? manifest,
        Func<string, Task<Stream>>? openStream = null)
    {
        return new Strategy<IContentServiceStrategy>(new TestContentService(manifest, openStream));
    }

    public static PlatformServices CreateServices()
    {
        return new PlatformServices(
            new Strategy<IContentServiceStrategy>(new TestContentService(new ContentManifest("tests"), null)),
            new Strategy<IGraphicsServiceStrategy>(new TestGraphicsService()),
            new Strategy<IAudioServiceStrategy>(new TestAudioService()),
            new Strategy<IInputServiceStrategy>(new TestInputService()));
    }

    /// <summary>
    /// A frame timestamp, <paramref name="milliseconds"/> into the test.
    /// </summary>
    public static TimeSpan At(
        double milliseconds)
    {
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class TestContentService(
        ContentManifest? manifest,
        Func<string, Task<Stream>>? openStream) : IContentServiceStrategy
    {
        public ContentManifest Manifest { get; } = manifest!;

        public Task<Stream> OpenStreamAsync(string contentPath) => (openStream ?? throw new NotSupportedException())(contentPath);

        public IContentLoader<Texture> TextureLoader { get; } = new TestLoader<Texture>();

        public IContentLoader<Shader> ShaderLoader { get; } = new TestLoader<Shader>();

        public IContentLoader<Sound> SoundLoader { get; } = new TestLoader<Sound>();

        public IContentLoader<Music> MusicLoader { get; } = new TestLoader<Music>();
    }

    private sealed class TestLoader<TContent> : IContentLoader<TContent>
        where TContent : class, IContent
    {
        public Task<TContent> LoadAsync(string contentPath) => throw new NotSupportedException();
    }

    private sealed class TestGraphicsService : IGraphicsServiceStrategy
    {
        public int MultisampleCount => 1;

        public int? PreferredFrameRate { get; set; }

        public Task OnInitialize() => Task.CompletedTask;

        public Strategy<ISpriteBatchStrategy> CreateSpriteBatch() => new(new TestSpriteBatch());

        public Strategy<ITextureStrategy> CreateTexture(Vector2Int size, ReadOnlySpan<Color32> data) => throw new NotSupportedException();

        public Strategy<ITextureStrategy> CreateRenderTexture(Vector2Int size) => throw new NotSupportedException();

        public void SetRenderTarget(Strategy<ITextureStrategy>? textureStrategy)
        {
        }

        public Task ReadPixelsAsync(Strategy<ITextureStrategy> textureStrategy, Memory<Color32> destination) => throw new NotSupportedException();

        public void Clear(Color color)
        {
        }

        public void SetScissorRectangle(RectangleInt? rectangle)
        {
        }
    }

    private sealed class TestSpriteBatch : ISpriteBatchStrategy
    {
        public int TriangleCount { get; private set; }

        public void DrawTriangle(Strategy<ITextureStrategy> textureStrategy, in DotGame.Framework.Graphics.Vertex a, in DotGame.Framework.Graphics.Vertex b, in DotGame.Framework.Graphics.Vertex c) => this.TriangleCount++;

        public void Begin(Matrix4x4? transformMatrix = null, BlendMode blend = BlendMode.Alpha, Sampler sampler = Sampler.LinearClamp)
        {
        }

        public void SetShader(Strategy<IShaderStrategy>? shader)
        {
        }

        public void Draw(Strategy<ITextureStrategy> textureStrategy, Vector2 position, Color color, Angle rotation, Vector2 origin, Vector2 scale, Rectangle? sourceRectangle, SpriteFlip flip, in ShaderCustomData customData)
        {
        }

        public void End()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestAudioService : IAudioServiceStrategy
    {
        public Task OnInitialize() => Task.CompletedTask;

        public Strategy<IVolumeControlStrategy> MasterVolumeControl => throw new NotSupportedException();

        public Strategy<IVolumeControlStrategy> CreateVolumeControl() => throw new NotSupportedException();

        public Strategy<IVoiceStrategy> CreateVoice(Strategy<ISoundStrategy> sound, TimeSpan offset, TimeSpan duration) => throw new NotSupportedException();

        public bool ShouldPlayMusic() => true;
    }

    private sealed class TestInputService : IInputServiceStrategy
    {
        public Task OnInitialize() => Task.CompletedTask;

        public ITextInputHandle CreateTextInputHandle(ITextInputSink sink) => throw new NotSupportedException();
    }
}

/// <summary>
/// A game whose hooks do what a test says.
/// </summary>
internal sealed class ScriptedGame : Game
{
    public Func<ScriptedGame, Task>? Initialize { get; init; }

    public Func<ScriptedGame, Task>? Start { get; init; }

    public Func<Task>? Pause { get; init; }

    public Func<Task>? Stop { get; init; }

    public Action? Update { get; init; }

    public Action? Draw { get; init; }

    public Action<PointerEvent>? Pointer { get; init; }

    public Action<KeyEvent>? Key { get; init; }

    public Action? BoundsChanged { get; init; }

    public bool IsActiveNow => this.IsActive;

    protected override Task OnInitialize() => this.Initialize?.Invoke(this) ?? Task.CompletedTask;

    protected override Task OnStart() => this.Start?.Invoke(this) ?? Task.CompletedTask;

    protected override Task OnPause() => this.Pause?.Invoke() ?? Task.CompletedTask;

    protected override Task OnStop() => this.Stop?.Invoke() ?? Task.CompletedTask;

    protected override void OnUpdate() => this.Update?.Invoke();

    protected override void OnDraw() => this.Draw?.Invoke();

    protected override void OnPointerEvent(
        PointerEvent pointerEvent)
    {
        this.Pointer?.Invoke(pointerEvent);
    }

    protected override void OnKeyEvent(
        KeyEvent keyEvent)
    {
        this.Key?.Invoke(keyEvent);
    }

    protected override void OnClientBoundsChanged() => this.BoundsChanged?.Invoke();
}

/// <summary>
/// A game that records its hooks, and whose <c>OnStart</c> waits until the test lets it finish.
/// </summary>
internal sealed class TestGame : Game
{
    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Calls { get; } = [];

    public int PointerEvents => this.PointerEventLog.Count;

    public List<PointerEvent> PointerEventLog { get; } = [];

    public int BoundsChanges { get; private set; }

    public bool IsActiveNow => this.IsActive;

    /// <summary>
    /// Lets <c>OnStart</c> finish; its continuation runs at the next pump of the game thread.
    /// </summary>
    public void FinishStart()
    {
        this._startGate.TrySetResult();
    }

    protected override Task OnInitialize()
    {
        this.Calls.Add("Initialize");
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        this.Calls.Add("Start");
        await this._startGate.Task;
        this.Calls.Add("Started");
    }

    protected override Task OnStop()
    {
        this.Calls.Add("Stop");
        return Task.CompletedTask;
    }

    protected override Task OnPause()
    {
        this.Calls.Add("Pause");
        return Task.CompletedTask;
    }

    protected override Task OnResume()
    {
        this.Calls.Add("Resume");
        return Task.CompletedTask;
    }

    protected override void OnFixedUpdate()
    {
        this.Calls.Add("FixedUpdate");
    }

    protected override void OnUpdate()
    {
        this.Calls.Add("Update");
    }

    protected override void OnDraw()
    {
        this.Calls.Add("Draw");
    }

    protected override void OnPointerEvent(
        PointerEvent pointerEvent)
    {
        this.PointerEventLog.Add(pointerEvent);
    }

    protected override void OnClientBoundsChanged()
    {
        this.BoundsChanges++;
    }
}
