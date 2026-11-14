// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
// The same functions as the Mono runtime's llvm-runtime.cpp (MIT, .NET Foundation), compiled with
// WebAssembly exception handling as first shipped (WebAssembly.LegacyExceptions.targets): the
// runtime pack's copy (libmono-wasm-eh-wasm.a) is compiled with the final form, and is the only
// part of the runtime that is. Linked as an object, these are the definitions the link uses, so the
// archive's member isn't pulled in.
//
// Exported as the runtime pack's are (EMSCRIPTEN_KEEPALIVE): the runtime's JavaScript calls the
// catch, and the jiterpreter imports begin_catch and end_catch into the code it generates, looking
// them up among the wasm's exports. Without the export, the first trace with a catch failed ("raw
// cwrap mono_jiterp_begin_catch not found").

#include <cstdio>
#include <emscripten/emscripten.h>

extern "C" {

typedef int gboolean;
typedef void (*MonoLLVMInvokeCallback) (void *arg);

gboolean mono_trace_is_enabled (void);
void *__cxa_begin_catch (void *exception) noexcept;
void __cxa_end_catch (void);

// Throws the exception the AOT code catches: an int32 pointer
void mono_llvm_cpp_throw_exception (void)
{
    int *ex = nullptr;
    if (mono_trace_is_enabled ())
        fprintf (stderr, "Native Stacktrace (mono_llvm_cpp_throw_exception):\n");
    throw ex;
}

// Calls cb (arg), and says whether it threw the AOT code's exception
EMSCRIPTEN_KEEPALIVE void mono_llvm_cpp_catch_exception (MonoLLVMInvokeCallback cb, void *arg, gboolean *out_thrown)
{
    *out_thrown = 0;
    try {
        cb (arg);
    } catch (int *ex) {
        *out_thrown = 1;
    }
}

// The jiterpreter's catch, from its generated code
EMSCRIPTEN_KEEPALIVE void mono_jiterp_begin_catch (void *ptr)
{
    __cxa_begin_catch (ptr);
}

EMSCRIPTEN_KEEPALIVE void mono_jiterp_end_catch (void)
{
    __cxa_end_catch ();
}

}
