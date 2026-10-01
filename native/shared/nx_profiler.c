// Sampling profiler for the game's main thread (measurement builds, MONO_NX_PROFILER=1).
// One extra thread (Mesa's glthread worker) can be registered with nx_profiler_add_thread;
// it is sampled the same way right after the main thread and its samples carry flag 8.
//
// A native thread on core 2 wakes every 2.5 ms (400 Hz), pauses the main thread with
// svcSetThreadActivity, reads its registers with svcGetThreadContext3, copies up to 96 KB of its
// stack, and resumes it. Nothing that can take a lock (malloc, stdio, Mono) runs while the main
// thread is paused. After resuming, the copy is scanned for return addresses: a frame-pointer
// walk would skip LLVM-compiled methods, which save x29 without setting it. Stale return
// addresses left in live frames can appear as extra callers; the leaf PC is always exact.
//
// /mono/prof.bin, little-endian:
//   header: "NXPROF1\0", u64 code_start, u64 code_size, u64 tick_freq, u32 interval_us,
//           u32 profiler_scan_offset (ELF address of nx_profiler_scan, to check the base)
//   sample: u64 tick, u32 pc, u32 lr, u32 stack_depth, u16 count, u16 flags, u32 frames[count]
//   pc/lr/frames are offsets from code_start (= ELF addresses); 0xffffffff = outside the code.
//   frames: the innermost INNER_FRAMES and outermost OUTER_FRAMES return addresses found.
//   flags: 1 = stack deeper than the copy, 2 = pc outside the code, 4 = middle frames elided,
//          8 = the registered extra thread (not the main thread).
// Every 10 s the log gets an NX_PROF line (samples, failures, pause cost, stack depth).

#include "nx_profiler.h"
#include <string.h>

static int is_call(uint32_t insn)
{
    return (insn & 0xFC000000u) == 0x94000000u     // BL imm26
        || (insn & 0xFFFFFC1Fu) == 0xD63F0000u;    // BLR Xn
}

size_t nx_profiler_scan(const uint64_t *words, size_t count, uint64_t code_start, uint64_t code_end,
    const uint8_t *code_copy, uint32_t *out, size_t max)
{
    size_t n = 0;
    for (size_t i = 0; i < count && n < max; ++i) {
        uint64_t w = words[i];
        if (w < code_start + 4 || w >= code_end || (w & 3))
            continue;
        uint32_t insn;
        memcpy(&insn, code_copy + (w - code_start) - 4, sizeof insn);
        if (!is_call(insn))
            continue;
        uint32_t offset = (uint32_t)(w - code_start);
        if (n && out[n - 1] == offset)
            continue;
        out[n++] = offset;
    }
    return n;
}

#ifndef NX_PROFILER_HOST
#include <stdbool.h>
#include <stdio.h>
#include <switch.h>
#include <io_util.h>

#define PROF_PATH "/mono/prof.bin"
#define INTERVAL_NS 2500000ull
#define STACK_COPY_BYTES (96 * 1024)
#define SCAN_FRAMES 512
#define INNER_FRAMES 40
#define OUTER_FRAMES 16
#define MAX_FRAMES (INNER_FRAMES + OUTER_FRAMES)
#define BUFFER_BYTES (512 * 1024)
#define OUTSIDE 0xffffffffu

typedef struct {
    uint64_t tick;
    uint32_t pc, lr, stack_depth;
    uint16_t count, flags;
} SampleHeader;
_Static_assert(sizeof(SampleHeader) == 24, "sample header layout");

static Thread prof_thread;
static volatile bool prof_stop;
static bool prof_running;
static FILE *prof_file;
static Handle main_thread;
static volatile Handle extra_thread;
static uint64_t code_start, code_end;
static uint64_t stack_copy[STACK_COPY_BYTES / 8];
static uint32_t scanned[SCAN_FRAMES];
static uint8_t buffer[BUFFER_BYTES];
static size_t buffer_used;
static uint64_t samples, pause_failures, context_failures, truncated, bytes_written;
static uint64_t pause_ticks_sum, pause_ticks_max, depth_max;
static uint64_t window_samples, window_pause_sum, window_pause_max;

