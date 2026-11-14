// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

import { trace } from './diagnostics.js'
import { resolveContentUrl, fetchWithRetry, precompressedUrl, readPrecompressed } from './content.js'

// The framework's SpriteVertex is 7 words (28 bytes); a sprite is 4 of them. Vertices arrive in the
// game's coordinates, and the sprite vertex shader's MatrixTransform projects them.
const VERTEX_STRIDE = 28;
const VERTICES_PER_SPRITE = 4;
const INDICES_PER_SPRITE = 6;

// Command opcodes. Matches FrameRecorder.
const CMD_DRAW = 1;
const CMD_SET_RENDER_STATE = 30;
const CMD_SET_RENDER_TARGET = 2;
const CMD_CLEAR = 3;
const CMD_SET_SCISSOR = 4;
const CMD_DISABLE_SCISSOR = 5;
const CMD_SET_SHADER = 6;
const CMD_SET_UNIFORM_FLOAT = 7;
const CMD_SET_UNIFORM_VEC2 = 8;
const CMD_SET_UNIFORM_INT = 9;
const CMD_SET_UNIFORM_VEC4 = 10;
const CMD_READBACK = 11;

// Resolves when a lost context is back, so a texture still decoding when it was lost uploads to
// the restored context rather than to none. Null while the context is fine.
let contextRestored = null;
let resolveContextRestored = null;

// A frame's counts, for the check that every frame still makes one call (test/web/browser-check.mjs).
// That the renderer's main path can disconnect unnoticed isn't hypothetical: it did, for four months.
const renderStats = { executes: 0, draws: 0, sprites: 0 };
globalThis.dgRenderStats = () => ({ ...renderStats });

// Whether this browser and device can create a WebGL 2 context, which the game
// needs (its shaders are GLSL ES 3.00). Checking for WebGL2RenderingContext
// isn't enough: a browser can have the API and still refuse a context, for
// example on a GPU or driver it blocks. The probe's context is released at once,
// so it doesn't count against the browser's limit on live contexts.
export function isWebGL2Supported() {
    try {
        const gl = document.createElement('canvas').getContext('webgl2');
        if (!gl) {
            return false;
        }

        const loseContext = gl.getExtension('WEBGL_lose_context');
        if (loseContext) {
            loseContext.loseContext();
        }

        return true;
    } catch {
        return false;
    }
}

