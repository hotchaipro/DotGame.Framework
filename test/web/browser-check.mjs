// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Checks a running web build in headless Edge or Chrome over the DevTools protocol: the renderer makes
// one call per frame, the failure policy holds, and the game survives losing its WebGL context.
// Start the sample (dotnet run in sample/webgl), then: node test/web/browser-check.mjs [url] [screenshot.png]
// Exits nonzero if a check fails. See test/README.md.
import { writeFileSync } from 'node:fs';

import { findBrowser, launchBrowser } from './headless-browser.mjs';

const url = process.argv[2] ?? 'http://localhost:61531/';
const screenshotPath = process.argv[3];

// Longer than a healthy run by far: a page that stops answering ends here, not never
const TimeoutMilliseconds = 5 * 60 * 1000;
setTimeout(() => {
    console.error(`FAIL the check didn't finish within ${TimeoutMilliseconds / 60000} minutes`);
    process.exit(2);
}, TimeoutMilliseconds).unref();

if (!findBrowser()) {
    console.error('No browser found: set BROWSER to one with the DevTools protocol.');
    process.exit(2);
}

const { port } = await launchBrowser({
    args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--window-size=800,600'],
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; (i < 40) && !target; i++) {
    await sleep(500);
    try {
        const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
        target = list.find((t) => t.type === 'page');
    } catch { /* not up yet */ }
}

if (!target) {
    console.error('FAIL the browser started but offered no page');
    process.exit(2);
}

const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => { ws.onopen = r; });
let id = 0;
const pending = new Map();
const errors = [];
ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg);
        pending.delete(msg.id);
    } else if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
        errors.push(msg.params.args.map((a) => a.value ?? a.description).join(' ').slice(0, 300));
    } else if (msg.method === 'Runtime.exceptionThrown') {
        errors.push('exception: ' + (msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text).slice(0, 300));
    }
};
const send = (method, params = {}) => new Promise((r) => { const n = ++id; pending.set(n, r); ws.send(JSON.stringify({ id: n, method, params })); });
const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.result?.value;

const results = [];
const check = (name, passed, detail) => {
    results.push({ name, passed, detail });
    console.log(`${passed ? 'ok  ' : 'FAIL'} ${name}${(detail === undefined) ? '' : `: ${JSON.stringify(detail)}`}`);
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url });

const frames = () => evaluate('dgFrameStats().frames');

// NOTE: Frames run while the game loads, drawing nothing until it has started
for (let i = 0; i < 120; i++) {
    await sleep(500);
    if ((await evaluate('globalThis.dgRenderStats?.().executes')) > 10) {
        break;
    }
}

const dialog = () => evaluate("!!document.getElementById('dg-crash-backdrop')");

// One execute per frame: the renderer's main path staying connected
const counted = await evaluate(`new Promise((resolve) => {
    const start = dgRenderStats().executes;
    let frames = 0;
    const tick = () => {
        if (++frames === 60) {
            resolve({ frames, executes: dgRenderStats().executes - start });
        } else {
            requestAnimationFrame(tick);
        }
    };
    requestAnimationFrame(tick);
})`);
check('one execute per frame', Math.abs(counted.executes - counted.frames) <= 2, counted);

const stats = await evaluate('dgRenderStats()');
check('sprites are drawn', (stats.sprites > 0) && (stats.draws > 0), stats);

const attributes = await evaluate("JSON.stringify((({antialias,depth,stencil})=>({antialias,depth,stencil}))(document.querySelector('canvas').getContext('webgl2').getContextAttributes()))");
check('multisampling as the sample asks, and no depth', attributes === JSON.stringify({ antialias: true, depth: false, stencil: false }), attributes);

const glError = await evaluate("document.querySelector('canvas').getContext('webgl2').getError()");
check('no GL error', glError === 0, glError);

if (screenshotPath) {
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(screenshotPath, Buffer.from(shot.result.data, 'base64'));
}

// A lost context comes back, and drawing resumes
await evaluate("globalThis.dgLoseContext = document.querySelector('canvas').getContext('webgl2').getExtension('WEBGL_lose_context'); dgLoseContext.loseContext()");
await sleep(1000);
await evaluate('dgLoseContext.restoreContext()');
let restored = false;
for (let i = 0; (i < 40) && !restored; i++) {
    await sleep(250);
    const a = await evaluate('dgRenderStats().executes');
    await sleep(250);
    const b = await evaluate('dgRenderStats().executes');
    restored = (b > a) && ((await evaluate('dgRenderStats().sprites')) > 0);
}
check('recovers from a lost context', restored && !(await dialog()), { restored });
check('no GL error after recovery', (await evaluate("document.querySelector('canvas').getContext('webgl2').getError()")) === 0);

if (screenshotPath) {
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(screenshotPath.replace(/\.png$/, '-restored.png'), Buffer.from(shot.result.data, 'base64'));
}

// Third-party errors don't end the game
await evaluate(`(() => { const e = new Error('third-party rejection'); e.stack = 'Error: x\\n    at send (https://ads.example/sdk.js:1:2)'; Promise.reject(e);
    const t = new TypeError('Failed to fetch'); t.dgFetchUrl = 'https://analytics.example/a'; t.stack = 'TypeError: Failed to fetch\\n    at instrumentedFetch (${url}_dotgame/diagnostics.js:1:1)\\nFetched from:\\n    at send (https://analytics.example/a.js:1:1)'; Promise.reject(t);
    globalThis.dispatchEvent(new ErrorEvent('error', { message: 'third-party error', filename: 'https://ads.example/sdk.js', error: e })); })()`);
await sleep(1500);
const f1 = await frames();
await sleep(1000);
const f2 = await frames();
check('third-party errors leave the game running', !(await dialog()) && !(await evaluate("document.body.innerText.includes(\"Couldn't load\")")) && (f2 > f1), { frames: f2 - f1 });

// An error of unknown source stays fatal, and stops the game behind the dialog
await evaluate("(() => { const e = new Error('Script error.'); e.stack = ''; Promise.reject(e); })()");
await sleep(1500);
const f3 = await frames();
await sleep(1500);
const f4 = await frames();
check('an unknown error shows the dialog and stops frames', (await dialog()) && (f4 === f3), { frames: f4 - f3 });

const unexpected = errors.filter((e) => !e.includes('Script error') && !e.includes('third-party'));
console.log(`console errors: ${errors.length}`);
for (const e of unexpected) {
    console.log(`  ${e}`);
}

ws.close();
process.exit(results.every((r) => r.passed) ? 0 : 1);
