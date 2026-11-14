// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotGame.Framework.Android.Content;

/// <summary>
/// A read-only <see cref="Stream"/> over an Android asset, backed by
/// the NDK AAsset API. Avoids copying the entire asset into a
/// <see cref="MemoryStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// When opened with <see cref="AASSET_MODE_RANDOM"/>, the stream is
/// seekable and supports random access. For assets that are stored
/// uncompressed in the APK (e.g. <c>.ogg</c>, <c>.wav</c>,
/// <c>.webp</c>), this provides zero-copy access directly from the
/// APK file. For compressed assets, the NDK decompresses into an
/// internal buffer — equivalent to a <see cref="MemoryStream"/> copy.
/// </para>
/// <para>
/// When opened with <see cref="AASSET_MODE_STREAMING"/>, the stream
/// is forward-only and optimized for sequential reads.
/// </para>
/// </remarks>
internal sealed partial class AssetStream : Stream
{
    private nint _asset;
    private readonly bool _canSeek;

    internal AssetStream(
        nint assetManager,
        string assetPath,
        int accessMode = AASSET_MODE_RANDOM)
    {
        if (assetManager == nint.Zero)
        {
            throw new ArgumentException(
                "Native AAssetManager pointer is null.", nameof(assetManager));
        }

        ArgumentNullException.ThrowIfNull(assetPath);

        int byteCount = Encoding.UTF8.GetByteCount(assetPath);
        Span<byte> utf8Path = stackalloc byte[byteCount + 1];
        Encoding.UTF8.GetBytes(assetPath, utf8Path);
        utf8Path[byteCount] = 0; // null-terminate

        nint asset;
        unsafe
        {
            byte* ptr = (byte*)System.Runtime.CompilerServices.Unsafe
                .AsPointer(ref MemoryMarshal.GetReference(utf8Path));
            asset = AAssetManager_open(
                assetManager, (nint)ptr, accessMode);
        }

        if (asset == nint.Zero)
        {
            throw new FileNotFoundException(
                $"Asset not found: '{assetPath}'");
        }

        this._asset = asset;
        this._canSeek = (accessMode == AASSET_MODE_RANDOM);
    }

    public override bool CanRead => this._asset != nint.Zero;

    public override bool CanSeek => this._canSeek && this._asset != nint.Zero;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(this._asset == nint.Zero, nameof(AssetStream));

            return AAsset_getLength64(this._asset);
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(this._asset == nint.Zero, nameof(AssetStream));

            // Current position = total length - remaining length
            return AAsset_getLength64(this._asset)
                - AAsset_getRemainingLength64(this._asset);
        }

        set
        {
            if (!this._canSeek)
            {
                throw new NotSupportedException(
                    "This stream does not support seeking.");
            }

            this.Seek(value, SeekOrigin.Begin);
        }
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ObjectDisposedException.ThrowIf(this._asset == nint.Zero, nameof(AssetStream));

        if (count == 0)
        {
            return 0;
        }

        int bytesRead;
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bytesRead = AAsset_read(
                    this._asset, (nint)(ptr + offset), (nuint)count);
            }
        }

        if (bytesRead < 0)
        {
            throw new IOException(
                $"AAsset_read failed (requested {count} bytes).");
        }

        return bytesRead;
    }

    public override int Read(
        Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(this._asset == nint.Zero, nameof(AssetStream));

        if (buffer.Length == 0)
        {
            return 0;
        }

        int bytesRead;
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                bytesRead = AAsset_read(
                    this._asset, (nint)ptr, (nuint)buffer.Length);
            }
        }

        if (bytesRead < 0)
        {
            throw new IOException(
                $"AAsset_read failed (requested {buffer.Length} bytes).");
        }

        return bytesRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.Read(buffer, offset, count));
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(this.Read(buffer.Span));
    }

    public override long Seek(
        long offset,
        SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(this._asset == nint.Zero, nameof(AssetStream));

        if (!this._canSeek)
        {
            throw new NotSupportedException("This stream does not support seeking.");
        }

        int whence = origin switch
        {
            SeekOrigin.Begin => 0,   // SEEK_SET
            SeekOrigin.Current => 1, // SEEK_CUR
            SeekOrigin.End => 2,     // SEEK_END
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        long newPosition = AAsset_seek64(this._asset, offset, whence);

        if (newPosition < 0)
        {
            throw new IOException(
                $"AAsset_seek64 failed (offset={offset}, origin={origin}).");
        }

        return newPosition;
    }

    public override void Flush()
    {
        // No-op for read-only stream
    }

    public override void SetLength(
        long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(
        bool disposing)
    {
        nint asset = this._asset;

        if (asset != nint.Zero)
        {
            this._asset = nint.Zero;
            AAsset_close(asset);
        }

        base.Dispose(disposing);
    }

    // ----------------------------------------------------------------
    //  NDK AAsset P/Invoke declarations (libandroid.so)
    // ----------------------------------------------------------------

    private const string AndroidLib = "android";
    internal const int AASSET_MODE_STREAMING = 2;
    internal const int AASSET_MODE_RANDOM = 1;

    [LibraryImport(AndroidLib, EntryPoint = "AAssetManager_fromJava")]
    private static partial nint AAssetManager_fromJava(
        nint env,
        nint assetManagerJobject);

    [LibraryImport(AndroidLib, EntryPoint = "AAssetManager_open")]
    private static partial nint AAssetManager_open(
        nint manager,
        nint filename,
        int mode);

    [LibraryImport(AndroidLib, EntryPoint = "AAsset_read")]
    private static partial int AAsset_read(
        nint asset,
        nint buffer,
        nuint count);

    [LibraryImport(AndroidLib, EntryPoint = "AAsset_seek64")]
    private static partial long AAsset_seek64(
        nint asset,
        long offset,
        int whence);

    [LibraryImport(AndroidLib, EntryPoint = "AAsset_getLength64")]
    private static partial long AAsset_getLength64(
        nint asset);

    [LibraryImport(AndroidLib, EntryPoint = "AAsset_getRemainingLength64")]
    private static partial long AAsset_getRemainingLength64(
        nint asset);

    [LibraryImport(AndroidLib, EntryPoint = "AAsset_close")]
    private static partial void AAsset_close(
        nint asset);

    /// <summary>
    /// Gets the native <c>AAssetManager*</c> pointer from the application context.
    /// </summary>
    internal static nint GetNativeAssetManager()
    {
        var context = global::Android.App.Application.Context;
        var assetManager = context.Assets
            ?? throw new InvalidOperationException(
                "Android AssetManager is not available.");

        nint env = Java.Interop.JniEnvironment.EnvironmentPointer;
        nint jobject = assetManager.Handle;

        nint nativeManager = AAssetManager_fromJava(env, jobject);

        if (nativeManager == nint.Zero)
        {
            throw new InvalidOperationException(
                "Failed to obtain native AAssetManager pointer.");
        }

        return nativeManager;
    }
}
