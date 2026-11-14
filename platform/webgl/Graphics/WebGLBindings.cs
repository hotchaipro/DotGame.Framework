// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed partial class WebGLBindings
{
    private const string ModuleName = "graphics.js";

    [JSImport("graphics.getOrCreateWebGLContext", ModuleName)]
    internal static partial JSObject GetOrCreateWebGLContext(string elementId, bool multisample);

    [JSImport("graphics.getMultisampleCount", ModuleName)]
    internal static partial int GetMultisampleCount(JSObject webGlContext);

    [JSImport("graphics.createTexture2D", ModuleName)]
    internal static partial JSObject CreateTexture2D();

    [JSImport("graphics.createTextureFromData", ModuleName)]
    internal static partial JSObject CreateTextureFromData(JSObject glContext, int width, int height, [JSMarshalAs<JSType.MemoryView>] Span<byte> rgbaData);

    [JSImport("graphics.setTextureData", ModuleName)]
    internal static partial void SetTextureData(JSObject texture, int x, int y, int width, int height, [JSMarshalAs<JSType.MemoryView>] Span<byte> rgbaData);

    [JSImport("graphics.loadTexture2DFromContent", ModuleName)]
    internal static partial Task LoadTexture2DFromContent(JSObject texture, JSObject glContext, string filePath);

    [JSImport("graphics.unloadTexture2D", ModuleName)]
    internal static partial void UnloadTexture2D(JSObject texture);

    [JSImport("graphics.createFrameRenderer", ModuleName)]
    internal static partial void CreateFrameRenderer(JSObject glContext, JSObject defaultProgram);

    [JSImport("graphics.setFrameResource", ModuleName)]
    internal static partial void SetFrameResource(int index, JSObject resource);

    [JSImport("graphics.clearFrameResources", ModuleName)]
    internal static partial void ClearFrameResources();

    [JSImport("graphics.executeFrame", ModuleName)]
    internal static partial int ExecuteFrame([JSMarshalAs<JSType.MemoryView>] Span<byte> vertices, int spriteCount, [JSMarshalAs<JSType.MemoryView>] Span<byte> commands, int commandCount);

    [JSImport("graphics.isContextLost", ModuleName)]
    internal static partial bool IsContextLost(JSObject glContext);

    [JSImport("graphics.createRenderTexture", ModuleName)]
    internal static partial JSObject CreateRenderTexture(JSObject glContext, int width, int height);

    [JSImport("graphics.takeReadback", ModuleName)]
    internal static partial int TakeReadback(int readbackId, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("graphics.loadShaderSource", ModuleName)]
    internal static partial Task<string> LoadShaderSource(string filePath);

    [JSImport("graphics.compileShaderProgram", ModuleName)]
    internal static partial JSObject CompileShaderProgram(JSObject glContext, string vertexShaderSource, string fragmentShaderSource);

    [JSImport("graphics.deleteShaderProgram", ModuleName)]
    internal static partial void DeleteShaderProgram(JSObject program);

    [JSImport("graphics.getUniformLocation", ModuleName)]
    internal static partial JSObject? GetUniformLocation(JSObject glContext, JSObject program, string name);

    [JSImport("graphics.handleContextLost", ModuleName)]
    internal static partial void HandleContextLost();

    [JSImport("graphics.handleContextRestored", ModuleName)]
    internal static partial void HandleContextRestored();
}
