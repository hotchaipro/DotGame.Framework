// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// A headless browser for a script, which leaves nothing behind however the script ends: success,
// failure, an exception, a timeout, Ctrl+C, or the caller killing it.
//
// A browser's launcher exits at once, or leaves its children running if it's killed, so a browser
// is only ever identified by its own temporary profile, which every one of its processes names on
// its command line. That also leaves the user's own browser alone. A killed script runs no code,
// so a small watchdog process outlives it to clean up (browser-watchdog.mjs).
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ProfileVariable = 'HC_BROWSER_PROFILE';
const candidates = [
    process.env.BROWSER,
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome',
];

const sleepSync = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function findBrowser() {
    return candidates.find((path) => path && existsSync(path));
}

// NOTE: The profile goes in the environment, so these commands can't match themselves
function runWithProfile(command, args, profile) {
    return spawnSync(command, args, {
        env: { ...process.env, [ProfileVariable]: profile },
        encoding: 'utf8',
        windowsHide: true,
    });
}

const windowsMatch = `Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($env:${ProfileVariable}) -and ($_.ProcessId -ne $PID) }`;

/** How many processes name this profile on their command line. */
export function countProfileProcesses(profile) {
    if (process.platform === 'win32') {
        const result = runWithProfile('powershell', ['-NoProfile', '-NonInteractive', '-Command', `@(${windowsMatch}).Count`], profile);
        return Number(result.stdout.trim()) || 0;
    }

    const result = runWithProfile('sh', ['-c', `pgrep -f -- "$${ProfileVariable}" | wc -l`], profile);
    return Number(result.stdout.trim()) || 0;
}

/** Ends every process of this profile, and removes the profile unless told to keep it. */
export function destroyProfile(profile, keepProfile = false) {
    for (let attempt = 0; attempt < 5; attempt++) {
        if (process.platform === 'win32') {
            runWithProfile('powershell', ['-NoProfile', '-NonInteractive', '-Command',
                `${windowsMatch} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`], profile);
        } else {
            runWithProfile('sh', ['-c', `pkill -9 -f -- "$${ProfileVariable}"`], profile);
        }

        if (countProfileProcesses(profile) === 0) {
            break;
        }

        sleepSync(300);
    }

    if (keepProfile) {
        return;
    }

    // NOTE: Windows holds a dead process's files for a moment
    for (let attempt = 0; attempt < 20; attempt++) {
        try {
            rmSync(profile, { recursive: true, force: true });
            return;
        } catch {
            sleepSync(250);
        }
    }
}

const guarded = new Set();
let isHandlingExit = false;

function stopAll() {
    for (const stop of [...guarded]) {
        stop();
    }
}

function installExitHandlers() {
    if (isHandlingExit) {
        return;
    }

    isHandlingExit = true;
    process.on('exit', stopAll);
    for (const [signal, code] of [['SIGINT', 130], ['SIGTERM', 143], ['SIGHUP', 129], ['SIGBREAK', 131]]) {
        process.on(signal, () => process.exit(code));
    }

    // Without these, Node's own handling ends the process, which does run 'exit' handlers, but
    // only with these does an unattended script say why, and exit nonzero
    process.on('uncaughtException', (error) => {
        console.error(error);
        process.exit(1);
    });
    process.on('unhandledRejection', (error) => {
        console.error(error);
        process.exit(1);
    });
}

/**
 * Makes sure a profile's browser ends with this process. Returns the function to end it now.
 * keepProfile leaves the folder, for a tool that reuses one.
 */
export function guardProfile(profile, { keepProfile = false } = {}) {
    installExitHandlers();

    // The watchdog runs until this marker goes (we stopped cleanly) or this process does
    const marker = `${profile}.guard`;
    writeFileSync(marker, String(process.pid));
    spawn(process.execPath, [join(dirname(fileURLToPath(import.meta.url)), 'browser-watchdog.mjs')], {
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
        env: {
            ...process.env,
            [ProfileVariable]: profile,
            HC_BROWSER_PARENT: String(process.pid),
            HC_BROWSER_KEEP_PROFILE: keepProfile ? '1' : '',
        },
    }).unref();

    let isStopped = false;
    const stop = () => {
        if (isStopped) {
            return;
        }

        isStopped = true;
        guarded.delete(stop);
        destroyProfile(profile, keepProfile);
        rmSync(marker, { force: true });
    };
    guarded.add(stop);
    return stop;
}

function isProcessAlive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    } catch (error) {
        return error.code === 'EPERM';
    }
}

/**
 * Ends the browsers of scripts that are gone along with their watchdogs, as when a whole process
 * tree is killed: the browser isn't in the tree by then, since its launcher has exited. A script
 * that's still running keeps its browser, whichever session it belongs to.
 */
export function removeAbandonedProfiles() {
    for (const name of readdirSync(tmpdir())) {
        if (!name.startsWith('dg-browser-') || !name.endsWith('.guard')) {
            continue;
        }

        const marker = join(tmpdir(), name);
        let owner = 0;
        try {
            owner = Number(readFileSync(marker, 'utf8'));
        } catch {
            continue;
        }

        if ((owner > 0) && !isProcessAlive(owner)) {
            destroyProfile(marker.slice(0, -'.guard'.length));
            rmSync(marker, { force: true });
        }
    }
}

/**
 * Starts a browser on a new profile and a free port. The port comes from the file the browser
 * writes in its own profile, so this can't connect to any other browser.
 */
export async function launchBrowser({ browserPath = findBrowser(), args = [], headless = true, url = 'about:blank' } = {}) {
    if (!browserPath) {
        throw new Error('No browser found: set BROWSER to one with the DevTools protocol.');
    }

    removeAbandonedProfiles();

    const profile = mkdtempSync(join(tmpdir(), 'dg-browser-'));
    const stop = guardProfile(profile);
    spawn(browserPath, [
        ...(headless ? ['--headless=new'] : []),
        '--remote-debugging-port=0',
        `--user-data-dir=${profile}`,
        '--no-first-run',
        '--no-default-browser-check',
        ...args,
        url,
    ], { stdio: 'ignore', windowsHide: true });

    const portFile = join(profile, 'DevToolsActivePort');
    for (let attempt = 0; attempt < 120; attempt++) {
        // NOTE: On Windows the file is locked while the browser writes it
        let port = 0;
        try {
            port = Number(readFileSync(portFile, 'utf8').split('\n')[0]);
        } catch (error) {
            if ((error.code !== 'ENOENT') && (error.code !== 'EBUSY') && (error.code !== 'EPERM')) {
                stop();
                throw error;
            }
        }

        if (port > 0) {
            return { port, profile, stop };
        }

        await sleep(250);
    }

    stop();
    throw new Error('The browser did not start within 30 seconds.');
}
