// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Cleans up a script's browser when the script is gone without having done it, as when it's killed,
// which runs none of its code. See headless-browser.mjs.
import { existsSync, rmSync } from 'node:fs';

import { destroyProfile } from './headless-browser.mjs';

const profile = process.env.HC_BROWSER_PROFILE;
const parent = Number(process.env.HC_BROWSER_PARENT);
const keepProfile = process.env.HC_BROWSER_KEEP_PROFILE === '1';
const marker = `${profile}.guard`;

function isParentAlive() {
    try {
        process.kill(parent, 0);
        return true;
    } catch {
        return false;
    }
}

const timer = setInterval(() => {
    if (!existsSync(marker)) {
        clearInterval(timer);
    } else if (!isParentAlive()) {
        clearInterval(timer);
        destroyProfile(profile, keepProfile);
        rmSync(marker, { force: true });
    }
}, 500);
