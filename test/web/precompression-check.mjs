// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// A published web build fetches the precompressed copies of its files (docs/guide/platforms.md,
// "Hosting"): from a host that sends them with Content-Encoding the game starts, and the copies
// arrived compressed and were decoded by the browser; from one that doesn't, the game stops with
// the error that says so, naming the file, unless the publish has the fallback, which starts too.
//
// Publish a web game, then: node test/web/precompression-check.mjs <publish folder>/wwwroot <serve.mjs>
// (serve.mjs: a script that serves a publish as your host does, started as `node <serve.mjs> <wwwroot>
// --port <port> [--no-encoding]`, --no-encoding sending the copies without Content-Encoding, and
// printing a line once it listens). Exits nonzero if a check fails.
//
// With --host <url> in place of serve.mjs, it checks a host already serving that publish as it will
// be deployed (a preview deploy): there the game must start, no file it fetched may be missing, and
// every copy must decode to its file, which catches a CDN that compresses a copy again. Check a real
// deploy: a local stand-in doesn't compress as the CDN does (wrangler dev re-encodes every response
// itself, as gzip for a browser), and passes or fails for its own reasons.
import { spawn } from 'node:child_process';
import { readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { launchBrowser } from './headless-browser.mjs';

const root = process.argv[2];
const hostUrl = (process.argv[3] === '--host') ? process.argv[4] : null;
const serveScript = hostUrl ? null : process.argv[3];
if (!root || (!hostUrl && !serveScript) || (process.argv[3] === '--host' && !hostUrl)) {
    console.error('usage: node test/web/precompression-check.mjs <wwwroot> (<serve.mjs> | --host <url>)');
    process.exit(2);
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let failures = 0;

function check(name, actual, expected) {
    const passed = actual === expected;
    failures += passed ? 0 : 1;
    console.log(`${passed ? 'ok  ' : 'FAIL'} ${name}${passed ? '' : `: expected ${expected}, got ${actual}`}`);
}

const page = readFileSync(join(root, 'index.html'), 'utf8');
const format = /<meta name="dotgame-precompressed" content="(br|gz)"/.exec(page)?.[1] ?? null;
check('the published page names its copies', format !== null, true);

async function serve(port, extraArgs) {
    const server = spawn(process.execPath, [serveScript, root, '--port', String(port), ...extraArgs], { stdio: 'pipe' });
    await new Promise((resolve) => server.stdout.once('data', resolve));
    return server;
}

async function load(name, port, extraArgs, url = `http://127.0.0.1:${port}/`) {
    const server = hostUrl ? null : await serve(port, extraArgs);
    const { port: debugPort, stop } = await launchBrowser({ url: 'about:blank' });
    try {
        const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
        const ws = new WebSocket(targets.find((t) => t.type === 'page').webSocketDebuggerUrl);
        await new Promise((resolve) => { ws.onopen = resolve; });
        let id = 0;
        const pending = new Map();
        const consoleLines = [];
        ws.onmessage = (event) => {
            const message = JSON.parse(event.data);
            if (message.method === 'Runtime.consoleAPICalled') {
                consoleLines.push(`${message.params.type}: ${message.params.args.map((arg) => String(arg.value ?? arg.description)).join(' ')}`);
            }
            if (message.method === 'Runtime.exceptionThrown') {
                consoleLines.push(`exception: ${message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text}`);
            }
            if (message.id && pending.has(message.id)) {
                pending.get(message.id)(message.result);
                pending.delete(message.id);
            }
        };
        const send = (method, params = {}) => new Promise((resolve) => {
            const n = ++id;
            pending.set(n, resolve);
            ws.send(JSON.stringify({ id: n, method, params }));
        });
        const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result.value;

        await send('Page.enable');
        await send('Runtime.enable');
        await send('Page.navigate', { url });

        let state = null;
        for (let i = 0; (i < 120) && !state; i++) {
            await sleep(500);
            state = await evaluate(`(() => {
                if (document.getElementById('dg-crash-backdrop')) return 'crashed';
                return performance.getEntriesByType('mark').some((m) => m.name.endsWith(':first-frame')) ? 'started' : null;
            })()`);
        }
        const crashText = await evaluate(`document.getElementById('dg-crash-backdrop')?.innerText ?? ''`);
        const copies = await evaluate(`JSON.stringify(performance.getEntriesByType('resource')
            .filter((e) => /\\.(br|gz)$/.test(new URL(e.name).pathname))
            .map((e) => ({ name: e.name, encoded: e.encodedBodySize, decoded: e.decodedBodySize })))`);
        const missing = await evaluate(`JSON.stringify(performance.getEntriesByType('resource')
            .filter((e) => e.responseStatus >= 400).map((e) => e.responseStatus + ' ' + e.name))`);
        if (process.env.PRECOMPRESSION_CHECK_VERBOSE) {
            console.log(`--- ${name}: ${state}`);
            console.log(consoleLines.filter((l) => !l.startsWith('debug')).slice(-15).join('\n'));
        }
        return { state, crashText, copies: JSON.parse(copies ?? '[]'), missing: JSON.parse(missing ?? '[]') };
    } finally {
        await stop();
        server?.kill();
    }
}

// A copy decoded to anything but its file (still compressed: a host that compressed it again and
// sent only its own layer's encoding) is the wrong bytes
function wronglyDecoded(copies) {
    return copies.filter((c) => {
        const original = decodeURIComponent(new URL(c.name).pathname).replace(/\.(br|gz)$/, '');
        return statSync(join(root, original)).size !== c.decoded;
    }).map((c) => `${c.name} (${c.decoded} bytes)`);
}

const configured = await load(hostUrl ? 'host' : 'configured', 8471, [], hostUrl ?? undefined);
check('from a host set up for the copies, the game starts', configured.state, 'started');
check('it fetched copies, content and the runtime among them',
    configured.copies.some((c) => c.name.includes('/Content/')) && configured.copies.some((c) => c.name.includes('/_framework/')), true);
check('every file it fetched was there', configured.missing.join(', '), '');
if (format === 'br') {
    check('every copy arrived as Brotli and decoded to its file',
        wronglyDecoded(configured.copies).join(', '), '');
}

if (hostUrl) {
    if (failures > 0) {
        console.log(`${failures} failed`);
        process.exit(1);
    }
    console.log('All passed');
    process.exit(0);
}

const unconfigured = await load('unconfigured', 8472, ['--no-encoding']);
if (format === 'br') {
    check('from a host that isn\'t set up, the game stops with an error', unconfigured.state, 'crashed');
    check('the error names the header to send', unconfigured.crashText.includes('Content-Encoding: br'), true);
} else {
    check('with the fallback, from a host that isn\'t set up, the game starts', unconfigured.state, 'started');
}

if (failures > 0) {
    console.log(`${failures} failed`);
    process.exit(1);
}
console.log('All passed');
