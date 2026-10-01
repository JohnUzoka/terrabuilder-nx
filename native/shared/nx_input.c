#include "nx_input.h"
#include "nx_input_latch.h"
#include "io_util.h"

#include <switch.h>
#include <SDL2/SDL.h>
#include <FNA3D/FNA3D.h>
#include <mono/metadata/loader.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/class.h>
#include <mono/metadata/image.h>
#include <mono/metadata/object.h>
#include <inttypes.h>
#include <string.h>

#define NX_INPUT_CONTROLLERS 4
#define NX_INPUT_RAW_BUTTONS 28
#define NX_INPUT_POLL_NS 4000000LL
#define NX_INPUT_REPORT_SECONDS 5

/* These are the raw button orders in devkitPro SDL's switch-sdl-2.28 backend,
 * not SDL_GameControllerButton order. In particular Plus/Minus are b10/b11.
 * Zero entries replace the backend's unused BIT(31) placeholders.
 */
static const uint64_t raw_default[NX_INPUT_RAW_BUTTONS] = {
    HidNpadButton_A, HidNpadButton_B, HidNpadButton_X, HidNpadButton_Y,
    HidNpadButton_StickL, HidNpadButton_StickR,
    HidNpadButton_L, HidNpadButton_R, HidNpadButton_ZL, HidNpadButton_ZR,
    HidNpadButton_Plus, HidNpadButton_Minus,
    HidNpadButton_Left, HidNpadButton_Up, HidNpadButton_Right, HidNpadButton_Down,
    HidNpadButton_StickLLeft, HidNpadButton_StickLUp,
    HidNpadButton_StickLRight, HidNpadButton_StickLDown,
    HidNpadButton_StickRLeft, HidNpadButton_StickRUp,
    HidNpadButton_StickRRight, HidNpadButton_StickRDown,
    HidNpadButton_LeftSL, HidNpadButton_LeftSR,
    HidNpadButton_RightSL, HidNpadButton_RightSR
};

static const uint64_t raw_left_joy[NX_INPUT_RAW_BUTTONS] = {
    HidNpadButton_Down, HidNpadButton_Left, HidNpadButton_Right, HidNpadButton_Up,
    0, 0, 0, 0, HidNpadButton_LeftSL, HidNpadButton_LeftSR,
    HidNpadButton_StickL, HidNpadButton_Minus,
    HidNpadButton_StickLUp, HidNpadButton_StickLRight,
    HidNpadButton_StickLDown, HidNpadButton_StickLLeft,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
};

static const uint64_t raw_right_joy[NX_INPUT_RAW_BUTTONS] = {
    HidNpadButton_X, HidNpadButton_A, HidNpadButton_Y, HidNpadButton_B,
    0, 0, 0, 0, HidNpadButton_RightSL, HidNpadButton_RightSR,
    HidNpadButton_StickR, HidNpadButton_Plus,
    HidNpadButton_StickRDown, HidNpadButton_StickRLeft,
    HidNpadButton_StickRUp, HidNpadButton_StickRRight,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
};

typedef struct NxPadSample {
    uint32_t buttons;
    uint32_t device_type;
    uint32_t device_style;
    uint32_t active_style;
    uint32_t sources;
    uint8_t plus_minus; /* bit 0 = Plus held, bit 1 = Minus held, any mapping */
    bool connected;
    bool supported;
} NxPadSample;

typedef struct NxPadSlot {
    PadState pad; /* Only the worker updates this independent PadState. */
    NxPadSample sample;
    NxButtonLatch latch;
    bool sampled;
    /* Published only by BeginUpdate, never by the worker. */
    bool snapshot_ready;
    bool snapshot_supported;
    /* Set when Plus+Minus are both held, cleared once both are released. */
    bool fps_combo;
} NxPadSlot;

/* Frame timing (NX_PHASE log lines, SDL_PollEvent/FNA3D_SwapBuffers wrappers) is a
 * measurement build option: make MONO_NX_PHASE_TIMING=1. Default builds keep the
 * managed hook entry points (patched Terraria calls them every frame) and the input
 * latch, but do no timing, reporting or call wrapping.
 */
#if defined(MONO_NX_PHASE_TIMING)
typedef struct NxPhase {
    uint64_t begin;
    uint64_t first;
    uint64_t count;
    uint64_t total_count;
    uint64_t sum;
    uint64_t max;
    bool active;
} NxPhase;
#endif

