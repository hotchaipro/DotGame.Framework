// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DotGame.Framework.WebGL.Content;

[SupportedOSPlatform("browser")]
internal sealed partial class ContentBindings
{
    private const string ModuleName = "content.js";

    [JSImport("content.getBaseUrl", ModuleName)]
    internal static partial string GetBaseUrl();

    // Resolves to the content's bytes as a JavaScript Uint8Array; copy them out with
    // CopyContent. (A Task can't carry a byte[] across the interop boundary.)
    [JSImport("content.fetchContent", ModuleName)]
    internal static partial Task<JSObject> FetchContentAsync(
        string contentPath);

    [JSImport("content.copyContent", ModuleName)]
    internal static partial void CopyContent(
        JSObject source,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
}
