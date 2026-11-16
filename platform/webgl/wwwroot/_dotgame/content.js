// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

import { trace, attributeFetchFault, captureCallerStack, showConnectionMessage } from './diagnostics.js'

/**
 * Returns the base URL that content paths are resolved against.
 * Honors a <base href> element if present, otherwise uses the
 * directory of the current page. Using window.location.origin
 * would drop any sub-path (e.g. https://example.com/folder/)
 * and cause all content requests to 404.
 * Always ends with a trailing '/'.
 */
export function getBaseUrl() {
    var baseUrl = (document.baseURI || window.location.href).split('?')[0].split('#')[0];

    // Trim any trailing document name (e.g. "index.html")
    return baseUrl.substring(0, baseUrl.lastIndexOf('/') + 1);
}

/**
 * Resolves a content path (relative to the base URL) to an absolute URL.
 */
export function resolveContentUrl(filePath) {
    return new URL(filePath, getBaseUrl()).href;
}

// A network failure (a TypeError from fetch or from reading its body: "Load
// failed" in Safari, "Failed to fetch" in Chrome) is retried this many times
// while the page is visible. Time spent hidden or offline doesn't count.
const NetworkRetryDelaysMs = [250, 1000, 2000, 4000];

function isNetworkFailure(error) {
    return (error instanceof TypeError);
}

// Resolves once the page is visible and the browser thinks it's online. A phone
// suspends a backgrounded page's network, so a request in flight when the player
// switches apps fails, and retrying before they come back would fail too.
function whenNetworkUsable() {
    const hidden = () => (typeof document !== 'undefined') && document.hidden;
    const offline = () => (typeof navigator !== 'undefined') && (navigator.onLine === false);
    if (!hidden() && !offline()) {
        return Promise.resolve();
    }

    return new Promise((resolve) => {
        const check = () => {
            if (!hidden() && !offline()) {
                document.removeEventListener('visibilitychange', check);
                globalThis.removeEventListener('online', check);
                resolve();
            }
        };
        document.addEventListener('visibilitychange', check);
        globalThis.addEventListener('online', check);
    });
}

// The URLs fetched until the first frame, for reportStartupContent; null after it
let startupFetches = new Set();

/**
 * Stops recording fetches and returns those made before the first frame. Called as the first
 * frame is drawn, before anything after it (such as postload) fetches.
 */
export function takeStartupFetches() {
    const fetched = startupFetches;
    startupFetches = null;
    return fetched;
}

/**
 * While developing, says what was fetched before the first frame (takeStartupFetches) and isn't
 * preloaded: marking it Preload="true" starts its download with the page's, which a game can only
 * learn by running. And the other way round: content the page preloads that nothing fetched by the
 * first frame, whose download only took bandwidth from what the first frame needed.
 */
export function reportStartupContent(fetched) {
    const host = window.location.hostname;
    if ((host !== 'localhost') && (host !== '127.0.0.1') && !host.endsWith('.localhost')) {
        return;
    }

    const preloaded = new Set(Array.from(document.querySelectorAll('link[rel="preload"]'), link => link.href));
    const missing = Array.from(fetched).filter(url => !preloaded.has(url) && url.includes('/Content/'));
    if (missing.length > 0) {
        console.info(
            `[startup] ${missing.length} content file(s) loaded before the first frame aren't preloaded. `
            + `Add Preload="true" to their content items for a faster start:\n  ${missing.join('\n  ')}`);
    }

    // NOTE: Only the content fetch preloads: the launch screen's images are preloaded for its <img> elements
    const unused = Array.from(document.querySelectorAll('link[rel="preload"][as="fetch"]'), link => link.href)
        .filter(url => !fetched.has(url) && url.includes('/Content/'));
    if (unused.length > 0) {
        console.warn(
            `[startup] ${unused.length} preloaded content file(s) weren't loaded before the first frame, so their downloads `
            + `only slowed it. Remove Preload="true" from their content items, unless the first frame should load them:\n  ${unused.join('\n  ')}`);
    }
}