static NxPadSlot slots[NX_INPUT_CONTROLLERS];
static Mutex input_lock;
static Thread input_thread;
static bool registered;
static bool worker_attempted;
static bool worker_running;
static bool stopping;
static bool fps_combo_down;
static bool fps_shown;
#if defined(MONO_NX_PHASE_TIMING)
static uint64_t start_tick;
static uint64_t report_tick;
static uint64_t tick_frequency;
static uint64_t updates_in_tick;
static uint64_t max_updates_in_tick;
static NxPhase tick_phase;
static NxPhase update_phase;
static NxPhase draw_phase;
static NxPhase poll_phase;
static NxPhase swap_phase;
/* Derived from the public declarations, including their calling convention. */
extern __typeof__(SDL_PollEvent) __real_SDL_PollEvent;
extern __typeof__(FNA3D_SwapBuffers) __real_FNA3D_SwapBuffers;
#endif

static NxPadSample read_pad(unsigned index)
{
    NxPadSample sample = {0};
    PadState *pad = &slots[index].pad;
    padUpdate(pad);
    sample.connected = padIsConnected(pad);
    sample.device_type = hidGetNpadDeviceType((HidNpadIdType)index);
    sample.device_style = hidGetNpadStyleSet((HidNpadIdType)index);
    sample.active_style = padGetStyleSet(pad);
    sample.sources = pad->active_id_mask | (padIsHandheld(pad) ? 0x100u : 0u);

    const uint32_t supported_styles = HidNpadStyleTag_NpadFullKey |
        HidNpadStyleTag_NpadHandheld | HidNpadStyleTag_NpadJoyDual |
        HidNpadStyleTag_NpadJoyLeft | HidNpadStyleTag_NpadJoyRight;
    sample.supported = ((sample.device_style | sample.active_style) &
        ~supported_styles) == 0;
    if (!sample.connected || !sample.supported)
        return sample;

    /* Match SDL's selection even for slot zero's default/handheld aggregate. */
    const uint64_t *mapping = raw_default;
    if (!(sample.device_style & HidNpadStyleTag_NpadJoyDual)) {
        if (sample.device_type & HidDeviceTypeBits_JoyLeft)
            mapping = raw_left_joy;
        else if (sample.device_type & HidDeviceTypeBits_JoyRight)
            mapping = raw_right_joy;
    }
    uint64_t held = padGetButtons(pad);
    sample.plus_minus = ((held & HidNpadButton_Plus) ? 1u : 0u) |
        ((held & HidNpadButton_Minus) ? 2u : 0u);
    for (unsigned button = 0; button < NX_INPUT_RAW_BUTTONS; ++button) {
        if (held & mapping[button])
            sample.buttons |= UINT32_C(1) << button;
    }
    return sample;
}

static void publish_sample(NxPadSlot *slot, NxPadSample sample)
{
    const NxPadSample *old = &slot->sample;
    if (!slot->sampled || sample.connected != old->connected ||
        sample.supported != old->supported || sample.sources != old->sources ||
        sample.device_type != old->device_type ||
        sample.device_style != old->device_style ||
        sample.active_style != old->active_style) {
        nx_button_latch_reset(&slot->latch);
    }
    slot->sample = sample;
    slot->sampled = true;
    nx_button_latch_sample(&slot->latch, sample.buttons);
}

static void input_worker(void *unused)
{
    (void)unused;
    for (;;) {
        mutexLock(&input_lock);
        bool stop = stopping;
        mutexUnlock(&input_lock);
        if (stop)
            break;

        NxPadSample samples[NX_INPUT_CONTROLLERS];
        for (unsigned i = 0; i < NX_INPUT_CONTROLLERS; ++i)
            samples[i] = read_pad(i);

        mutexLock(&input_lock);
        for (unsigned i = 0; i < NX_INPUT_CONTROLLERS; ++i)
            publish_sample(&slots[i], samples[i]);
        mutexUnlock(&input_lock);

        /* Never spin or call SDL/Mono/logging here, including on disconnect. */
        svcSleepThread(NX_INPUT_POLL_NS);
    }
}

