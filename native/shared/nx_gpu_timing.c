// GPU frame timing and clock logging (measurement builds, MONO_NX_GPU_TIMING=1).
//
// nx_input.c's FNA3D_SwapBuffers wrapper calls nx_gpu_frame_end() just before the real swap
// and nx_gpu_frame_begin() just after it, on the GL thread. Each writes a GL_TIMESTAMP query
// (ARB_timer_query); results are read RING-2 frames later and only once available, so this
// never waits on the GPU. Per frame:
//   span   = end - begin: GPU time from the first to the last command of the frame (busy plus
//            any bubbles where the GPU waited for the CPU to submit more);
//   gap    = begin - previous end: the swap/blit plus any idle time between frames;
//   period = end - previous end: should average the CPU frame time (sanity check).
// GPU-bound: gap is near 0 and span is near period. CPU-bound: gap and bubbles grow.
// The Tegra X1 timestamps are not ns: on hardware (build 78) the period was 10.24 "ms" at a
// locked 60 fps and 12.8 at 48 fps, both a factor of 31.25/19.2 MHz = 625/384 short. Reports
// are scaled by TICK_SCALE.
// Every report also logs apm's performance mode/configuration and the CPU/GPU/EMC clocks.
// It also logs the swapchain (NX_SWAP; needs --wrap=nwindowDequeueBuffer/nwindowQueueBuffer):
// dequeues per slot, how many came back with an unsignaled release fence, the time blocked on
// it, the time from the previous queue to the dequeue returning, and how often the display
// reported more than one pending frame.
// NX_SWAP_INTERVAL (env, experiment) overrides the swap interval the game asks for. With 0 the
// display takes the newest queued frame at each refresh instead of queueing them, so the release
// fence never waits a refresh; queueing is then paced to at most 60 per second here, because
// Terraria with frame skip off runs one update per frame.

#include "nx_gpu_timing.h"
#include "io_util.h"

#include <switch.h>
#include <SDL2/SDL.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>

#define GL_TIMESTAMP 0x8E28
#define GL_QUERY_COUNTER_BITS 0x8864
#define GL_QUERY_RESULT 0x8866
#define GL_QUERY_RESULT_AVAILABLE 0x8867
#define RING 8
#define TICK_SCALE (625.0 / 384.0)

typedef void (*GenQueries)(int, unsigned *);
typedef void (*QueryCounter)(unsigned, unsigned);
typedef void (*GetQueryiv)(unsigned, unsigned, int *);
typedef void (*GetQueryObjectiv)(unsigned, unsigned, int *);
typedef void (*GetQueryObjectui64v)(unsigned, unsigned, uint64_t *);

static GenQueries gen_queries;
static QueryCounter query_counter;
static GetQueryObjectiv get_query_objectiv;
static GetQueryObjectui64v get_query_objectui64v;
static bool init_done, enabled;
static unsigned begin_q[RING], end_q[RING];
static bool issued[RING];
static uint64_t frame;      // frame being recorded (begin issued, end not yet)
static uint64_t next_read;  // oldest frame whose results are not read yet
static uint64_t last_end;
static bool have_last_end;
static uint64_t n, span_sum, span_max, gap_sum, period_sum, period_n, lost;

#define SWAP_SLOTS 8
static uint64_t sw_slot[SWAP_SLOTS], sw_deq, sw_blocked, sw_wait_ns, sw_wait_max, sw_lat_ns, sw_lat_n, sw_behind;
static uint64_t sw_last_queue;

static bool clocks_ready;
static ClkrstSession cpu_s, gpu_s, emc_s;
static bool cpu_ok, gpu_ok, emc_ok;

static void init_clocks(void)
{
    clocks_ready = true;
    Result rc = apmInitialize();
    if (R_FAILED(rc))
        io_debugf("NX_GPU apmInitialize failed rc=0x%x", rc);
    rc = clkrstInitialize();
    if (R_FAILED(rc)) {
        io_debugf("NX_GPU clkrstInitialize failed rc=0x%x", rc);
        return;
    }
    cpu_ok = R_SUCCEEDED(clkrstOpenSession(&cpu_s, PcvModuleId_CpuBus, 3));
    gpu_ok = R_SUCCEEDED(clkrstOpenSession(&gpu_s, PcvModuleId_GPU, 3));
    emc_ok = R_SUCCEEDED(clkrstOpenSession(&emc_s, PcvModuleId_EMC, 3));
    if (gpu_ok) {
        u32 rates[32];
        s32 count = 0;
        PcvClockRatesListType type;
        if (R_SUCCEEDED(clkrstGetPossibleClockRates(&gpu_s, rates, 32, &type, &count))) {
            char text[400];
            int len = 0;
            for (s32 i = 0; i < count && len < (int)sizeof text - 16; ++i)
                len += snprintf(text + len, sizeof text - len, "%s%.1f", i ? "," : "", rates[i] / 1e6);
            text[len] = 0;
            io_debugf("NX_GPU possible GPU clocks (list type %d, MHz): %s", (int)type, count ? text : "-");
        }
    }
}

