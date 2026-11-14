// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// Centralized direct P/Invoke bindings for the OpenGL ES 2.0 entry
/// points this engine calls on the per-frame hot path.
/// </summary>
/// <remarks>
/// Each entry point here is invoked once or more per frame and would
/// otherwise be marshaled through Mono.Android's <c>GLES20</c> Java
/// binding. That route pays one JNI hop (and for the variants that
/// take arrays, a managed-to-Java array copy) per call. The native
/// <c>libGLESv2.so</c> symbols accept the same arguments as raw
/// pointers / primitives, so going through <see cref="System.Runtime.InteropServices.LibraryImportAttribute"/>
/// removes the per-call marshaling cost entirely.
///
/// Only the truly per-frame hot paths live here, plus <c>glTexImage2D</c> and
/// <c>glCompressedTexImage2D</c>, which upload pixels or blocks in place (the <c>GLES20</c>
/// binding needs them in a <c>java.nio.Buffer</c>, which would mean an
/// extra full-size allocation and copy per texture). Other one-shot init
/// code (program compile, buffer creation) still goes through
/// <c>GLES20</c> where the readability win is worth more than the
/// negligible JNI cost.
/// </remarks>
internal static partial class GLBindings
{
    private const string LibraryName = "libGLESv2.so";

    // --- Texture upload -----------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glTexImage2D")]
    internal static partial void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, nint pixels);

    [LibraryImport(LibraryName, EntryPoint = "glTexSubImage2D")]
    internal static partial void glTexSubImage2D(int target, int level, int x, int y, int width, int height, int format, int type, nint pixels);

    [LibraryImport(LibraryName, EntryPoint = "glCompressedTexImage2D")]
    internal static partial void glCompressedTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int imageSize, nint data);

    // --- Buffer object management -------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glBindBuffer")]
    internal static partial void glBindBuffer(int target, int buffer);

    [LibraryImport(LibraryName, EntryPoint = "glBufferData")]
    internal static partial void glBufferData(int target, nint size, nint data, int usage);

    [LibraryImport(LibraryName, EntryPoint = "glBufferSubData")]
    internal static partial void glBufferSubData(int target, nint offset, nint size, nint data);

    // --- Vertex attribute setup ---------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glEnableVertexAttribArray")]
    internal static partial void glEnableVertexAttribArray(int index);

    [LibraryImport(LibraryName, EntryPoint = "glVertexAttribPointer")]
    internal static partial void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, nint pointer);

    // --- Texture binding ----------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glActiveTexture")]
    internal static partial void glActiveTexture(int texture);

    [LibraryImport(LibraryName, EntryPoint = "glBindTexture")]
    internal static partial void glBindTexture(int target, int texture);

    // --- Framebuffer / viewport ---------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glBindFramebuffer")]
    internal static partial void glBindFramebuffer(int target, int framebuffer);

    [LibraryImport(LibraryName, EntryPoint = "glViewport")]
    internal static partial void glViewport(int x, int y, int width, int height);

    [LibraryImport(LibraryName, EntryPoint = "glReadPixels")]
    internal static partial void glReadPixels(int x, int y, int width, int height, int format, int type, nint pixels);

    // --- Clearing -----------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glClearColor")]
    internal static partial void glClearColor(float r, float g, float b, float a);

    [LibraryImport(LibraryName, EntryPoint = "glClear")]
    internal static partial void glClear(int mask);

    // --- State toggles ------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glEnable")]
    internal static partial void glEnable(int cap);

    [LibraryImport(LibraryName, EntryPoint = "glDisable")]
    internal static partial void glDisable(int cap);

    [LibraryImport(LibraryName, EntryPoint = "glBlendFunc")]
    internal static partial void glBlendFunc(int sfactor, int dfactor);

    [LibraryImport(LibraryName, EntryPoint = "glScissor")]
    internal static partial void glScissor(int x, int y, int width, int height);

    // --- Programs and uniforms ----------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glUseProgram")]
    internal static partial void glUseProgram(int program);

    [LibraryImport(LibraryName, EntryPoint = "glUniform1f")]
    internal static partial void glUniform1f(int location, float v0);

    [LibraryImport(LibraryName, EntryPoint = "glUniform1i")]
    internal static partial void glUniform1i(int location, int v0);

    [LibraryImport(LibraryName, EntryPoint = "glUniform2f")]
    internal static partial void glUniform2f(int location, float v0, float v1);

    [LibraryImport(LibraryName, EntryPoint = "glUniform4f")]
    internal static partial void glUniform4f(int location, float v0, float v1, float v2, float v3);

    /// <remarks>
    /// Mono.Android's <c>GlUniformMatrix4fv</c> binding marshals the
    /// managed <c>float[16]</c> through a Java <c>float[]</c>
    /// (alloc+copy) on every call. The native ES2 entry point takes a
    /// pointer, so callers can pin their cached matrix and hand the
    /// raw pointer in here, avoiding all of that.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "glUniformMatrix4fv")]
    internal static partial void glUniformMatrix4fv(int location, int count, byte transpose, nint value);

    // --- Drawing ------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "glDrawElements")]
    internal static partial void glDrawElements(int mode, int count, int type, nint indices);
}
