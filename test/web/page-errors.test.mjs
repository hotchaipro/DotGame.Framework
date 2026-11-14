// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Run with: node --test test/web

import assert from 'node:assert/strict';
import test from 'node:test';
import { classifyPageError } from '../../platform/webgl/wwwroot/_dotgame/page-errors.js';

const PAGE = 'https://game.example';

test('a stack on another origin is third-party', () => {
    const stack = 'Error: x\n    at track (https://ads.example/sdk.js:1:2)';
    assert.equal(classifyPageError(stack, '', PAGE), 'third-party');
});

test('Safari and Firefox stack frames are read too', () => {
    assert.equal(classifyPageError('track@https://ads.example/sdk.js:1:2', '', PAGE), 'third-party');
    assert.equal(classifyPageError('run@https://game.example/_dotgame/main.js:1:2', '', PAGE), 'own');
});

test('a frame on the page origin is its own, even below a third party', () => {
    const stack = 'Error: x\n    at a (https://ads.example/sdk.js:1:2)\n    at b (https://game.example/_dotgame/game.js:3:4)';
    assert.equal(classifyPageError(stack, '', PAGE), 'own');
});

test('a frame in the runtime is its own', () => {
    assert.equal(classifyPageError('at x (wasm://wasm/abc:wasm-function[1]:0x2)', '', PAGE), 'own');
    assert.equal(classifyPageError('at x (<anonymous>:wasm-function[1]:0x2)', '', PAGE), 'own');
});

test('no location is unknown, so it stays fatal', () => {
    assert.equal(classifyPageError('', '', PAGE), 'unknown');
    assert.equal(classifyPageError(undefined, undefined, PAGE), 'unknown');
    assert.equal(classifyPageError('Error: Script error.', '', PAGE), 'unknown');
    assert.equal(classifyPageError('at x (<anonymous>:1:1)', '', PAGE), 'unknown');
});

test('the filename alone attributes an error with no stack', () => {
    assert.equal(classifyPageError('', 'https://ads.example/sdk.js', PAGE), 'third-party');
    assert.equal(classifyPageError('', 'https://game.example/_dotgame/main.js', PAGE), 'own');
});

test('a failed fetch belongs to its caller, not the wrapper', () => {
    const fromThirdParty = 'TypeError: Failed to fetch\n    at instrumentedFetch (https://game.example/_dotgame/diagnostics.js:1:1)\nFetched from:\n    at send (https://analytics.example/a.js:1:1)';
    assert.equal(classifyPageError(fromThirdParty, '', PAGE), 'third-party');

    const fromGame = 'TypeError: Failed to fetch\n    at instrumentedFetch (https://game.example/_dotgame/diagnostics.js:1:1)\nFetched from:\n    at load (https://game.example/_dotgame/content.js:1:1)';
    assert.equal(classifyPageError(fromGame, '', PAGE), 'own');

    const noCaller = 'TypeError: Failed to fetch\n    at instrumentedFetch (https://game.example/_dotgame/diagnostics.js:1:1)\nFetched from:\n';
    assert.equal(classifyPageError(noCaller, '', PAGE), 'unknown');
});

test('a malformed location is not an attribution', () => {
    assert.equal(classifyPageError('', 'not a url', PAGE), 'unknown');
});