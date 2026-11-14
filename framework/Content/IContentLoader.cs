// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace DotGame.Framework.Content;

/// <summary>
/// Non-generic content loader entry point. See
/// <see cref="IContentLoader{TContent}"/> for the threading contract.
/// </summary>
public interface IContentLoader
{
    /// <summary>Loads the content with a key.</summary>
    /// <param name="contentKey">The content's key: its path under <c>Content/</c>, with no extension.</param>
    Task<Object> LoadAsync(string contentKey);
}

/// <summary>
/// Loads a piece of content of type <typeparamref name="TContent"/> from
/// the configured content source.
/// </summary>
/// <remarks>
/// <para><b>Threading contract:</b></para>
/// <list type="bullet">
/// <item><description>
/// <see cref="LoadAsync(string)"/> MAY be invoked on the game thread or on a
/// worker pool thread. Implementations must not assume either. While the
/// game starts, <c>Content.LoadAsync</c> runs loaders on the game thread; after
/// that it runs them on worker threads, except on the web, which has only
/// the one thread.
/// </description></item>
/// <item><description>
/// Decode/parse work runs inline on the calling thread. Implementations
/// SHOULD NOT wrap their decode in <c>Task.Run</c>: <c>Content.LoadAsync</c> has
/// already chosen the thread, and on single-threaded WASM it would only add
/// continuations.
/// </description></item>
/// <item><description>
/// Implementations MUST execute engine-restricted work on the game thread.
/// This includes GPU resource creation and upload, shader compilation on
/// platforms that require the device thread, and audio engine
/// attach/connect. Use
/// <c>GameThread.InvokeAsync(...)</c> — it short-circuits to a
/// synchronous direct call when already on the game thread.
/// </description></item>
/// <item><description>
/// <c>.ConfigureAwait(false)</c> SHOULD be used on awaited I/O calls so
/// continuations don't bounce back to the game thread when the loader is
/// running on a worker thread. It is harmless when the loader runs on the
/// game thread (continuations stay inline).
/// </description></item>
/// <item><description>
/// The returned <see cref="Task{TContent}"/> may complete on any thread.
/// Callers that require the game thread observe it by awaiting from the
/// game thread; the game synchronization context handles the hop.
/// </description></item>
/// </list>
/// <para>See <c>docs/architecture/threading.md</c> for the full threading model.</para>
/// </remarks>
public interface IContentLoader<TContent> : IContentLoader
    where TContent : class, IContent
{
    /// <summary>Loads the content with a key, as its type.</summary>
    /// <param name="contentKey">The content's key: its path under <c>Content/</c>, with no extension.</param>
    new Task<TContent> LoadAsync(string contentKey);
    async Task<Object> IContentLoader.LoadAsync(string contentKey) => await this.LoadAsync(contentKey).ConfigureAwait(false);
}
