// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Run with: node --test test/web

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const modules = join(root, 'platform', 'webgl', 'wwwroot', '_dotgame');

function importGraph(name, found = new Set()) {
    if (found.has(name)) {
        return found;
    }

    found.add(name);
    const source = readFileSync(join(modules, name), 'utf8');
    for (const match of source.matchAll(/from\s+'\.\/([\w-]+\.js)'/g)) {
        importGraph(match[1], found);
    }

    return found;
}

test("the page's module preloads are main.js's import graph", () => {
    const targets = readFileSync(join(root, 'pipeline', 'ContentPipeline.webgl.targets'), 'utf8');
    const listed = /<WebStartupModule Include="([^"]+)"/.exec(targets)[1]
        .split(';')
        .map(path => path.replace('_dotgame/', ''));

    assert.deepEqual([...listed].sort(), [...importGraph('main.js')].sort());
});
