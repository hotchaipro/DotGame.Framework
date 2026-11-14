// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Run with: node --test test/web

// However a script with a browser ends, nothing of the browser stays. Each run looks only for
// processes of its own profile, so a browser the user has open doesn't count, and isn't touched.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { countProfileProcesses, findBrowser, launchBrowser } from './headless-browser.mjs';

const fixture = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'browser-guard-fixture.mjs');
const skip = findBrowser() ? false : 'no browser here';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function runFixture(mode, end) {
    const child = spawn(process.execPath, [fixture, mode], { stdio: ['ignore', 'pipe', 'ignore'] });
    const exited = new Promise((resolve) => child.on('exit', resolve));
    const profile = await new Promise((resolve, reject) => {
        let output = '';
        child.stdout.on('data', (data) => {
            output += data;
            const match = /profile=(.+)\r?\n/.exec(output);
            if (match) {
                resolve(match[1]);
            }
        });
        exited.then(() => reject(new Error(`The fixture ended first: ${output}`)));
    });

    await end?.(child, profile);
    const code = await exited;

    // The watchdog polls, so a killed script's browser takes it a moment
    for (let i = 0; (i < 60) && (existsSync(profile) || (countProfileProcesses(profile) > 0)); i++) {
        await sleep(500);
    }

    assert.equal(countProfileProcesses(profile), 0, 'browser processes are left');
    assert.equal(existsSync(profile), false, 'the profile is left');
    assert.equal(existsSync(`${profile}.guard`), false, 'the marker is left');
    return code;
}

async function killOnceRunning(child, profile, signal) {
    for (let i = 0; (i < 40) && (countProfileProcesses(profile) === 0); i++) {
        await sleep(250);
    }

    assert.ok(countProfileProcesses(profile) > 0, 'the browser should be running');
    child.kill(signal);
}

test('a script that succeeds leaves nothing', { skip }, async () => {
    assert.equal(await runFixture('pass'), 0);
});

test('a script that fails leaves nothing', { skip }, async () => {
    assert.equal(await runFixture('fail'), 1);
});

test('an exception leaves nothing, and exits nonzero', { skip }, async () => {
    assert.equal(await runFixture('throw'), 1);
});

test('an unhandled rejection leaves nothing, and exits nonzero', { skip }, async () => {
    assert.equal(await runFixture('reject'), 1);
});

test('an interrupted script leaves nothing', { skip }, async () => {
    await runFixture('hang', (child, profile) => killOnceRunning(child, profile, 'SIGINT'));
});

test('a killed script leaves nothing', { skip }, async () => {
    await runFixture('hang', (child, profile) => killOnceRunning(child, profile, 'SIGKILL'));
});

test('a browser whose script and watchdog were both killed goes at the next launch', { skip }, async () => {
    // A process that has ended, for the script that owned this browser
    const owner = spawn(process.execPath, ['-e', '']);
    await new Promise((resolve) => owner.on('exit', resolve));

    const profile = mkdtempSync(join(tmpdir(), 'dg-browser-'));
    writeFileSync(`${profile}.guard`, String(owner.pid));
    spawn(findBrowser(), ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], {
        stdio: 'ignore',
        windowsHide: true,
    });
    for (let i = 0; (i < 40) && (countProfileProcesses(profile) === 0); i++) {
        await sleep(250);
    }

    assert.ok(countProfileProcesses(profile) > 0, 'the browser should be running');

    const next = await launchBrowser();
    next.stop();

    assert.equal(countProfileProcesses(profile), 0, 'browser processes are left');
    assert.equal(existsSync(profile), false, 'the profile is left');
    assert.equal(existsSync(`${profile}.guard`), false, 'the marker is left');
});

test('two browsers at once get their own ports and profiles', { skip }, async () => {
    const first = await launchBrowser();
    const second = await launchBrowser();
    try {
        assert.notEqual(first.port, second.port);
        assert.notEqual(first.profile, second.profile);
    } finally {
        first.stop();
        second.stop();
    }

    assert.equal(countProfileProcesses(first.profile), 0);
    assert.equal(countProfileProcesses(second.profile), 0);
});