static void log_clocks(void)
{
    if (!clocks_ready)
        init_clocks();
    ApmPerformanceMode mode = ApmPerformanceMode_Invalid;
    u32 config = 0;
    if (R_SUCCEEDED(apmGetPerformanceMode(&mode)))
        apmGetPerformanceConfiguration(mode, &config);
    u32 cpu = 0, gpu = 0, emc = 0;
    if (cpu_ok) clkrstGetClockRate(&cpu_s, &cpu);
    if (gpu_ok) clkrstGetClockRate(&gpu_s, &gpu);
    if (emc_ok) clkrstGetClockRate(&emc_s, &emc);
    io_debugf("NX_CLOCK apm_mode=%d config=0x%08x cpu=%.1fMHz gpu=%.1fMHz emc=%.1fMHz",
        (int)mode, config, cpu / 1e6, gpu / 1e6, emc / 1e6);
}

static void init_gl(void)
{
    init_done = true;
    gen_queries = (GenQueries)SDL_GL_GetProcAddress("glGenQueries");
    query_counter = (QueryCounter)SDL_GL_GetProcAddress("glQueryCounter");
    GetQueryiv get_queryiv = (GetQueryiv)SDL_GL_GetProcAddress("glGetQueryiv");
    get_query_objectiv = (GetQueryObjectiv)SDL_GL_GetProcAddress("glGetQueryObjectiv");
    get_query_objectui64v = (GetQueryObjectui64v)SDL_GL_GetProcAddress("glGetQueryObjectui64v");
    if (!gen_queries || !query_counter || !get_queryiv || !get_query_objectiv || !get_query_objectui64v) {
        io_debugf("NX_GPU disabled: ARB_timer_query entry points missing");
        return;
    }
    int bits = 0;
    get_queryiv(GL_TIMESTAMP, GL_QUERY_COUNTER_BITS, &bits);
    if (bits <= 0) {
        io_debugf("NX_GPU disabled: GL_TIMESTAMP has %d counter bits", bits);
        return;
    }
    gen_queries(RING, begin_q);
    gen_queries(RING, end_q);
    enabled = true;
    io_debugf("NX_GPU timestamp queries enabled (%d bits, ring %d); NX_GPU every report: "
        "frames span/gap/period avg ms, span max ms, lost", bits, RING);
}

static void read_ready(void)
{
    // Read finished frames in order; stop at the first one the GPU has not reached yet.
    while (next_read < frame) {
        unsigned slot = next_read % RING;
        if (issued[slot]) {
            int available = 0;
            get_query_objectiv(end_q[slot], GL_QUERY_RESULT_AVAILABLE, &available);
            if (!available) {
                if (frame - next_read < RING - 1)
                    return;
                ++lost;  // about to be reused; never wait for it
                have_last_end = false;
            } else {
                uint64_t b = 0, e = 0;
                get_query_objectui64v(begin_q[slot], GL_QUERY_RESULT, &b);
                get_query_objectui64v(end_q[slot], GL_QUERY_RESULT, &e);
                if (e >= b) {
                    uint64_t span = e - b;
                    ++n;
                    span_sum += span;
                    if (span > span_max) span_max = span;
                    if (have_last_end && b >= last_end && e > last_end) {
                        gap_sum += b - last_end;
                        period_sum += e - last_end;
                        ++period_n;
                    }
                }
                last_end = e;
                have_last_end = true;
            }
            issued[slot] = false;
        }
        ++next_read;
    }
}

void nx_gpu_frame_end(void)
{
    if (!init_done)
        init_gl();
    if (!enabled)
        return;
    unsigned slot = frame % RING;
    if (issued[slot]) {  // begin was written for this frame; close it
        query_counter(end_q[slot], GL_TIMESTAMP);
        ++frame;
    }
}

void nx_gpu_frame_begin(void)
{
    if (!enabled)
        return;
    read_ready();
    unsigned slot = frame % RING;
    if (frame - next_read >= RING) {  // ring full (results very late): skip this frame
        ++lost;
        return;
    }
    query_counter(begin_q[slot], GL_TIMESTAMP);
    issued[slot] = true;
}

