// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Whose code an error that reached the page's global handlers came from. Ad, analytics and
// portal scripts are normal on a web page, and their failures aren't the game's.
//
// 'third-party': every location it names is on another origin. It's traced and the game runs on.
// 'own': a location is on the page's origin, or in the WebAssembly runtime. The game's fault.
// 'unknown': it names no location (a cross-origin script's bare "Script error."). Treated as the
// game's: a game bug that lost its stack must not go unreported, so this can't be third-party.
export function classifyPageError(stack, filename, pageOrigin) {
    // NOTE: A failed fetch's own stack is the fetch wrapper's, on this page's origin, so what
    // counts is the code that called it (see attributeFetchFault)
    const text = String(stack ?? '');
    const fetchedFrom = text.indexOf('Fetched from:');
    const frames = (fetchedFrom >= 0) ? text.substring(fetchedFrom) : text;

    if (/wasm:\/\/|wasm-function|blob:/.test(frames)) {
        return 'own';
    }

    const locations = frames.match(/https?:\/\/[^\s()"'<>]+/g) ?? [];
    if (filename) {
        locations.push(String(filename));
    }

    let origins = 0;
    for (const location of locations) {
        let origin;
        try {
            origin = new URL(location).origin;
        } catch {
            continue;
        }

        if (origin === pageOrigin) {
            return 'own';
        }

        origins++;
    }

    return (origins > 0) ? 'third-party' : 'unknown';
}