/* All callers are managed game-thread hooks, never the sampling worker. */
static void ensure_worker(void)
{
    if (worker_attempted || stopping || !SDL_WasInit(SDL_INIT_JOYSTICK))
        return;
    worker_attempted = true;
    padInitializeDefault(&slots[0].pad);
    for (unsigned i = 1; i < NX_INPUT_CONTROLLERS; ++i)
        padInitialize(&slots[i].pad, (HidNpadIdType)(HidNpadIdType_No1 + i));

    /* One priority above the usual 0x2C main thread lets sampling preempt a
     * CPU-bound managed update on the default core, then sleep for four ms.
     * Same-priority polling on that core would not reliably capture slow ticks.
     */
    Result rc = threadCreate(&input_thread, input_worker, NULL, NULL,
        0x4000, 0x2B, -2);
    if (R_FAILED(rc)) {
        io_debugf("NX_INPUT threadCreate failed: 0x%08x; using SDL buttons", (unsigned)rc);
        return;
    }
    rc = threadStart(&input_thread);
    if (R_FAILED(rc)) {
        io_debugf("NX_INPUT threadStart failed: 0x%08x; using SDL buttons", (unsigned)rc);
        Result close_rc = threadClose(&input_thread);
        if (R_FAILED(close_rc))
            io_debugf("NX_INPUT threadClose after start failure: 0x%08x", (unsigned)close_rc);
        return;
    }
    worker_running = true;
    io_debugf("NX_INPUT sampler started: 4 pads, 4ms, priority=0x2B; raw buttons latched per update");
}

#if defined(MONO_NX_PHASE_TIMING)
static void begin_phase(NxPhase *phase, uint64_t now)
{
    if (phase->first == UINT64_MAX)
        phase->first = now - start_tick;
    phase->begin = now;
    phase->active = true;
}

static void record_phase(NxPhase *phase, uint64_t begin, uint64_t now)
{
    uint64_t first = begin - start_tick;
    if (first < phase->first)
        phase->first = first;
    uint64_t duration = now - begin;
    ++phase->count;
    ++phase->total_count;
    phase->sum += duration;
    if (duration > phase->max)
        phase->max = duration;
}

static void end_phase(NxPhase *phase, uint64_t now)
{
    if (!phase->active)
        return;
    phase->active = false;
    record_phase(phase, phase->begin, now);
}

static double milliseconds(uint64_t ticks)
{
    return (double)ticks * 1000.0 / (double)tick_frequency;
}

static double first_seconds(const NxPhase *phase)
{
    return phase->first == UINT64_MAX ? -1.0 :
        (double)phase->first / (double)tick_frequency;
}

static void reset_interval(NxPhase *phase)
{
    phase->count = 0;
    phase->sum = 0;
    phase->max = 0;
}

static void report_phases(uint64_t now, bool final)
{
    if (!final && now - report_tick < tick_frequency * NX_INPUT_REPORT_SECONDS)
        return;
    io_debugf("NX_PHASE%s elapsed=%.3fs window=%.3fs "
        "tick=%" PRIu64 "/%" PRIu64 " sum/max=%.3f/%.3fms "
        "update=%" PRIu64 "/%" PRIu64 " sum/max=%.3f/%.3fms "
        "draw=%" PRIu64 "/%" PRIu64 " sum/max=%.3f/%.3fms "
        "max_updates_tick=%" PRIu64 " first_tick/update/draw=%.3f/%.3f/%.3fs "
        "poll=%" PRIu64 "/%" PRIu64 " sum/max=%.3f/%.3fms "
        "swap=%" PRIu64 "/%" PRIu64 " sum/max=%.3f/%.3fms",
        final ? " final" : "", milliseconds(now - start_tick) / 1000.0,
        milliseconds(now - report_tick) / 1000.0,
        tick_phase.count, tick_phase.total_count,
        milliseconds(tick_phase.sum), milliseconds(tick_phase.max),
        update_phase.count, update_phase.total_count,
        milliseconds(update_phase.sum), milliseconds(update_phase.max),
        draw_phase.count, draw_phase.total_count,
        milliseconds(draw_phase.sum), milliseconds(draw_phase.max),
        max_updates_in_tick, first_seconds(&tick_phase),
        first_seconds(&update_phase), first_seconds(&draw_phase),
        poll_phase.count, poll_phase.total_count,
        milliseconds(poll_phase.sum), milliseconds(poll_phase.max),
        swap_phase.count, swap_phase.total_count,
        milliseconds(swap_phase.sum), milliseconds(swap_phase.max));
    reset_interval(&tick_phase);
    reset_interval(&update_phase);
    reset_interval(&draw_phase);
    reset_interval(&poll_phase);
    reset_interval(&swap_phase);
    max_updates_in_tick = 0;
    report_tick = now;
}

