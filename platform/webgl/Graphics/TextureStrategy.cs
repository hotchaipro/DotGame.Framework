// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed class TextureStrategy : ITextureStrategy, IDisposable
{
    private JSObject? _nativeHandle;

    internal TextureStrategy(
        JSObject nativeHandle,
        Vector2Int size)
    {
        this._nativeHandle = nativeHandle;
        this.Size = size;
    }

    ~TextureStrategy()
    {
        this.Dispose(false);
    }

    internal JSObject NativeHandle => this._nativeHandle ?? throw new ArgumentNullException(nameof(this.NativeHandle));

    public Vector2Int Size { get; }

    // The service that made it from pixels; other textures can't have them replaced
    internal GraphicsServiceStrategy? Owner { get; init; }

    public void SetData(
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
        if (this.Owner is null)
        {
            throw new InvalidOperationException("Only a texture you created with CreateTexture can have its pixels replaced.");
        }

        this.Owner.UpdateTexture(this, region, data);
    }

    /// <summary>
    /// Releases the native handle without calling the WebGL delete function.
    /// Used during context restore when GL objects have already been
    /// invalidated by the browser.
    /// </summary>
    internal void Invalidate()
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle is not null)
        {
            this._nativeHandle = null;
            nativeHandle.Dispose();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    /// <remarks>
    /// <para>
    /// The <paramref name="disposing"/> flag must be honored here, and the
    /// finalizer path must not touch the browser at all. <see cref="JSObject"/>
    /// is itself a finalizable managed object: once the finalizer queue is
    /// running there is no ordering guarantee between it and this type, so the
    /// proxy may already have released its JS-side slot. Passing a released
    /// proxy to a <c>[JSImport]</c> target throws, and an exception escaping a
    /// finalizer tears down the whole runtime — which on the browser means the
    /// game disappears behind a crash dialog for what is only a missed cleanup.
    /// </para>
    /// <para>
    /// Skipping the delete is the lesser cost. The texture stays resident until
    /// the WebGL context goes away, but the proxy slot is still reclaimed by
    /// <see cref="JSObject"/>'s own finalizer, and the browser frees every GL
    /// object with the context. The real defect is the missing
    /// <see cref="Dispose()"/> call, so the finalizer reports it instead of
    /// papering over it.
    /// </para>
    /// </remarks>
    private void Dispose(
        bool disposing)
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle is null)
        {
            return;
        }

        this._nativeHandle = null;

        if (!disposing)
        {
            Trace.Write(0x9c41d7a0, $"[TextureStrategy] Finalized without Dispose; GL texture {this.Size.X}x{this.Size.Y} left to the context");
            return;
        }

        WebGLBindings.UnloadTexture2D(nativeHandle);

        nativeHandle.Dispose();
    }
}
