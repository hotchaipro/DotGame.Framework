// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Common.Input;

/// <summary>
/// Gives each connected gamepad, known to the platform by its own id, the lowest free index.
/// Use it from one thread.
/// </summary>
internal sealed class GamepadSlots
{
    private readonly long?[] _ids = new long?[InputService.MaxGamepads];

    /// <summary>
    /// The id's index, or -1 if it isn't connected.
    /// </summary>
    public int IndexOf(
        long id)
    {
        for (int i = 0; i < this._ids.Length; i++)
        {
            if (this._ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gives the id an index, or returns -1 if every one is taken.
    /// </summary>
    public int Connect(
        long id)
    {
        for (int i = 0; i < this._ids.Length; i++)
        {
            if (this._ids[i] is null)
            {
                this._ids[i] = id;
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Frees the id's index, and returns it, or -1 if it wasn't connected.
    /// </summary>
    public int Disconnect(
        long id)
    {
        int index = this.IndexOf(id);
        if (index >= 0)
        {
            this._ids[index] = null;
        }

        return index;
    }
}