static uint32_t code_offset(uint64_t address)
{
    return address >= code_start && address < code_end ? (uint32_t)(address - code_start) : OUTSIDE;
}

static void flush_buffer(void)
{
    if (!buffer_used)
        return;
    fwrite(buffer, 1, buffer_used, prof_file);
    fflush(prof_file);
    bytes_written += buffer_used;
    buffer_used = 0;
}

static void sample_once(Handle thread, uint16_t thread_flag)
{
    ThreadContext context;
    size_t copied = 0;
    uint64_t depth = 0;
    Result rc = svcSetThreadActivity(thread, ThreadActivity_Paused);
    if (R_FAILED(rc)) {
        if (pause_failures++ == 0)
            io_debugf("NX_PROF pause failed rc=0x%x", rc);
        return;
    }
    uint64_t paused = armGetSystemTick();
    Result context_rc = svcGetThreadContext3(&context, thread);
    if (R_SUCCEEDED(context_rc)) {
        MemoryInfo info;
        u32 page_info;
        if (R_SUCCEEDED(svcQueryMemory(&info, &page_info, context.sp))
            && context.sp >= info.addr && context.sp < info.addr + info.size) {
            depth = info.addr + info.size - context.sp;
            copied = depth > STACK_COPY_BYTES ? STACK_COPY_BYTES : depth;
            memcpy(stack_copy, (const void *)context.sp, copied & ~(size_t)7);
        }
    }
    svcSetThreadActivity(thread, ThreadActivity_Runnable);
    uint64_t pause = armGetSystemTick() - paused;
    if (R_FAILED(context_rc)) {
        if (context_failures++ == 0)
            io_debugf("NX_PROF svcGetThreadContext3 failed rc=0x%x", context_rc);
        return;
    }

    ++samples;
    ++window_samples;
    pause_ticks_sum += pause;
    window_pause_sum += pause;
    if (pause > pause_ticks_max) pause_ticks_max = pause;
    if (pause > window_pause_max) window_pause_max = pause;
    // Main thread only: a pthread stack lives inside a larger heap mapping, so its depth is meaningless.
    if (!thread_flag && depth > depth_max) depth_max = depth;
    if (!thread_flag && depth > STACK_COPY_BYTES) ++truncated;

    if (buffer_used + sizeof(SampleHeader) + MAX_FRAMES * 4 > BUFFER_BYTES)
        flush_buffer();
    SampleHeader header = {
        .tick = paused, .pc = code_offset(context.pc.x), .lr = code_offset(context.lr),
        .stack_depth = depth > UINT32_MAX ? UINT32_MAX : (uint32_t)depth,
    };
    // Keep both ends: the leaf side for attribution, the outer side for Draw/Update phase.
    size_t found = nx_profiler_scan(stack_copy, copied / 8, code_start, code_end,
        (const uint8_t *)code_start, scanned, SCAN_FRAMES);
    uint8_t *frames = buffer + buffer_used + sizeof header;
    if (found <= MAX_FRAMES) {
        memcpy(frames, scanned, found * 4);
        header.count = (uint16_t)found;
    } else {
        memcpy(frames, scanned, INNER_FRAMES * 4);
        memcpy(frames + INNER_FRAMES * 4, scanned + found - OUTER_FRAMES, OUTER_FRAMES * 4);
        header.count = MAX_FRAMES;
    }
    header.flags = (!thread_flag && depth > STACK_COPY_BYTES ? 1 : 0) | (header.pc == OUTSIDE ? 2 : 0)
        | (found > MAX_FRAMES ? 4 : 0) | thread_flag;
    memcpy(buffer + buffer_used, &header, sizeof header);
    buffer_used += sizeof header + header.count * 4u;
}

