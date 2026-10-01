#pragma once
#include <stddef.h>
#include <stdint.h>

// Sampling profiler for the game's main thread (measurement builds, MONO_NX_PROFILER=1).
// See nx_profiler.c for the method and /mono/prof.bin's format.
void nx_profiler_start(void);
void nx_profiler_stop(void);
// Also sample one other thread (e.g. Mesa's glthread worker, from its own thread); flag 8.
// Mesa declares it weak, so builds without the profiler still link.
void nx_profiler_add_thread(uint32_t thread_handle);

// Scans a copied stack (8-byte words, leaf first) for return addresses: values inside
// [code_start, code_end) whose preceding instruction is BL or BLR. code_copy gives read access to
// the code at those addresses (the live code on Switch, a fake buffer in the host test).
// Writes up to max offsets from code_start and returns how many.
size_t nx_profiler_scan(const uint64_t *words, size_t count, uint64_t code_start, uint64_t code_end,
    const uint8_t *code_copy, uint32_t *out, size_t max);
