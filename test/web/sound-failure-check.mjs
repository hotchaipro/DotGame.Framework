// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// A sound that can't download asks the player to check their connection and try again, with no
// crash dialog, and a touch on Try Again reloads the page, as the page's input handling can cancel
// what makes a tap a click. A sound that downloads but won't decode (a format the browser doesn't
// have, such as Ogg Opus before Safari 18.4) stays silent: it's traced, and the game carries on
// with no message and no crash.
//
// Start a web build with sounds, then: node test/web/sound-failure-check.mjs <url> <part of a sound's url>
// Exits nonzero if a check fails.
import { launchBrowser } from './headless-browser.mjs';

const url = process.argv[2];
const soundUrlPart = process.argv[3];
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let failures = 0;

function check(name, actual, expected) {
    const passed = actual === expected;
    failures += passed ? 0 : 1;
    console.log(`${passed ? 'ok  ' : 'FAIL'} ${name}${passed ? '' : `: expected ${expected}, got ${actual}`}`);
}

async function run(name, prepare, decodeFails = false) {
    const { port, stop } = await launchBrowser({ args: ['--window-size=720,1280'], url: 'about:blank' });
    try {
        const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
        const ws = new WebSocket(targets.find((t) => t.type === 'page').webSocketDebuggerUrl);
        await new Promise((resolve) => { ws.onopen = resolve; });

        let id = 0;
        const pending = new Map();
        const consoleLines = [];
        ws.onmessage = (event) => {
            const message = JSON.parse(event.data);
            if (message.method === 'Runtime.consoleAPICalled') {
                consoleLines.push(message.params.args.map((arg) => String(arg.value)).join(' '));
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
        await send('Network.enable');
        await prepare(send);
        await send('Page.navigate', { url: decodeFails ? `${url}${url.includes('?') ? '&' : '?'}dgTrace=1` : url });

        if (decodeFails) {
            let traced = false;
            for (let i = 0; (i < 60) && !traced; i++) {
                await sleep(1000);
                traced = consoleLines.some((line) => line.includes("can't decode audio") && line.includes(soundUrlPart));
            }
            check(`${name}: the sound is traced as one that won't play`, traced, true);

            // Long enough for a connection message to have shown, had it gone that way
            await sleep(5000);
            check(`${name}: no connection message shows`, await evaluate(`document.getElementById('dg-connection-backdrop') === null`), true);
            check(`${name}: no crash is recorded`, await evaluate(`(globalThis.dgCrashes ? globalThis.dgCrashes().length : 0)`), 0);
            return;
        }

        let shown = false;
        for (let i = 0; (i < 60) && !shown; i++) {
            await sleep(1000);
            shown = await evaluate(`document.getElementById('dg-connection-backdrop') !== null`);
        }

        check(`${name}: the connection message shows`, shown, true);
        check(`${name}: no crash is recorded`, await evaluate(`(globalThis.dgCrashes ? globalThis.dgCrashes().length : 0)`), 0);

        await evaluate(`globalThis.dgNotReloaded = true`);
        const rect = await evaluate(`(() => {
            const r = document.querySelector('#dg-connection-backdrop button').getBoundingClientRect();
            return { x: r.x + (r.width / 2), y: r.y + (r.height / 2) };
        })()`);
        await send('Emulation.setTouchEmulationEnabled', { enabled: true });
        await send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [rect] });
        await send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });

        let reloaded = false;
        for (let i = 0; (i < 10) && !reloaded; i++) {
            await sleep(500);
            reloaded = (await evaluate(`globalThis.dgNotReloaded === true`)) !== true;
        }
        check(`${name}: a touch on Try Again reloads`, reloaded, true);
    } finally {
        await stop();
    }
}

await run('download fails', (send) => send('Network.setBlockedURLs', { urls: [`*${soundUrlPart}*`] }));

await run('decode fails', (send) => send('Page.addScriptToEvaluateOnNewDocument', {
    source: `BaseAudioContext.prototype.decodeAudioData = () => Promise.reject(new DOMException('Unable to decode audio data', 'EncodingError'));`
}), true);

if (failures > 0) {
    console.log(`${failures} failed`);
    process.exit(1);
}
console.log('All passed');