static void report(uint64_t start, bool final)
{
    double us_per_tick = 1e6 / (double)armGetSystemTickFreq();
    io_debugf("NX_PROF%s elapsed=%.1fs samples=%llu window_samples=%llu pause_us avg/max=%.1f/%.1f "
        "window_pause_us avg/max=%.1f/%.1f pause_fail=%llu context_fail=%llu stack_max_kb=%llu "
        "truncated=%llu written_mb=%.1f",
        final ? " final" : "", (double)(armGetSystemTick() - start) / armGetSystemTickFreq(),
        (unsigned long long)samples, (unsigned long long)window_samples,
        samples ? pause_ticks_sum * us_per_tick / samples : 0.0, pause_ticks_max * us_per_tick,
        window_samples ? window_pause_sum * us_per_tick / window_samples : 0.0, window_pause_max * us_per_tick,
        (unsigned long long)pause_failures, (unsigned long long)context_failures,
        (unsigned long long)(depth_max >> 10), (unsigned long long)truncated,
        (bytes_written + buffer_used) / 1048576.0);
    window_samples = window_pause_sum = window_pause_max = 0;
}

static void profiler_worker(void *arg)
{
    (void)arg;
    uint64_t start = armGetSystemTick();
    uint64_t next_report = start + 10 * armGetSystemTickFreq();
    while (!prof_stop) {
        sample_once(main_thread, 0);
        if (extra_thread)
            sample_once(extra_thread, 8);
        if (context_failures > 100 || pause_failures > 100) {
            io_debugf("NX_PROF stopping: the kernel refuses thread pause/context reads");
            break;
        }
        uint64_t now = armGetSystemTick();
        if (now >= next_report) {
            report(start, false);
            next_report = now + 10 * armGetSystemTickFreq();
        }
        svcSleepThread(INTERVAL_NS);
    }
    flush_buffer();
    report(start, true);
}

void nx_profiler_start(void)
{
    MemoryInfo info;
    u32 page_info;
    if (R_FAILED(svcQueryMemory(&info, &page_info, (u64)(uintptr_t)&nx_profiler_scan))) {
        io_debugf("NX_PROF disabled: cannot query the code segment");
        return;
    }
    code_start = info.addr;
    code_end = info.addr + info.size;
    main_thread = envGetMainThreadHandle();
    prof_file = fopen(PROF_PATH, "wb");
    if (!prof_file) {
        io_debugf("NX_PROF disabled: cannot open %s", PROF_PATH);
        return;
    }
    uint8_t header[40] = "NXPROF1";
    uint64_t code_size = code_end - code_start, freq = armGetSystemTickFreq();
    uint32_t interval_us = (uint32_t)(INTERVAL_NS / 1000);
    uint32_t scan_offset = code_offset((u64)(uintptr_t)&nx_profiler_scan);
    memcpy(header + 8, &code_start, 8);
    memcpy(header + 16, &code_size, 8);
    memcpy(header + 24, &freq, 8);
    memcpy(header + 32, &interval_us, 4);
    memcpy(header + 36, &scan_offset, 4);
    fwrite(header, 1, sizeof header, prof_file);
    // Above the game's 0x2C so samples are on time; core 2 keeps it off the main thread's core.
    Result rc = threadCreate(&prof_thread, profiler_worker, NULL, NULL, 0x4000, 0x2B, 2);
    if (R_SUCCEEDED(rc)) {
        rc = threadStart(&prof_thread);
        if (R_FAILED(rc))
            threadClose(&prof_thread);
    }
    prof_running = R_SUCCEEDED(rc);
    if (!prof_running) {
        io_debugf("NX_PROF disabled: thread failed rc=0x%x", rc);
        fclose(prof_file);
        prof_file = NULL;
        return;
    }
    io_debugf("NX_PROF sampling the main thread every %u us into %s (code 0x%llx size 0x%llx, scan at 0x%x)",
        interval_us, PROF_PATH, (unsigned long long)code_start, (unsigned long long)code_size, scan_offset);
}

void nx_profiler_add_thread(Handle thread)
{
    if (!prof_running || extra_thread)
        return;
    extra_thread = thread;
    io_debugf("NX_PROF also sampling thread handle 0x%x (flag 8)", thread);
}

void nx_profiler_stop(void)
{
    if (!prof_running)
        return;
    prof_stop = true;
    threadWaitForExit(&prof_thread);
    threadClose(&prof_thread);
    fclose(prof_file);
    prof_running = false;
}
#endif