Result __real_nwindowDequeueBuffer(NWindow *nw, s32 *out_slot, NvMultiFence *out_fence);
Result __real_nwindowQueueBuffer(NWindow *nw, s32 slot, const NvMultiFence *fence);

Result __wrap_nwindowDequeueBuffer(NWindow *nw, s32 *out_slot, NvMultiFence *out_fence)
{
    NvMultiFence fence;
    s32 slot = -1;
    Result rc = __real_nwindowDequeueBuffer(nw, &slot, &fence);
    if (R_FAILED(rc))
        return rc;
    if (out_slot)
        *out_slot = slot;
    if (out_fence) {
        *out_fence = fence;
    } else {
        uint64_t t0 = armTicksToNs(armGetSystemTick());
        if (R_FAILED(nvMultiFenceWait(&fence, 0))) {
            nvMultiFenceWait(&fence, -1);
            uint64_t w = armTicksToNs(armGetSystemTick()) - t0;
            ++sw_blocked;
            sw_wait_ns += w;
            if (w > sw_wait_max) sw_wait_max = w;
        }
    }
    ++sw_deq;
    if (slot >= 0 && slot < SWAP_SLOTS) ++sw_slot[slot];
    if (sw_last_queue) {
        sw_lat_ns += armTicksToNs(armGetSystemTick()) - sw_last_queue;
        ++sw_lat_n;
        sw_last_queue = 0;
    }
    return rc;
}

static int swap_override = -2;

Result __real_nwindowSetSwapInterval(NWindow *nw, u32 swap_interval);
Result __wrap_nwindowSetSwapInterval(NWindow *nw, u32 swap_interval)
{
    if (swap_override == -2) {
        const char *v = getenv("NX_SWAP_INTERVAL");
        swap_override = v && *v ? atoi(v) : -1;
        if (swap_override >= 0)
            io_debugf("NX_SWAP interval %u requested, using %d (NX_SWAP_INTERVAL)", swap_interval, swap_override);
    }
    return __real_nwindowSetSwapInterval(nw, swap_override >= 0 ? (u32)swap_override : swap_interval);
}

Result __wrap_nwindowQueueBuffer(NWindow *nw, s32 slot, const NvMultiFence *fence)
{
    static uint64_t paced;
    if (swap_override == 0) {
        uint64_t now = armTicksToNs(armGetSystemTick());
        const uint64_t period = 16666667;
        if (paced && now < paced + period)
            svcSleepThread(paced + period - now);
        now = armTicksToNs(armGetSystemTick());
        paced = (paced && now < paced + 2 * period) ? paced + period : now;
    }
    Result rc = __real_nwindowQueueBuffer(nw, slot, fence);
    sw_last_queue = armTicksToNs(armGetSystemTick());
    if (R_SUCCEEDED(rc) && nw->consumer_running_behind) ++sw_behind;
    return rc;
}

static void log_swap(bool final)
{
    if (!sw_deq)
        return;
    char slots[96];
    int len = 0;
    for (int i = 0; i < SWAP_SLOTS; ++i)
        if (sw_slot[i]) len += snprintf(slots + len, sizeof(slots) - len, "%s%d:%" PRIu64, len ? "," : "", i, sw_slot[i]);
    io_debugf("NX_SWAP%s dequeues=%" PRIu64 " slots=%s blocked=%" PRIu64 " wait avg/max=%.3f/%.3fms queue_to_dequeue=%.3fms behind=%" PRIu64,
        final ? " final" : "", sw_deq, slots, sw_blocked, sw_blocked ? sw_wait_ns / 1e6 / sw_blocked : 0.0,
        sw_wait_max / 1e6, sw_lat_n ? sw_lat_ns / 1e6 / sw_lat_n : 0.0, sw_behind);
    for (int i = 0; i < SWAP_SLOTS; ++i) sw_slot[i] = 0;
    sw_deq = sw_blocked = sw_wait_ns = sw_wait_max = sw_lat_ns = sw_lat_n = sw_behind = 0;
}

void nx_gpu_report(bool final)
{
    log_clocks();
    log_swap(final);
    if (!enabled)
        return;
    io_debugf("NX_GPU%s frames=%" PRIu64 " span/gap/period=%.3f/%.3f/%.3fms span_max=%.3fms lost=%" PRIu64,
        final ? " final" : "", n, n ? span_sum * TICK_SCALE / 1e6 / n : 0.0,
        period_n ? gap_sum * TICK_SCALE / 1e6 / period_n : 0.0,
        period_n ? period_sum * TICK_SCALE / 1e6 / period_n : 0.0, span_max * TICK_SCALE / 1e6, lost);
    n = span_sum = span_max = gap_sum = period_sum = period_n = lost = 0;
}
