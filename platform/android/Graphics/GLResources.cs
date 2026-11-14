// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using Android.Opengl;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// Deletes GL objects from any thread. GL calls only work on the thread with the context, which a
/// finalizer's isn't, nor a worker's that disposes a texture, so those deletes wait for the game
/// thread's next frame. A name belongs to the context that made it: names restart in a new context,
/// so a delete from an earlier one is dropped rather than deleting something else.
/// </summary>
internal static class GLResources
{
    private readonly record struct PendingDelete(int Id, bool IsProgram, int Generation);

    private static readonly ConcurrentQueue<PendingDelete> s_pending = new();

    /// <summary>
    /// The current context's number, which an object remembers when it's created.
    /// </summary>
    internal static int Generation { get; private set; }

    /// <summary>
    /// What else goes when a texture does (its framebuffer) and when a program does (the state
    /// tracked for it). Called on the game thread.
    /// </summary>
    internal static Action<int>? TextureDeleted { get; set; }

    internal static Action<int>? ProgramDeleted { get; set; }

    /// <summary>
    /// A new context was made current.
    /// </summary>
    internal static void OnNewContext()
    {
        Generation++;
        s_pending.Clear();
    }

    internal static void DeleteTexture(
        int textureId,
        int generation)
    {
        Delete(new PendingDelete(textureId, IsProgram: false, generation));
    }

    internal static void DeleteProgram(
        int programId,
        int generation)
    {
        Delete(new PendingDelete(programId, IsProgram: true, generation));
    }

    /// <summary>
    /// Runs the deletes that were waiting for the game thread. Called each frame; allocates nothing
    /// when there are none.
    /// </summary>
    internal static void RunPendingDeletes()
    {
        while (s_pending.TryDequeue(out var delete))
        {
            DeleteNow(delete);
        }
    }

    private static void Delete(
        PendingDelete delete)
    {
        if (GameThread.IsCurrent)
        {
            DeleteNow(delete);
        }
        else
        {
            s_pending.Enqueue(delete);
        }
    }

    private static void DeleteNow(
        PendingDelete delete)
    {
        if (delete.Generation != Generation)
        {
            return;
        }

        if (delete.IsProgram)
        {
            GLES20.GlDeleteProgram(delete.Id);
            ProgramDeleted?.Invoke(delete.Id);
        }
        else
        {
            TextureDeleted?.Invoke(delete.Id);
            GLES20.GlDeleteTextures(1, [delete.Id], 0);
        }
    }
}
