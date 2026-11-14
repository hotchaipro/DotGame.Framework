// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Starts a browser, says which, then ends as browser-cleanup.test.mjs asks.
import { launchBrowser } from '../headless-browser.mjs';

const { profile } = await launchBrowser();
console.log(`profile=${profile}`);

switch (process.argv[2]) {
    case 'pass':
        process.exit(0);
        break;
    case 'fail':
        process.exit(1);
        break;
    case 'throw':
        throw new Error('expected by the test');
    case 'reject':
        Promise.reject(new Error('expected by the test'));
        await new Promise(() => { });
        break;
    default:
        // Until it's killed
        setInterval(() => { }, 1000);
        break;
}