/**
 * Fetches a URL and reads its response with `read` (which receives the
 * Response), retrying the whole request when it fails at the network level --
 * as it does when the player switches away from the page while it loads.
 * HTTP errors aren't retried: `read` sees the response and decides. An
 * AbortError is never retried.
 */
// Not a fault of the game: the player is asked to try again, with no crash dialog or report. Try
// Again reloads, so what asked for the content stays waiting behind the message rather than failing
export function waitBehindConnectionMessage(url, reason = 'offline', detail = '') {
    showConnectionMessage(url, reason, detail);
    return new Promise(() => { });
}

/**
 * Why a response for a file the game needs wasn't the file: a 404 or 410 is a file the current
 * deploy doesn't have (a page left from an earlier one asks for it); anything else, the host's error.
 */
export function failedResponseReason(response) {
    return ((response.status === 404) || (response.status === 410)) ? 'stale' : 'bad-file';
}

export async function fetchWithRetry(url, init, read) {
    startupFetches?.add(new URL(url, document.baseURI).href);

    // Retries run after an await, where the stack no longer shows who asked
    const callerStack = captureCallerStack();

    for (let attempt = 0; ; attempt += 1) {
        try {
            const response = await fetch(url, init);
            return read ? await read(response) : response;
        } catch (error) {
            if (!isNetworkFailure(error)) {
                throw attributeRequestFault(url, callerStack, attempt, error);
            }
            if (attempt >= NetworkRetryDelaysMs.length) {
                attributeRequestFault(url, callerStack, attempt, error);
                return waitBehindConnectionMessage(url);
            }

            trace(0x3c7a51e9, `network failure (${error.message}), retry ${attempt + 1}: ${url}`);
            await whenNetworkUsable();
            await new Promise((resolve) => setTimeout(resolve, NetworkRetryDelaysMs[attempt]));
            await whenNetworkUsable();
        }
    }
}

// A failure in reading the body hasn't been attributed by the fetch wrapper, and
// one in fetching was attributed to this module's retry loop: either way the
// error also says who requested it, and how many attempts it took.
function attributeRequestFault(url, callerStack, attempt, error) {
    if (!isNetworkFailure(error)) {
        return error;
    }
    if (!error.dgFetchUrl) {
        return attributeFetchFault(url, callerStack, error);
    }
    try {
        error.stack = `${error.stack}
Requested from (after ${attempt + 1} attempts):
${callerStack}`;
    } catch {
        // The fetch wrapper's attribution stands.
    }
    return error;
}

// The publish's precompression, from the page (pipeline/tasks/_ApplyWebPrecompression.cs): the
// format of the copies ('br', or 'gz' with DotGameWebDecompressionFallback) and the extensions of
// the files that have them; null for a build, which fetches every file as it is
const precompression = (() => {
    const meta = (typeof document !== 'undefined') ? document.querySelector('meta[name="dotgame-precompressed"]') : null;
    if (!meta || ((meta.content !== 'br') && (meta.content !== 'gz'))) {
        return null;
    }
    return { format: meta.content, extensions: new Set((meta.dataset.extensions || '').split(' ').filter(Boolean)) };
})();

function extensionOf(url) {
    const path = url.split('?')[0].split('#')[0];
    const name = path.substring(path.lastIndexOf('/') + 1);
    const dot = name.lastIndexOf('.');
    return (dot > 0) ? name.substring(dot).toLowerCase() : '';
}

const ContentTypes = { '.wasm': 'application/wasm', '.json': 'application/json', '.js': 'text/javascript' };

/**
 * The URL to fetch for a file: its precompressed copy, where the publish made one, or itself.
 */