static void begin_tick(void)
{
    begin_phase(&tick_phase, armGetSystemTick());
    updates_in_tick = 0;
    ensure_worker();
}

static void end_tick(void)
{
    uint64_t now = armGetSystemTick();
    if (tick_phase.active && updates_in_tick > max_updates_in_tick)
        max_updates_in_tick = updates_in_tick;
    end_phase(&tick_phase, now);
    report_phases(now, false);
}
#else
static void begin_tick(void) { ensure_worker(); }
static void end_tick(void) {}
#endif

/* Plus+Minus toggles Terraria's frame-rate display (the game's F10) by setting
 * Terraria.Main.showFrameRate directly; the embedding calls switch to GC-unsafe
 * mode themselves. Game thread only, from the BeginUpdate hook.
 */
static void toggle_fps_display(void)
{
    static MonoVTable *vtable;
    static MonoClassField *field;
    static bool failed;
    if (failed)
        return;
    if (!field) {
        MonoImage *image = mono_image_loaded("Terraria");
        MonoClass *klass = image ? mono_class_from_name(image, "Terraria", "Main") : NULL;
        field = klass ? mono_class_get_field_from_name(klass, "showFrameRate") : NULL;
        vtable = field ? mono_class_vtable(mono_get_root_domain(), klass) : NULL;
        if (!vtable) {
            failed = true;
            field = NULL;
            io_debugf("NX_INPUT FPS toggle unavailable: Terraria.Main.showFrameRate not found");
            return;
        }
    }
    fps_shown = !fps_shown;
    MonoBoolean value = fps_shown;
    mono_field_static_set_value(vtable, field, &value);
    io_debugf("NX_INPUT Plus+Minus: FPS display %s", fps_shown ? "on" : "off");
}

static void begin_update(void)
{
    ensure_worker();
    if (worker_running) {
        bool combo = false;
        mutexLock(&input_lock);
        for (unsigned i = 0; i < NX_INPUT_CONTROLLERS; ++i) {
            NxPadSlot *slot = &slots[i];
            nx_button_latch_advance(&slot->latch);
            slot->snapshot_ready = slot->sampled;
            slot->snapshot_supported = slot->sample.supported;
            uint8_t plus_minus = slot->sample.connected ? slot->sample.plus_minus : 0;
            if (plus_minus == 3u && slot->sample.supported)
                slot->fps_combo = true;
            else if (plus_minus == 0)
                slot->fps_combo = false;
            if (slot->fps_combo) {
                /* Only full controllers can hold both, so the raw order is
                 * raw_default (Plus b10, Minus b11). Hide them from the game.
                 */
                slot->latch.presented &= ~((UINT32_C(1) << 10) | (UINT32_C(1) << 11));
                combo = true;
            }
        }
        mutexUnlock(&input_lock);
        if (combo && !fps_combo_down)
            toggle_fps_display();
        fps_combo_down = combo;
    }
#if defined(MONO_NX_PHASE_TIMING)
    if (tick_phase.active)
        ++updates_in_tick;
    begin_phase(&update_phase, armGetSystemTick());
#endif
}

#if defined(MONO_NX_PHASE_TIMING)
static void end_update(void)
{
    end_phase(&update_phase, armGetSystemTick());
}

static void begin_draw(void)
{
    begin_phase(&draw_phase, armGetSystemTick());
}

static void end_draw(void)
{
    end_phase(&draw_phase, armGetSystemTick());
}
#else
static void end_update(void) {}
static void begin_draw(void) {}
static void end_draw(void) {}
#endif

#if defined(MONO_NX_PHASE_TIMING)

/* These APIs run on the game/video thread, never the input sampler. The
 * linker also wraps function addresses returned by the static dl-shims.
 * Record every completed call while registered, including outside Tick.
 * Local start ticks preserve inclusive accounting if a callback reenters.
 * Poll is native polling/pumping only, not managed event dispatch; swap is
 * the complete presentation call, including CPU work and any waiting.
 */