export function setupGraphics() {
    return {
        _uniformLocationCache: new WeakMap(),
        _programContextMap: new Map(),
        currentRenderTexture: null,

        // Render texture pixels read back as a frame ran, by readback id, until C# takes them
        readbacks: new Map(),

        // Starts reading a render texture's pixels, top row first, into a pixel buffer, which the
        // GPU fills in its own time: a read into memory would stall the frame for the whole
        // pipeline. WebGL reads the bottom row first, and render textures are drawn upside down
        // (see the projection), so the first row it reads is the picture's top.
        _readRenderTexture: function(gl, rt) {
            const buffer = gl.createBuffer();
            gl.bindBuffer(gl.PIXEL_PACK_BUFFER, buffer);
            gl.bufferData(gl.PIXEL_PACK_BUFFER, rt.width * 4 * rt.height, gl.STREAM_READ);

            gl.bindFramebuffer(gl.FRAMEBUFFER, rt.framebuffer);
            gl.readPixels(0, 0, rt.width, rt.height, gl.RGBA, gl.UNSIGNED_BYTE, 0);
            gl.bindBuffer(gl.PIXEL_PACK_BUFFER, null);

            // Bind the target that was bound again
            const current = this.currentRenderTexture;
            gl.bindFramebuffer(gl.FRAMEBUFFER, current ? current.framebuffer : null);

            const fence = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
            gl.flush();

            return { gl, buffer, fence };
        },

        _compileShaderProgram: function(glContext, vertexSource, fragmentSource) {
            const gl = glContext;

            // A lost context creates nothing (null), and WebKit throws a TypeError on a null
            // shader where Chrome does nothing: fail with the reason instead, which callers
            // recognize by the context being lost
            const lost = () => new Error('Shader failed: the WebGL context was lost');

            const vertexShader = gl.createShader(gl.VERTEX_SHADER);
            if (!vertexShader) {
                throw lost();
            }
            gl.shaderSource(vertexShader, vertexSource);
            gl.compileShader(vertexShader);

            const fragmentShader = gl.createShader(gl.FRAGMENT_SHADER);
            if (!fragmentShader) {
                throw lost();
            }
            gl.shaderSource(fragmentShader, fragmentSource);
            gl.compileShader(fragmentShader);

            const program = gl.createProgram();
            if (!program) {
                throw lost();
            }
            gl.attachShader(program, vertexShader);
            gl.attachShader(program, fragmentShader);
            gl.linkProgram(program);

            // NOTE: Only the link's status is asked for: each status query waits for the driver,
            // which can compile in parallel if it's left alone. The stages' logs matter only on
            // failure.
            if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
                if (gl.isContextLost()) {
                    throw lost();
                }
                const vertexLog = gl.getShaderInfoLog(vertexShader);
                const fragmentLog = gl.getShaderInfoLog(fragmentShader);
                const programLog = gl.getProgramInfoLog(program);
                gl.deleteProgram(program);
                gl.deleteShader(vertexShader);
                gl.deleteShader(fragmentShader);
                throw new Error(`Shader failed: ${programLog} (vertex: ${vertexLog}; fragment: ${fragmentLog})`);
            }

            gl.deleteShader(vertexShader);
            gl.deleteShader(fragmentShader);

            // Set all sampler uniforms to texture unit 0
            gl.useProgram(program);
            const numUniforms = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
            for (let i = 0; i < numUniforms; i++) {
                const info = gl.getActiveUniform(program, i);
                if (info.type === gl.SAMPLER_2D) {
                    const loc = gl.getUniformLocation(program, info.name);
                    if (loc !== null) {
                        gl.uniform1i(loc, 0);
                    }
                }
            }

            return program;
        },

        getMultisampleCount: function(glContext) {
            return Math.max(glContext.getParameter(glContext.SAMPLES), 1);
        },

        getOrCreateWebGLContext: function(elementId, multisample) {
            const canvas = document.getElementById(elementId);
            if (!canvas) {
                throw new Error(`Canvas element '${elementId}' not found.`);
            }

            // NOTE: The canvas's size is game.js's (syncCanvasSize), which reports it to managed code
            // as ClientBounds. Sizing it here too, on a restored context, could change it without a
            // report (its own formula needn't agree: the observed device-pixel box against the CSS
            // box times the ratio), and the game then drew at the old size in a corner of the new.

            // Sprites need no depth buffer, and multisampling is the game's choice
            const glContext = canvas.getContext('webgl2', { antialias: multisample === true, depth: false, stencil: false });
            if (!glContext) {
                // main.js checks for WebGL 2 before starting, so this is a context
                // refused after that check passed (for example, the GPU process
                // failed in between). Fail with a message that says so, rather than
                // a null dereference.
                trace(0xf07c3e51, `webgl2 context unavailable for '${elementId}'`);
                throw new Error(`A WebGL 2 context couldn't be created for '${elementId}'.`);
            }

            const debugInfo = glContext.getExtension('WEBGL_debug_renderer_info');
            trace(0x2a95d6f8, `webgl2 context created for '${elementId}' ${canvas.width}x${canvas.height} dpr=${window.devicePixelRatio || 1} renderer='${debugInfo ? glContext.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) : glContext.getParameter(glContext.RENDERER)}'`);
            glContext.viewport(0, 0, canvas.width, canvas.height);

            return glContext;
        },

        Texture2D: class {
            constructor() {
                this.glTexture = null;
                this.width = 0;
                this.height = 0;
                this.glContext = null;
            }

            async loadFromContent(glContext, filePath) {
                this.glContext = glContext;

                // Fetch the image file
                const fullUrl = resolveContentUrl(filePath);
                const blob = await fetchWithRetry(fullUrl, undefined, async (response) => {
                    if (!response.ok) {
                        throw new Error(`Failed to fetch texture file '${filePath}' (${response.statusText})`);
                    }
                    return response.blob();
                });

                // Decode the image without premultiplying its alpha or converting its colour
                // space, so the texture holds exactly the values the content pipeline stored.
                // Textures that should be premultiplied were premultiplied at build time, and
                // data textures such as MSDF font atlases must not be altered. An <img> element
                // is decoded premultiplied by some browsers, which loses precision at low alpha.
                const bitmap = await createImageBitmap(blob, {
                    premultiplyAlpha: 'none',
                    colorSpaceConversion: 'none',
                });

                try {
                    // Lost while this decoded: upload to the context that comes back
                    while (glContext.isContextLost()) {
                        await contextRestored;
                    }

                    // Create WebGL texture
                    this.glTexture = glContext.createTexture();
                    glContext.bindTexture(glContext.TEXTURE_2D, this.glTexture);

                    // Set texture parameters
                    glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_WRAP_S, glContext.CLAMP_TO_EDGE);
                    glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_WRAP_T, glContext.CLAMP_TO_EDGE);
                    glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_MIN_FILTER, glContext.LINEAR);
                    glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_MAG_FILTER, glContext.LINEAR);

                    // These are the defaults, and are ignored for ImageBitmap sources by browsers
                    // that follow the current WebGL specification; set them for those that don't.
                    glContext.pixelStorei(glContext.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
                    glContext.pixelStorei(glContext.UNPACK_COLORSPACE_CONVERSION_WEBGL, glContext.NONE);

                    // Upload image to texture
                    glContext.texImage2D(glContext.TEXTURE_2D, 0, glContext.RGBA, glContext.RGBA, glContext.UNSIGNED_BYTE, bitmap);

                    this.width = bitmap.width;
                    this.height = bitmap.height;
                }
                finally {
                    bitmap.close();
                }
            }

            loadFromData(glContext, width, height, rgbaData) {
                this.glContext = glContext;
                this.width = width;
                this.height = height;

                this.glTexture = glContext.createTexture();
                glContext.bindTexture(glContext.TEXTURE_2D, this.glTexture);

                glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_WRAP_S, glContext.CLAMP_TO_EDGE);
                glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_WRAP_T, glContext.CLAMP_TO_EDGE);
                glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_MIN_FILTER, glContext.LINEAR);
                glContext.texParameteri(glContext.TEXTURE_2D, glContext.TEXTURE_MAG_FILTER, glContext.LINEAR);

                // NOTE: A view of C#'s memory, valid during this call, which texImage2D copies from
                glContext.texImage2D(glContext.TEXTURE_2D, 0, glContext.RGBA, width, height, 0, glContext.RGBA, glContext.UNSIGNED_BYTE, rgbaData._unsafe_create_view());

                glContext.bindTexture(glContext.TEXTURE_2D, null);
            }

            dispose() {
                if (this.glTexture && this.glContext) {
                    this.glContext.deleteTexture(this.glTexture);
                    this.glTexture = null;
                    this.glContext = null;
                }
            }
        },

        createTexture2D: function() {
            return new this.Texture2D();
        },

        createTextureFromData: function(glContext, width, height, rgbaData) {
            const texture = new this.Texture2D();
            texture.loadFromData(glContext, width, height, rgbaData);
            return texture;
        },

        setTextureData: function(texture, x, y, width, height, rgbaData) {
            const gl = texture.glContext;
            if (!texture.glTexture || !gl || gl.isContextLost()) {
                return;
            }

            gl.bindTexture(gl.TEXTURE_2D, texture.glTexture);

            // NOTE: A view of C#'s memory, valid during this call, which texSubImage2D copies from
            gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, width, height, gl.RGBA, gl.UNSIGNED_BYTE, rgbaData.slice());
            gl.bindTexture(gl.TEXTURE_2D, null);
        },

        loadTexture2DFromContent: async function(texture, glContext, filePath) {
            return await texture.loadFromContent(glContext, filePath);
        },

        unloadTexture2D: function(texture) {
            texture.dispose();
        },

        RenderTexture: class {
            constructor(glContext, width, height) {
                const gl = glContext;
                this.glContext = glContext;
                this.width = width;
                this.height = height;
                this.glTexture = null;
                this.framebuffer = null;

                // Lost: this is created again when the context is restored
                if (gl.isContextLost()) {
                    return;
                }

                // Create color texture attachment
                this.glTexture = gl.createTexture();
                gl.bindTexture(gl.TEXTURE_2D, this.glTexture);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

                // Create framebuffer and attach the color texture
                this.framebuffer = gl.createFramebuffer();
                gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
                gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.glTexture, 0);

                const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
                if (status !== gl.FRAMEBUFFER_COMPLETE) {
                    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
                    gl.bindTexture(gl.TEXTURE_2D, null);
                    gl.deleteFramebuffer(this.framebuffer);
                    gl.deleteTexture(this.glTexture);
                    this.framebuffer = null;
                    this.glTexture = null;
                    throw new Error(`RenderTexture framebuffer is incomplete: 0x${status.toString(16)}`);
                }

                // Restore default framebuffer and texture bindings
                gl.bindFramebuffer(gl.FRAMEBUFFER, null);
                gl.bindTexture(gl.TEXTURE_2D, null);
            }

            dispose() {
                if (this.glContext) {
                    this.glContext.deleteFramebuffer(this.framebuffer);
                    this.glContext.deleteTexture(this.glTexture);
                    this.framebuffer = null;
                    this.glTexture = null;
                    this.glContext = null;
                }
            }
        },

        // Draws what a frame recorded. All of a frame's vertices go up in one call, with no work per
        // sprite here, and no copy: the vertices are a view of the WebAssembly heap.
        FrameRenderer: class {
            constructor(glContext, graphics, defaultProgram) {
                const gl = glContext;
                this.glContext = glContext;
                this.graphics = graphics;
                this.defaultProgram = defaultProgram;
                this.resources = [];
                this.indexCapacity = 0; // sprites

                // Each render target's projection; a program has it once its version matches
                this.projection = new Float32Array(16);
                this.projectionVersion = 0;

                // Premultiplied alpha: dst = src + dst * (1 - src.alpha)
                gl.enable(gl.BLEND);
                gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
                gl.clearColor(0.0, 0.0, 0.0, 0.0);

                this.vao = gl.createVertexArray();
                gl.bindVertexArray(this.vao);

                this.vertexBuffer = gl.createBuffer();
                gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);

                // layout(location = 0) in vec4 position
                gl.enableVertexAttribArray(0);
                gl.vertexAttribPointer(0, 2, gl.FLOAT, false, VERTEX_STRIDE, 0);
                // layout(location = 1) in vec4 color: 4 normalized bytes
                gl.enableVertexAttribArray(1);
                gl.vertexAttribPointer(1, 4, gl.UNSIGNED_BYTE, true, VERTEX_STRIDE, 8);
                // layout(location = 2) in vec2 texCoord
                gl.enableVertexAttribArray(2);
                gl.vertexAttribPointer(2, 2, gl.FLOAT, false, VERTEX_STRIDE, 12);
                // layout(location = 3) in vec4 customData: 4 half floats
                gl.enableVertexAttribArray(3);
                gl.vertexAttribPointer(3, 4, gl.HALF_FLOAT, false, VERTEX_STRIDE, 20);

                this.indexBuffer = gl.createBuffer();
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
                this._ensureIndices(256);

                gl.bindVertexArray(null);
            }

            // The same quads' indices for every frame, 32-bit, so a batch has no size limit. The
            // vertex array is bound.
            _ensureIndices(spriteCount) {
                if (spriteCount <= this.indexCapacity) {
                    return;
                }

                const capacity = Math.max(spriteCount, this.indexCapacity * 2);
                const indices = new Uint32Array(capacity * INDICES_PER_SPRITE);
                for (let i = 0; i < capacity; i++) {
                    const vi = i * VERTICES_PER_SPRITE;
                    const ii = i * INDICES_PER_SPRITE;
                    indices[ii]     = vi;
                    indices[ii + 1] = vi + 1;
                    indices[ii + 2] = vi + 2;
                    indices[ii + 3] = vi;
                    indices[ii + 4] = vi + 2;
                    indices[ii + 5] = vi + 3;
                }

                const gl = this.glContext;
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
                gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
                this.indexCapacity = capacity;
            }

            // An orthographic projection for the current target, in pixels with the origin at the
            // top left. A render texture is drawn upside down, its top row first in memory, so it
            // samples the right way up when it's drawn like any other texture (texture row 0 is
            // at the top of a sprite); the canvas is the right way up on screen.
            _setTarget(gl) {
                const target = this.graphics.currentRenderTexture;
                const width = target ? target.width : gl.canvas.width;
                const height = target ? target.height : gl.canvas.height;
                const direction = target ? 1 : -1;

                const m = this.projection;
                m[0] = 2 / width;
                m[5] = direction * 2 / height;
                m[10] = -1;
                m[12] = -1;
                m[13] = -direction;
                m[15] = 1;
                this.projectionVersion++;

                gl.viewport(0, 0, width, height);
            }

            // The blend mode in the low bits, and the sampler above them. Colors are premultiplied.
            _setRenderState(gl, state) {
                const current = this.renderState ?? 0;
                if (state === current) {
                    return;
                }

                this.renderState = state;

                if ((state & 0xF) !== (current & 0xF)) {
                    switch (state & 0xF) {
                        case 1: gl.blendFuncSeparate(gl.ONE, gl.ONE, gl.ONE, gl.ONE); break;
                        case 2: gl.blendFuncSeparate(gl.DST_COLOR, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA); break;
                        case 3: gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_COLOR, gl.ONE, gl.ONE_MINUS_SRC_ALPHA); break;
                        case 4: gl.blendFuncSeparate(gl.ONE, gl.ZERO, gl.ONE, gl.ZERO); break;
                        default: gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA); break;
                    }
                }

                if ((state >> 4) !== (current >> 4)) {
                    gl.bindSampler(0, this._getSampler(gl, state >> 4));
                }
            }

            // Textures are made for the usual case, so it needs no sampler object
            _getSampler(gl, sampler) {
                if (sampler === 0) {
                    return null;
                }

                this.samplers ??= [];
                if (!this.samplers[sampler]) {
                    const isPoint = (sampler === 1) || (sampler === 3);
                    const isRepeat = sampler >= 2;
                    const object = gl.createSampler();
                    const filter = isPoint ? gl.NEAREST : gl.LINEAR;
                    const wrap = isRepeat ? gl.REPEAT : gl.CLAMP_TO_EDGE;
                    gl.samplerParameteri(object, gl.TEXTURE_MIN_FILTER, filter);
                    gl.samplerParameteri(object, gl.TEXTURE_MAG_FILTER, filter);
                    gl.samplerParameteri(object, gl.TEXTURE_WRAP_S, wrap);
                    gl.samplerParameteri(object, gl.TEXTURE_WRAP_T, wrap);
                    this.samplers[sampler] = object;
                }

                return this.samplers[sampler];
            }

            execute(vertices, spriteCount, commandBytes, commandCount) {
                const gl = this.glContext;
                const graphics = this.graphics;
                const resources = this.resources;
                const commands = new Float32Array(commandBytes.buffer, commandBytes.byteOffset, commandCount);

                renderStats.executes++;
                renderStats.sprites = spriteCount;

                gl.bindVertexArray(this.vao);
                this._ensureIndices(spriteCount);

                // NOTE: bufferData, for new storage rather than overwriting what the last frame's
                // draws may still be using
                if (spriteCount > 0) {
                    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
                    gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STREAM_DRAW);
                }

                gl.activeTexture(gl.TEXTURE0);
                this._setTarget(gl);
                this._setRenderState(gl, 0);

                let program = this.defaultProgram;
                let usedProgram = null;
                let boundTexture = null;
                let draws = 0;

                let i = 0;
                while (i < commandCount) {
                    switch (commands[i]) {
                        case CMD_DRAW: {
                            const texture = resources[commands[i + 1]];
                            const start = commands[i + 2];
                            const count = commands[i + 3];
                            i += 4;

                            if (!texture || !texture.glTexture) {
                                break;
                            }

                            if (program !== usedProgram) {
                                gl.useProgram(program);
                                usedProgram = program;
                            }

                            if (program.dgProjectionVersion !== this.projectionVersion) {
                                if (program.dgMatrixLocation === undefined) {
                                    program.dgMatrixLocation = gl.getUniformLocation(program, 'MatrixTransform');
                                }

                                gl.uniformMatrix4fv(program.dgMatrixLocation, false, this.projection);
                                program.dgProjectionVersion = this.projectionVersion;
                            }

                            if (texture.glTexture !== boundTexture) {
                                gl.bindTexture(gl.TEXTURE_2D, texture.glTexture);
                                boundTexture = texture.glTexture;
                            }

                            gl.drawElements(gl.TRIANGLES, count * INDICES_PER_SPRITE, gl.UNSIGNED_INT, start * INDICES_PER_SPRITE * 4);
                            draws++;
                            break;
                        }

                        case CMD_SET_RENDER_STATE:
                            this._setRenderState(gl, commands[i + 1]);
                            i += 2;
                            break;

                        case CMD_SET_RENDER_TARGET: {
                            const index = commands[i + 1];
                            i += 2;

                            const target = (index < 0) ? null : resources[index];
                            gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.framebuffer : null);
                            graphics.currentRenderTexture = target;
                            this._setTarget(gl);
                            break;
                        }

                        case CMD_CLEAR:
                            gl.clearColor(commands[i + 1], commands[i + 2], commands[i + 3], commands[i + 4]);
                            gl.clear(gl.COLOR_BUFFER_BIT);
                            i += 5;
                            break;

                        case CMD_SET_SCISSOR:
                            gl.enable(gl.SCISSOR_TEST);
                            graphics._setScissor(gl, commands[i + 1], commands[i + 2], commands[i + 3], commands[i + 4]);
                            i += 5;
                            break;

                        case CMD_DISABLE_SCISSOR:
                            gl.disable(gl.SCISSOR_TEST);
                            i += 1;
                            break;

                        case CMD_SET_SHADER: {
                            const index = commands[i + 1];
                            i += 2;
                            program = (index < 0) ? this.defaultProgram : resources[index];
                            break;
                        }

                        // [cmd, program, location, values...]: a location applies only to its own
                        // program, which needn't be the one drawing now
                        case CMD_SET_UNIFORM_FLOAT:
                            usedProgram = resources[commands[i + 1]];
                            gl.useProgram(usedProgram);
                            gl.uniform1f(resources[commands[i + 2]], commands[i + 3]);
                            i += 4;
                            break;

                        case CMD_SET_UNIFORM_VEC2:
                            usedProgram = resources[commands[i + 1]];
                            gl.useProgram(usedProgram);
                            gl.uniform2f(resources[commands[i + 2]], commands[i + 3], commands[i + 4]);
                            i += 5;
                            break;

                        case CMD_SET_UNIFORM_INT:
                            usedProgram = resources[commands[i + 1]];
                            gl.useProgram(usedProgram);
                            gl.uniform1i(resources[commands[i + 2]], commands[i + 3]);
                            i += 4;
                            break;

                        case CMD_SET_UNIFORM_VEC4:
                            usedProgram = resources[commands[i + 1]];
                            gl.useProgram(usedProgram);
                            gl.uniform4f(resources[commands[i + 2]], commands[i + 3], commands[i + 4], commands[i + 5], commands[i + 6]);
                            i += 7;
                            break;

                        case CMD_READBACK: {
                            // [cmd, render texture, readback id]: the pixels as drawn so far
                            const target = resources[commands[i + 1]];
                            graphics.readbacks.set(commands[i + 2], graphics._readRenderTexture(gl, target));
                            i += 3;
                            break;
                        }

                        default:
                            throw new Error(`Unknown frame command ${commands[i]}`);
                    }
                }

                gl.bindVertexArray(null);

                renderStats.draws = draws;
                return draws;
            }
        },

        frameRenderer: null,

        createFrameRenderer: function(glContext, defaultProgram) {
            this.frameRenderer = new this.FrameRenderer(glContext, this, defaultProgram);
        },

        setFrameResource: function(index, resource) {
            this.frameRenderer.resources[index] = resource;
        },

        clearFrameResources: function() {
            if (this.frameRenderer) {
                this.frameRenderer.resources = [];
            }
        },

        executeFrame: function(vertices, spriteCount, commands, commandCount) {
            return this.frameRenderer.execute(vertices._unsafe_create_view(), spriteCount, commands._unsafe_create_view(), commandCount);
        },

        isContextLost: function(glContext) {
            return glContext.isContextLost();
        },

        // Sets the scissor from a rectangle with a top-left origin. gl.scissor's origin is the
        // bottom left of the target: the canvas's, which is the right way up, so the rectangle
        // flips; a render texture is drawn upside down (its top row first), so it doesn't.
        _setScissor: function(gl, x, y, width, height) {
            if (this.currentRenderTexture) {
                gl.scissor(x, y, width, height);
            } else {
                gl.scissor(x, gl.canvas.height - y - height, width, height);
            }
        },

        createRenderTexture: function(glContext, width, height) {
            return new this.RenderTexture(glContext, width, height);
        },

        // Copies a readback's pixels into C#'s memory and forgets them, if the GPU has made them:
        // 1 if it has, 0 if it's still to, and -1 if they'll never come (a lost context)
        takeReadback: function(readbackId, destination) {
            const readback = this.readbacks.get(readbackId);
            if (readback === undefined) {
                return -1;
            }

            const { gl, buffer, fence } = readback;
            let result = -1;
            if (!gl.isContextLost()) {
                const status = gl.clientWaitSync(fence, 0, 0);
                if (status === gl.TIMEOUT_EXPIRED) {
                    return 0;
                }

                if (status !== gl.WAIT_FAILED) {
                    gl.bindBuffer(gl.PIXEL_PACK_BUFFER, buffer);
                    gl.getBufferSubData(gl.PIXEL_PACK_BUFFER, 0, destination._unsafe_create_view());
                    gl.bindBuffer(gl.PIXEL_PACK_BUFFER, null);
                    result = 1;
                }
            }

            gl.deleteSync(fence);
            gl.deleteBuffer(buffer);
            this.readbacks.delete(readbackId);
            return result;
        },

        // NOTE: Fetches the precompressed copy where the publish made one, as fetchContent does: the
        // page's shader preloads point at that copy, and the plain file would download it again
        loadShaderSource: async function(filePath) {
            const fullUrl = resolveContentUrl(filePath);
            const fetched = precompressedUrl(fullUrl);
            return fetchWithRetry(fetched, undefined, async (response) => {
                if (!response.ok) {
                    throw new Error(`Failed to load shader file '${filePath}' (${response.statusText})`);
                }
                const decoded = (fetched === fullUrl) ? response : readPrecompressed(response, fullUrl);
                return decoded.text();
            });
        },

        compileShaderProgram: function(glContext, vertexSource, fragmentSource) {
            const program = this._compileShaderProgram(glContext, vertexSource, fragmentSource);
            if (program) {
                this._programContextMap.set(program, glContext);
            }
            return program;
        },

        deleteShaderProgram: function(program) {
            const gl = this._programContextMap.get(program);
            if (gl) {
                gl.deleteProgram(program);
                this._programContextMap.delete(program);
            }
        },

        _getUniformLocation: function(glContext, program, name) {
            let programCache = this._uniformLocationCache.get(program);
            if (!programCache) {
                programCache = new Map();
                this._uniformLocationCache.set(program, programCache);
            }
            let location = programCache.get(name);
            if (location === undefined) {
                location = glContext.getUniformLocation(program, name);
                programCache.set(name, location);
            }
            return location;
        },

        getUniformLocation: function(glContext, program, name) {
            return this._getUniformLocation(glContext, program, name);
        },

        handleContextLost: function() {
            this._uniformLocationCache = new WeakMap();
            this._programContextMap.clear();
            this.currentRenderTexture = null;
            this.frameRenderer = null;

            if (!contextRestored) {
                contextRestored = new Promise((resolve) => { resolveContextRestored = resolve; });
            }
        },

        handleContextRestored: function() {
            const resolve = resolveContextRestored;
            contextRestored = null;
            resolveContextRestored = null;
            resolve?.();
        }
    };
}