export function precompressedUrl(url) {
    // NOTE: Never a script: a module is imported by its URL, which the browser must decode itself
    const extension = extensionOf(url);
    if (!precompression || !precompression.extensions.has(extension) || (extension === '.js') || (extension === '.mjs')) {
        return url;
    }
    const end = url.search(/[?#]/);
    return (end < 0) ? `${url}.${precompression.format}` : `${url.substring(0, end)}.${precompression.format}${url.substring(end)}`;
}

let reportedHostSetup = false;

function reportHostSetup(message) {
    if (!reportedHostSetup) {
        reportedHostSetup = true;
        console.error(message);
    }
}

/**
 * Whether a wasm copy has arrived in an encoding other than br (none, or one the browser decoded)
 * and turned out to be the module: the host (a CDN) decodes copies for this browser, so any other
 * copy that arrives with no encoding is the file too. A CDN may send a small file plain and a large
 * one as gzip to the same browser (Cloudflare does). Brotli bytes have no signature to check them
 * by, so only a wasm can show it.
 */
let sendsDecodedCopies = false;

/**
 * The body of a wasm that came in an encoding other than br, which passes on as it arrives, and
 * notes whether it's the module (a CDN decoded the copy for this browser). With `message` (no
 * encoding), what isn't the module is the copy's Brotli bytes, which the host didn't say it sent,
 * and the body fails with `message`; without it, the caller's own check reports it.
 */
function watchWasmModule(body, message) {
    let head = new Uint8Array(0);
    let checked = false;
    return body.pipeThrough(new TransformStream({
        transform(chunk, controller) {
            if (checked) {
                controller.enqueue(chunk);
                return;
            }
            const joined = new Uint8Array(head.length + chunk.length);
            joined.set(head);
            joined.set(chunk, head.length);
            head = joined;
            if (head.length < 4) {
                return;
            }
            checked = true;
            if ((head[0] === 0x00) && (head[1] === 0x61) && (head[2] === 0x73) && (head[3] === 0x6d)) {
                sendsDecodedCopies = true;
            } else if (message) {
                reportHostSetup(message);
                controller.error(new Error(message));
                return;
            }
            controller.enqueue(head);
        },
        flush(controller) {
            if (checked) {
                return;
            }
            if (!message) {
                controller.enqueue(head);
            } else {
                reportHostSetup(message);
                controller.error(new Error(message));
            }
        },
    }));
}

/**
 * The response for a file fetched as its precompressed copy, as if it were the file: decoded, with
 * the file's own type, so a wasm still compiles as it arrives. A Brotli copy the host sent without
 * Content-Encoding: br can't be decoded here: the host isn't set up, which stops the game with an
 * error saying so (docs/guide/platforms.md, "Hosting"). A gzip copy (the fallback) that the host
 * didn't send as gzip is decoded here, natively.
 *
 * A Brotli copy can also arrive decoded, or in another encoding the browser decodes: a CDN sends
 * a browser (or a proxy) that doesn't accept Brotli the file itself, plain or as gzip, from the
 * copy (Cloudflare does). Any other encoding the response names is that, so the body is the file.
 * One that names none is either: a wasm is checked for WebAssembly's magic number; any other file
 * is the file once a wasm has arrived in an encoding other than br as the module (the runtime's
 * wasm loads before any content), and is taken as the host's Brotli bytes otherwise.
 */
export function readPrecompressed(response, originalUrl) {
    if (!response.ok) {
        return response;
    }

    const encoding = (response.headers.get('Content-Encoding') || '').toLowerCase();
    let body = response.body;
    if (precompression.format === 'br') {
        if ((encoding === '') || (encoding === 'identity')) {
            const message = `The host sent '${response.url || originalUrl}' without the header Content-Encoding: br, so the browser can't decode it. `
                + 'A DotGame web publish fetches a Brotli copy of each file, and the host must send those with that header '
                + '(see the DotGame guide, Platforms, "Hosting", for each host\'s setup), or the publish can set DotGameWebDecompressionFallback '
                + 'to true, to fetch gzip copies the page decodes itself, more slowly.';
            if (extensionOf(originalUrl) === '.wasm') {
                body = watchWasmModule(body, message);
            } else if (!sendsDecodedCopies) {
                reportHostSetup(message);
                throw new Error(message);
            }
        } else if ((encoding !== 'br') && (extensionOf(originalUrl) === '.wasm')) {
            body = watchWasmModule(body, null);
        }
    } else if (encoding !== 'gzip') {
        body = body.pipeThrough(new DecompressionStream('gzip'));
    }

    const type = ContentTypes[extensionOf(originalUrl)] || 'application/octet-stream';
    return new Response(body, { status: 200, headers: { 'Content-Type': type } });
}

/**
 * Checks that a response's body starts with WebAssembly's magic number (\0asm), reading only its
 * first chunk. Resolves to { response }, the same body to compile as it arrives, or, for what
 * isn't a module, to { length, start }: the bytes read, and the first of them as hex and text.
 */
export async function readWasm(response) {
    const reader = response.body.getReader();
    const chunks = [];
    let length = 0;
    while (length < 4) {
        const { done, value } = await reader.read();
        if (done) {
            break;
        }
        chunks.push(value);
        length += value.length;
    }

    const head = new Uint8Array(Math.min(length, 32));
    let offset = 0;
    for (const chunk of chunks) {
        if (offset >= head.length) {
            break;
        }
        const part = chunk.subarray(0, head.length - offset);
        head.set(part, offset);
        offset += part.length;
    }

    if ((head.length < 4) || (head[0] !== 0x00) || (head[1] !== 0x61) || (head[2] !== 0x73) || (head[3] !== 0x6d)) {
        reader.cancel().catch(() => { });
        const hex = Array.from(head, (b) => b.toString(16).padStart(2, '0')).join(' ');
        const text = Array.from(head, (b) => ((b >= 0x20) && (b < 0x7f)) ? String.fromCharCode(b) : '.').join('');
        return { response: null, length: length, start: `${hex} (${text})` };
    }

    const body = new ReadableStream({
        start(controller) {
            for (const chunk of chunks) {
                controller.enqueue(chunk);
            }
        },
        async pull(controller) {
            const { done, value } = await reader.read();
            if (done) {
                controller.close();
            } else {
                controller.enqueue(value);
            }
        },
        cancel(reason) {
            return reader.cancel(reason);
        }
    });
    return { response: new Response(body, { status: 200, headers: { 'Content-Type': 'application/wasm' } }) };
}

if (precompression && (precompression.format === 'gz')) {
    trace(0x6a1e0c47, 'decompression fallback: the page fetches gzip copies and decodes those the host didn\'t (DotGameWebDecompressionFallback)');
    console.warn('DotGame: this publish decodes gzip copies in the page (DotGameWebDecompressionFallback), which is slower than a host that sends Brotli copies with Content-Encoding: br. See the guide, Platforms, "Hosting".');
}

/**
 * Fetches a content file (a path relative to the base URL) and resolves to its
 * bytes as a Uint8Array, which the managed side copies out with copyContent.
 * A plain same-origin fetch, so it matches the page's
 * <link rel="preload" as="fetch" crossorigin="anonymous"> preloads (which the
 * publish points at the same precompressed copy).
 */
async function fetchContent(filePath) {
    const url = resolveContentUrl(filePath);
    const fetched = precompressedUrl(url);
    return fetchWithRetry(fetched, undefined, async (response) => {
        if (!response.ok) {
            trace(0x71c20e9a, `content request failed: ${response.status} ${response.statusText} '${filePath}'`);
            return waitBehindConnectionMessage(filePath, failedResponseReason(response), `${response.status} ${filePath}`);
        }
        const decoded = (fetched === url) ? response : readPrecompressed(response, url);
        return new Uint8Array(await decoded.arrayBuffer());
    });
}

/**
 * Copies fetched content bytes into a managed buffer (a memory view of a
 * Span<byte> as long as the content).
 */
function copyContent(source, destination) {
    destination.set(source);
}

export function setupContent() {
    return {
        getBaseUrl: getBaseUrl,
        fetchContent: fetchContent,
        copyContent: copyContent
    };
}
