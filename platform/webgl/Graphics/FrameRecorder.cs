// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Common.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

/// <summary>
/// Records a frame's drawing, from every sprite batch, and runs it at the end of the frame in one
/// call into JavaScript: the sprites' vertices, and a list of commands for what to do with them.
/// </summary>
/// <remarks>
/// <para>
/// Crossing into JavaScript and copying data across is the web's one real per-frame cost, so a frame
/// crosses once and copies nothing: JavaScript uploads the vertices from the WebAssembly heap in one
/// call, and does no work per sprite. Ending a batch costs nothing, so the engine can end one around
/// every run of text.
/// </para>
/// <para>
/// Everything that changes state is recorded in order with the sprites, so sprites drawn before a
/// change of render target, scissor or uniform get the state they were drawn with, as on the other
/// platforms. The browser's own check is <c>dgRenderStats</c>: one execute per frame.
/// </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class FrameRecorder
{
    private const int BytesPerSprite = 4 * SpriteVertex.Size;
    private const int InitialSpriteCapacity = 256;
    private const int InitialCommandCapacity = 256;

    // Resources stay registered from frame to frame, so a frame's textures cost no calls. This many
    // is the sign of a game that keeps creating them, when the table starts over.
    private const int MaxResources = 2048;

    // Command opcodes (encoded as floats in the command buffer). Matches graphics.js.
    private const float CMD_DRAW = 1;
    private const float CMD_SET_RENDER_TARGET = 2;
    private const float CMD_CLEAR = 3;
    private const float CMD_SET_SCISSOR = 4;
    private const float CMD_DISABLE_SCISSOR = 5;
    private const float CMD_SET_SHADER = 6;
    private const float CMD_SET_UNIFORM_FLOAT = 7;
    private const float CMD_SET_UNIFORM_VEC2 = 8;
    private const float CMD_SET_UNIFORM_INT = 9;
    private const float CMD_SET_RENDER_STATE = 30;
    private const float CMD_SET_UNIFORM_VEC4 = 10;
    private const float CMD_READBACK = 11;

    private byte[] _vertices = new byte[InitialSpriteCapacity * BytesPerSprite];
    private int _spriteCount;

    private float[] _commands = new float[InitialCommandCapacity];
    private int _commandCount;

    private readonly Dictionary<JSObject, int> _resourceIndices = new(ReferenceEqualityComparer.Instance);

    // The sprites since the last draw command, all with this texture and shader
    private JSObject? _texture;
    private int _textureIndex;
    private int _drawStart;

    // The shader the recorded commands have reached, null for the default
    private JSObject? _shader;

    /// <summary>
    /// The room for a sprite's vertices, which the caller writes, drawn with this texture and shader.
    /// </summary>
    internal ref SpriteVertex AddSprite(
        JSObject texture,
        JSObject? shader)
    {
        // NOTE: The usual case is the same as the last sprite, so no lookup
        if (!ReferenceEquals(shader, this._shader))
        {
            this.CloseDraw();
            this._shader = shader;
            this.EnsureCommandCapacity(2);
            this._commands[this._commandCount++] = CMD_SET_SHADER;
            this._commands[this._commandCount++] = (shader is null) ? -1 : this.GetResourceIndex(shader);
        }

        if (!ReferenceEquals(texture, this._texture))
        {
            this.CloseDraw();
            this._texture = texture;
            this._textureIndex = this.GetResourceIndex(texture);
        }

        int offset = this._spriteCount * BytesPerSprite;
        if ((offset + BytesPerSprite) > this._vertices.Length)
        {
            Array.Resize(ref this._vertices, this._vertices.Length * 2);
        }

        this._spriteCount++;

        return ref MemoryMarshal.AsRef<SpriteVertex>(this._vertices.AsSpan(offset, BytesPerSprite));
    }

    /// <summary>
    /// The blend mode and sampler for the sprites from here on. They change at a batch's Begin, so
    /// adding a sprite has nothing more to compare.
    /// </summary>
    internal void SetRenderState(
        int renderState)
    {
        if (renderState != this._renderState)
        {
            this.CloseDraw();
            this._renderState = renderState;
            this.EnsureCommandCapacity(2);
            this._commands[this._commandCount++] = CMD_SET_RENDER_STATE;
            this._commands[this._commandCount++] = renderState;
        }
    }

    internal void RecordSetRenderTarget(
        JSObject? renderTexture)
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(2);
        this._commands[this._commandCount++] = CMD_SET_RENDER_TARGET;
        this._commands[this._commandCount++] = (renderTexture is null) ? -1 : this.GetResourceIndex(renderTexture);
    }

    /// <summary>
    /// A render texture's pixels, read as the frame runs this far, kept in JS under the readback id.
    /// </summary>
    internal void RecordReadback(
        JSObject renderTexture,
        int readbackId)
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(3);
        this._commands[this._commandCount++] = CMD_READBACK;
        this._commands[this._commandCount++] = this.GetResourceIndex(renderTexture);
        this._commands[this._commandCount++] = readbackId;
    }

    internal void RecordClear(
        float r,
        float g,
        float b,
        float a)
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(5);
        this._commands[this._commandCount++] = CMD_CLEAR;
        this._commands[this._commandCount++] = r;
        this._commands[this._commandCount++] = g;
        this._commands[this._commandCount++] = b;
        this._commands[this._commandCount++] = a;
    }

    internal void RecordSetScissor(
        int x,
        int y,
        int width,
        int height)
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(5);
        this._commands[this._commandCount++] = CMD_SET_SCISSOR;
        this._commands[this._commandCount++] = x;
        this._commands[this._commandCount++] = y;
        this._commands[this._commandCount++] = width;
        this._commands[this._commandCount++] = height;
    }

    internal void RecordDisableScissor()
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(1);
        this._commands[this._commandCount++] = CMD_DISABLE_SCISSOR;
    }

    internal void RecordSetUniform(
        JSObject program,
        JSObject location,
        float value)
    {
        this.BeginUniform(CMD_SET_UNIFORM_FLOAT, program, location, 1);
        this._commands[this._commandCount++] = value;
    }

    internal void RecordSetUniform(
        JSObject program,
        JSObject location,
        int value)
    {
        this.BeginUniform(CMD_SET_UNIFORM_INT, program, location, 1);
        this._commands[this._commandCount++] = value;
    }

    internal void RecordSetUniform(
        JSObject program,
        JSObject location,
        Vector2 value)
    {
        this.BeginUniform(CMD_SET_UNIFORM_VEC2, program, location, 2);
        this._commands[this._commandCount++] = value.X;
        this._commands[this._commandCount++] = value.Y;
    }

    internal void RecordSetUniform(
        JSObject program,
        JSObject location,
        Vector4 value)
    {
        this.BeginUniform(CMD_SET_UNIFORM_VEC4, program, location, 4);
        this._commands[this._commandCount++] = value.X;
        this._commands[this._commandCount++] = value.Y;
        this._commands[this._commandCount++] = value.Z;
        this._commands[this._commandCount++] = value.W;
    }

    // [command, program, location, values...]. The command names its program because a shader's
    // parameters are usually set before the batch that uses it switches to it, and a uniform
    // location only applies to its own program. The sprites drawn so far keep the value they were
    // drawn with.
    private void BeginUniform(
        float command,
        JSObject program,
        JSObject location,
        int valueCount)
    {
        this.CloseDraw();
        this.EnsureCommandCapacity(3 + valueCount);
        this._commands[this._commandCount++] = command;
        this._commands[this._commandCount++] = this.GetResourceIndex(program);
        this._commands[this._commandCount++] = this.GetResourceIndex(location);
    }

    private void CloseDraw()
    {
        int count = this._spriteCount - this._drawStart;
        if (count <= 0)
        {
            return;
        }

        this.EnsureCommandCapacity(4);
        this._commands[this._commandCount++] = CMD_DRAW;
        this._commands[this._commandCount++] = this._textureIndex;
        this._commands[this._commandCount++] = this._drawStart;
        this._commands[this._commandCount++] = count;

        this._drawStart = this._spriteCount;
    }

    private int GetResourceIndex(
        JSObject resource)
    {
        if (!this._resourceIndices.TryGetValue(resource, out int index))
        {
            index = this._resourceIndices.Count;
            this._resourceIndices.Add(resource, index);
            WebGLBindings.SetFrameResource(index, resource);
        }

        return index;
    }

    private void EnsureCommandCapacity(
        int additional)
    {
        int required = this._commandCount + additional;
        if (required > this._commands.Length)
        {
            Array.Resize(ref this._commands, Math.Max(required, this._commands.Length * 2));
        }
    }

    private int _renderState;

    /// <summary>
    /// Runs what's been recorded, and starts the next frame's recording.
    /// </summary>
    /// <param name="canDraw">False while the context is lost, when the frame is dropped.</param>
    internal void Execute(
        bool canDraw)
    {
        this.CloseDraw();

        if (canDraw && (this._commandCount > 0))
        {
            var vertices = this._vertices.AsSpan(0, this._spriteCount * BytesPerSprite);
            var commands = MemoryMarshal.AsBytes(this._commands.AsSpan(0, this._commandCount));

            int drawCalls = WebGLBindings.ExecuteFrame(vertices, this._spriteCount, commands, this._commandCount);
            GameHost.CountDraws(drawCalls, this._spriteCount);
        }

        this._spriteCount = 0;
        this._commandCount = 0;
        this._drawStart = 0;
        this._texture = null;
        this._shader = null;
        this._renderState = 0;

        if (this._resourceIndices.Count > MaxResources)
        {
            this.ForgetResources();
        }
    }

    /// <summary>
    /// Forgets the resources registered in JS: when there are too many, and after a lost context,
    /// when none of them are any use.
    /// </summary>
    internal void ForgetResources()
    {
        this._resourceIndices.Clear();
        this._texture = null;
        this._shader = null;
        this._renderState = 0;
        WebGLBindings.ClearFrameResources();
    }
}