int SDLCALL __wrap_SDL_PollEvent(SDL_Event *event)
{
    if (!registered || stopping)
        return __real_SDL_PollEvent(event);
    uint64_t begin = armGetSystemTick();
    int result = __real_SDL_PollEvent(event);
    record_phase(&poll_phase, begin, armGetSystemTick());
    return result;
}

void __wrap_FNA3D_SwapBuffers(FNA3D_Device *device,
    FNA3D_Rect *sourceRectangle, FNA3D_Rect *destinationRectangle,
    void *overrideWindowHandle)
{
    if (!registered || stopping) {
        __real_FNA3D_SwapBuffers(device, sourceRectangle, destinationRectangle,
            overrideWindowHandle);
        return;
    }
    uint64_t begin = armGetSystemTick();
    __real_FNA3D_SwapBuffers(device, sourceRectangle, destinationRectangle,
        overrideWindowHandle);
    record_phase(&swap_phase, begin, armGetSystemTick());
}
#endif

/* Matches managed static byte GetButton(IntPtr controller, int button). */
static uint8_t get_button(SDL_GameController *controller, int button)
{
    if (worker_running && controller && button >= 0 &&
        button < SDL_CONTROLLER_BUTTON_MAX) {
        SDL_Joystick *joystick = SDL_GameControllerGetJoystick(controller);
        SDL_JoystickID instance = joystick ? SDL_JoystickInstanceID(joystick) : -1;
        if (instance >= 0 && instance < NX_INPUT_CONTROLLERS) {
            const char *name = SDL_JoystickName(joystick);
            NxPadSlot *slot = &slots[instance];
            if (name && strcmp(name, "Switch Controller") == 0 &&
                slot->snapshot_ready && slot->snapshot_supported) {
                SDL_GameControllerButtonBind bind = SDL_GameControllerGetBindForButton(
                    controller, (SDL_GameControllerButton)button);
                if (bind.bindType == SDL_CONTROLLER_BINDTYPE_BUTTON &&
                    bind.value.button >= 0 && bind.value.button < NX_INPUT_RAW_BUTTONS) {
                    return (uint8_t)((slot->latch.presented >> bind.value.button) & 1u);
                }
            }
        }
    }
    /* Unsupported devices, axes/hats, or initialization failures retain SDL's
     * real behavior. Do not alter any of FNA's other joystick/state APIs.
     */
    return SDL_GameControllerGetButton(controller, (SDL_GameControllerButton)button);
}

void nx_input_register(void)
{
    if (registered)
        return;
    registered = true;
    mutexInit(&input_lock);
#if defined(MONO_NX_PHASE_TIMING)
    start_tick = report_tick = armGetSystemTick();
    tick_frequency = armGetSystemTickFreq();
    tick_phase.first = update_phase.first = draw_phase.first = UINT64_MAX;
    poll_phase.first = swap_phase.first = UINT64_MAX;
#endif
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::BeginTick", (const void *)begin_tick);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::EndTick", (const void *)end_tick);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::BeginUpdate", (const void *)begin_update);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::EndUpdate", (const void *)end_update);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::BeginDraw", (const void *)begin_draw);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::EndDraw", (const void *)end_draw);
    mono_add_internal_call("Terraria.NxInputDiag.InputDiagnostics::GetButton", (const void *)get_button);
#if defined(MONO_NX_PHASE_TIMING)
    io_debugf("NX_INPUT internal calls registered; NX_PHASE every 5s (count=window/total; first=-1 means not reached; poll/swap=native inclusive wall time, may overlap Tick)");
#else
    io_debugf("NX_INPUT internal calls registered; frame timing not built (MONO_NX_PHASE_TIMING=0)");
#endif
}

void nx_input_shutdown(void)
{
    if (!registered)
        return;
    mutexLock(&input_lock);
    stopping = true;
    mutexUnlock(&input_lock);
    if (worker_running) {
        Result rc = threadWaitForExit(&input_thread);
        if (R_FAILED(rc)) {
            io_debugf("NX_INPUT threadWaitForExit failed: 0x%08x", (unsigned)rc);
            return; /* Never free a thread whose exit was not verified. */
        }
        worker_running = false;
        rc = threadClose(&input_thread);
        if (R_FAILED(rc))
            io_debugf("NX_INPUT threadClose failed: 0x%08x", (unsigned)rc);
    }
#if defined(MONO_NX_PHASE_TIMING)
    report_phases(armGetSystemTick(), true);
#endif
}
