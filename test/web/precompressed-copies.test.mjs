// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Run with: node --test test/web
//
// How the page reads a file's precompressed copy (content.js's readPrecompressed) as each host and
// network sends it: with Content-Encoding: br, in another encoding the browser decoded, plain (a CDN
// or a proxy decoded it), or as the copy's Brotli bytes without the header (a host that isn't set up).

import assert from 'node:assert/strict';
import test from 'node:test';
import { brotliCompressSync, gzipSync } from 'node:zlib';

const ORIGIN = 'https://game.example/';
const moduleUrl = new URL('../../platform/webgl/wwwroot/_dotgame/content.js', import.meta.url).href;

// The page the module reads at load: the publish's meta tag, and what diagnostics.js touches
const storage = { getItem: () => null, setItem: () => { }, removeItem: () => { } };
let format = 'br';
globalThis.window = globalThis;
globalThis.localStorage = storage;
globalThis.location = { href: ORIGIN, search: '', hostname: 'game.example', origin: 'https://game.example' };
globalThis.document = {
    baseURI: ORIGIN,
    hidden: false,
    addEventListener() { },
    removeEventListener() { },
    querySelector(selector) {
        return selector.includes('dotgame-precompressed')
            ? { content: format, dataset: { extensions: '.glsl .json .spritefont .wasm' } }
            : null;
    },
    querySelectorAll: () => [],
};

// Every fetch the module makes goes to the current test's host
let host = () => new Response(null, { status: 404 });
let fetched = [];
globalThis.fetch = async (url) => {
    fetched.push(String(url));
    return host(String(url));
};

const errors = [];
console.error = (message) => errors.push(String(message));
console.warn = () => { };

// A fresh module for each test: what the module has learned about the host is per page
let instance = 0;
async function freshModule(withFormat = 'br') {
    format = withFormat;
    fetched = [];
    errors.length = 0;
    instance += 1;
    return import(`${moduleUrl}?case=${instance}`);
}

const encoder = new TextEncoder();
const shader = encoder.encode('#ifdef FRAGMENT\nprecision mediump float;\nvoid main() { gl_FragColor = vec4(1.0); }\n#endif\n');
const font = Uint8Array.from([0x7b, 0x01, 0x01, 0x01, 0x02, 0x04, 0x6d, 0x73, 0x64, 0x66, 0x00, 0x00, 0x00, 0x80, 0xff, 0x10]);
const wasm = Uint8Array.from([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00, 0x01, 0x04]);
const brotli = (bytes) => new Uint8Array(brotliCompressSync(bytes));

const SHADER = `${ORIGIN}Content/Shaders/sprite.0e12.glsl`;
const FONT = `${ORIGIN}Content/Fonts/base.d2c4.spritefont`;
const FONT2 = `${ORIGIN}Content/Fonts/title.9a1b.spritefont`;
const WASM = `${ORIGIN}_framework/dotnet.native.gdxe.wasm`;

// A host that serves each file as itself, and each copy as `copy` says
function serving(files, copy) {
    return (url) => {
        if (url.endsWith('.br')) {
            const file = files[url.slice(0, -3)];
            return file ? copy(file, url) : new Response(null, { status: 404 });
        }
        const file = files[url];
        return file ? new Response(file) : new Response(null, { status: 404 });
    };
}

// A copy as a decoding CDN or proxy hands it to the page: the file, with no encoding
const plain = (file) => new Response(file);
// A copy as a host that isn't set up sends it: the Brotli bytes, with no encoding
const unlabeled = (file) => new Response(brotli(file));

async function read(content, url, response) {
    return new Uint8Array(await content.readPrecompressed(response, url).arrayBuffer());
}

async function copyOf(content, url) {
    return content.readPrecompressed(await fetch(`${url}.br`), url);
}

test('a copy sent with Content-Encoding: br is the file the browser decoded', async () => {
    const content = await freshModule();
    const response = new Response(shader, { headers: { 'Content-Encoding': 'br' } });
    assert.deepEqual(await read(content, SHADER, response), shader);
});

test('a copy in another encoding is the file the browser decoded', async () => {
    const content = await freshModule();
    const response = new Response(font, { headers: { 'Content-Encoding': 'gzip' } });
    assert.deepEqual(await read(content, FONT, response), font);
});

test('a plain shader arriving first, before any wasm, is the file', async () => {
    const content = await freshModule();
    host = serving({ [SHADER]: shader }, plain);
    const decoded = await copyOf(content, SHADER);
    assert.equal(await decoded.text(), new TextDecoder().decode(shader));
    assert.deepEqual(fetched, [`${SHADER}.br`], 'a text file is told by its bytes, without fetching it again');
    assert.equal(errors.length, 0);
});

test('a plain shader is the file even after a wasm arrived as Brotli', async () => {
    const content = await freshModule();
    host = serving({ [SHADER]: shader }, plain);
    const wasmResponse = new Response(wasm, { headers: { 'Content-Encoding': 'br' } });
    assert.deepEqual(await read(content, WASM, wasmResponse), wasm);
    assert.equal(await (await copyOf(content, SHADER)).text(), new TextDecoder().decode(shader));
});

test('a plain binary file arriving first is fetched again, and is the file when the two match', async () => {
    const content = await freshModule();
    host = serving({ [FONT]: font, [FONT2]: font }, plain);
    assert.deepEqual(new Uint8Array(await (await copyOf(content, FONT)).arrayBuffer()), font);
    assert.deepEqual(fetched, [`${FONT}.br`, FONT]);

    // That showed the host decodes copies for this browser: the next one isn't fetched again
    fetched = [];
    assert.deepEqual(new Uint8Array(await (await copyOf(content, FONT2)).arrayBuffer()), font);
    assert.deepEqual(fetched, [`${FONT2}.br`]);
});

test('a plain wasm is the module, and then a plain binary file is the file', async () => {
    const content = await freshModule();
    host = serving({ [FONT]: font }, plain);
    assert.deepEqual(await read(content, WASM, new Response(wasm)), wasm);
    assert.deepEqual(new Uint8Array(await (await copyOf(content, FONT)).arrayBuffer()), font);
    assert.deepEqual(fetched, [`${FONT}.br`]);
});

test('a text file that doesn\'t read as text is fetched again, whatever a wasm showed', async () => {
    const content = await freshModule();
    await read(content, WASM, new Response(wasm));
    host = serving({ [SHADER]: shader }, unlabeled);
    await assert.rejects(async () => (await copyOf(content, SHADER)).text(), /without the header Content-Encoding: br/);
    assert.deepEqual(fetched, [`${SHADER}.br`, SHADER]);
});

test('a host that isn\'t set up stops the game at a text file, naming it', async () => {
    const content = await freshModule();
    host = serving({ [SHADER]: shader }, unlabeled);
    await assert.rejects(async () => (await copyOf(content, SHADER)).text(),
        (error) => error.message.includes(`'${SHADER}' without the header Content-Encoding: br`));
    assert.equal(errors.length, 1, 'the host setup is reported once, on the console');
});

test('a host that isn\'t set up stops the game at a binary file', async () => {
    const content = await freshModule();
    host = serving({ [FONT]: font }, unlabeled);
    await assert.rejects(async () => (await copyOf(content, FONT)).arrayBuffer(), /without the header Content-Encoding: br/);
});

test('a host that isn\'t set up stops the game at a wasm', async () => {
    const content = await freshModule();
    await assert.rejects(async () => read(content, WASM, new Response(brotli(wasm))), /without the header Content-Encoding: br/);
});

test('a plain binary copy whose file can\'t be fetched to compare stops the game', async () => {
    const content = await freshModule();
    host = (url) => url.endsWith('.br') ? new Response(font) : new Response(null, { status: 404 });
    await assert.rejects(async () => (await copyOf(content, FONT)).arrayBuffer(), /without the header Content-Encoding: br/);
});

test('an empty text file is the file', async () => {
    const content = await freshModule();
    assert.equal(await content.readPrecompressed(new Response(new Uint8Array(0)), SHADER).text(), '');
});

test('Brotli bytes don\'t read as text', async () => {
    const content = await freshModule();
    assert.equal(content.readsAsText(shader), true);
    assert.equal(content.readsAsText(brotli(shader)), false);
    assert.equal(content.readsAsText(brotli(encoder.encode('{"textures":[{"name":"card","x":0,"y":0}]}'))), false);
});

test('with the fallback, a gzip copy the host sent without the header is decoded in the page', async () => {
    const content = await freshModule('gz');
    const response = new Response(new Uint8Array(gzipSync(font)));
    assert.deepEqual(await read(content, FONT, response), font);
});

test('with the fallback, a gzip copy the host sent as gzip is the file the browser decoded', async () => {
    const content = await freshModule('gz');
    const response = new Response(font, { headers: { 'Content-Encoding': 'gzip' } });
    assert.deepEqual(await read(content, FONT, response), font);
});
