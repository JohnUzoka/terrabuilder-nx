#include "core.h"
#include "nx_input.h"
#include "nx_runtime_config.h"
#if defined(MONO_NX_USE_AOT)
#include <mono/utils/mono-error.h>
// This lookup is exported by the pinned mono-nx SDK, but not its public headers.
extern void *mono_aot_get_method_from_token(MonoImage *image, uint32_t token,
    MonoError *error);
extern void *mono_threads_enter_gc_unsafe_region(void **stackpointer);
extern void mono_threads_exit_gc_unsafe_region(void *cookie, void **stackpointer);
#endif

#if defined(MONO_NX_USE_ROMFS)
#include <unistd.h>
#endif
#include <sys/stat.h>
#include <errno.h>
#include <stdio.h>
#include <string.h>
#if defined(MONO_NX_GC_STATS)
#include <malloc.h>
#include <mono/metadata/mono-gc.h>
// SGen's cumulative collection counters/times (100 ns units), gc-internal-agnostic.h.
typedef struct { int32_t minor_gc_count, major_gc_count;
                 int64_t minor_gc_time, major_gc_time, major_gc_time_concurrent; } NxGcStats;
extern NxGcStats mono_gc_stats;
#endif
// mono_jit_parse_options does not expose --interp=... in this embedding API.
// The interpreter option bitmask is exported by the bundled Mono runtime.
extern int mono_interp_opt;

static void ensure_sd_directory(const char *path)
{
    if (mkdir(path, 0777) != 0 && errno != EEXIST)
        io_debugf("mkdir failed for %s (errno=%d)", path, errno);
}

// Optional SD file /mono/gc_params.txt: one line, passed to SGen as MONO_GC_PARAMS
// (e.g. "nursery-size=16m" or "major=marksweep,nursery-size=32m"). Lets GC settings be
// compared by editing a text file instead of rebuilding. Absent file = SGen defaults.
#define GC_PARAMS_PATH "/mono/gc_params.txt"
static void apply_gc_params_file(void)
{
    FILE *f = fopen(GC_PARAMS_PATH, "r");
    if (!f)
        return;
    char line[256] = {0};
    if (fgets(line, sizeof line, f)) {
        line[strcspn(line, "\r\n")] = 0;
        const char *value = line + strspn(line, " \t");
        if (*value && *value != ';' && *value != '#') {
            setenv("MONO_GC_PARAMS", value, 1);
            io_debugf("NX_GC params=%s (from %s)", value, GC_PARAMS_PATH);
        }
    }
    fclose(f);
}

#if defined(MONO_NX_GC_STATS)
// Measurement build only (MONO_NX_GC_STATS=1): every 5 s log GC counts/time and memory
// use of both heap halves. Runs on its own native thread and only reads counters that
// need no Mono thread attachment (atomics / plain 64-bit loads, newlib mallinfo).
static Thread gc_stats_thread;
static volatile bool gc_stats_stop;
static void gc_stats_log(uint64_t start, bool final)
{
    struct mallinfo mi = mallinfo();
    double elapsed = (double)(armGetSystemTick() - start) / (double)armGetSystemTickFreq();
    io_debugf("NX_GC%s elapsed=%.3fs minor=%d major=%d minor_ms=%.3f major_ms=%.3f "
        "major_conc_ms=%.3f managed_alloc_mb=%.1f malloc_used_mb=%.1f malloc_arena_mb=%.1f",
        final ? " final" : "", elapsed,
        mono_gc_collection_count(0), mono_gc_collection_count(1),
        mono_gc_stats.minor_gc_time / 10000.0, mono_gc_stats.major_gc_time / 10000.0,
        mono_gc_stats.major_gc_time_concurrent / 10000.0,
        mono_gc_get_heap_size() / 1048576.0,
        mi.uordblks / 1048576.0, mi.arena / 1048576.0);
}
static void gc_stats_worker(void *arg)
{
    uint64_t start = (uint64_t)(uintptr_t)arg;
    while (!gc_stats_stop) {
        for (int i = 0; i < 50 && !gc_stats_stop; ++i)
            svcSleepThread(100000000LL);
        if (!gc_stats_stop)
            gc_stats_log(start, false);
    }
}
#endif

#if defined(MONO_NX_NET_TRACE)
// Diagnostic build only (MONO_NX_NET_TRACE=1, linked with -Wl,--wrap=connect): log every
// socket connect made through libSystem.Native with the raw sockaddr, result and errno,
// and a running call count (shows whether Terraria's retry loop runs before a failure).
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
extern int __real_connect(int fd, const struct sockaddr *addr, socklen_t len);
int __wrap_connect(int fd, const struct sockaddr *addr, socklen_t len)
{
    static int calls;
    int n = ++calls;
    uint64_t t0 = armGetSystemTick();
    int rc = __real_connect(fd, addr, len);
    int err = rc ? errno : 0;
    double ms = (double)(armGetSystemTick() - t0) * 1000.0 / (double)armGetSystemTickFreq();
    char bytes[3 * 16 + 1] = {0};
    const unsigned char *raw = (const unsigned char *)addr;
    for (socklen_t i = 0; addr && i < len && i < 16; i++)
        snprintf(bytes + 3 * i, 4, "%02x ", raw[i]);
    if (addr && len >= (socklen_t)sizeof(struct sockaddr_in) && addr->sa_family == AF_INET) {
        const struct sockaddr_in *in = (const struct sockaddr_in *)addr;
        char ip[INET_ADDRSTRLEN] = "?";
        inet_ntop(AF_INET, &in->sin_addr, ip, sizeof ip);
        io_debugf("NX_NET connect #%d fd=%d %s:%u len=%u rc=%d errno=%d (%s) %.1fms bytes=%s", n, fd, ip,
            (unsigned)ntohs(in->sin_port), (unsigned)len, rc, err, err ? strerror(err) : "ok", ms, bytes);
    } else {
        io_debugf("NX_NET connect #%d fd=%d family=%d len=%u rc=%d errno=%d (%s) %.1fms bytes=%s", n, fd,
            addr ? addr->sa_family : -1, (unsigned)len, rc, err, err ? strerror(err) : "ok", ms, bytes);
    }
    errno = err;
    return rc;
}
#endif
#ifndef MONO_NX_NIFM
#define MONO_NX_NIFM 1
#endif

#if defined(MONO_NX_NIFM)
// In Nintendo Switch Application mode (title takeover / installed title), Horizon OS
// network policy requires an active NIFM network request (IRequest) submitted by the
// application before BSD sockets can reach the network. Without a submitted request,
// connect returns ENETUNREACH (errno 114). Homebrew applications running under title
// takeover create and submit a NifmRequest at startup and hold it open.
// To preserve offline / airplane-mode startup and LAN-only play without internet access,
// the request is submitted asynchronously (nifmRequestSubmit instead of blocking on
// nifmRequestSubmitAndWait), and all NIFM steps are non-fatal.
#include <switch/services/nifm.h>

static bool s_nifm_initialized = false;
static NifmRequest s_nifm_request;
static bool s_nifm_request_active = false;

static void nx_net_nifm_init(void)
{
    Result rc = nifmInitialize(NifmServiceType_User);
    if (R_FAILED(rc)) {
        io_debugf("NX_NET nifm init failed (rc=0x%08x)", rc);
        return;
    }
    s_nifm_initialized = true;

    NifmInternetConnectionType conn_type = 0;
    u32 wifi_strength = 0;
    NifmInternetConnectionStatus conn_status = 0;
    Result status_rc = nifmGetInternetConnectionStatus(&conn_type, &wifi_strength, &conn_status);
    if (R_SUCCEEDED(status_rc)) {
        const char *type_str = (conn_type == NifmInternetConnectionType_WiFi) ? "WiFi" :
                               (conn_type == NifmInternetConnectionType_Ethernet) ? "Ethernet" : "Unknown";
        const char *status_str = (conn_status == NifmInternetConnectionStatus_Connected) ? "Connected" : "Connecting";
        io_debugf("NX_NET nifm status: %s type=%s wifi_strength=%u status_code=%d",
            status_str, type_str, (unsigned)wifi_strength, (int)conn_status);
    } else {
        io_debugf("NX_NET nifm status: unavailable (rc=0x%08x)", status_rc);
    }

    rc = nifmCreateRequest(&s_nifm_request, true);
    if (R_FAILED(rc)) {
        io_debugf("NX_NET nifm create request failed (rc=0x%08x)", rc);
        return;
    }
    s_nifm_request_active = true;

    rc = nifmRequestSubmit(&s_nifm_request);
    NifmRequestState state = NifmRequestState_Invalid;
    nifmGetRequestState(&s_nifm_request, &state);
    Result req_res = nifmGetResult(&s_nifm_request);
    io_debugf("NX_NET nifm request submitted (rc=0x%08x, state=%d, req_res=0x%08x)", rc, (int)state, req_res);
}

static void nx_net_nifm_exit(void)
{
    if (s_nifm_request_active) {
        nifmRequestClose(&s_nifm_request);
        s_nifm_request_active = false;
    }
    if (s_nifm_initialized) {
        nifmExit();
        s_nifm_initialized = false;
    }
}
#endif

#if defined(MONO_NX_FATAL_DIAG)
// Replaces the SDK's log hook so a fatal Mono error always reaches the log file
// (flushed) and then aborts through diagAbortWithResult, which makes Atmosphère
// write a crash report with the native stack. The SDK default otherwise loses
// the message in the stdio buffer (runtime_logging=false) or exits cleanly via
// the error applet with no stack (runtime_logging=true).
static void nx_fatal_diag_log(const char *log_domain, const char *log_level,
    const char *message, mono_bool fatal, void *user_data)
{
    (void)user_data;
    if (g_config.mono_runtime_logging || fatal)
        io_debugf("%s %s %s", log_domain ? log_domain : "(null)",
            log_level ? log_level : "", message ? message : "");
    if (fatal)
    {
        io_debugf("NX_FATAL_DIAG aborting for a native crash report");
        fflush(stdout);
        io_stdio_finish();
        diagAbortWithResult(MAKERESULT(Module_HomebrewLoader, 0x5d));
    }
}
#endif

int main(int argc, char *argv[])
{
    // Force FNA to use the SDL2 backend instead of SDL3.
    // Terraria 1.4.5+ ships with SDL3, but FNA supports both backends at
    // runtime via this env var. SDL2 is what devkitPro provides for Switch homebrew.
    setenv("FNA_PLATFORM_BACKEND", "SDL2", 1);
    // FNA3D driver selection — OpenGL works with switch-mesa. D3D11 is Windows-only.
    setenv("FNA3D_FORCE_DRIVER", "OpenGL", 1);
#if defined(MONO_NX_GL_COMPAT)
    // Opt-in comparison with the user's L4T GLCompatibility setup.
    // SDL's EGL backend honors these attributes; hardware support is checked
    // when it creates the context, not assumed from the requested profile.
    setenv("FNA3D_OPENGL_FORCE_ES3", "0", 1);
    setenv("FNA3D_OPENGL_FORCE_CORE_PROFILE", "0", 1);
    setenv("FNA3D_OPENGL_FORCE_COMPATIBILITY_PROFILE", "1", 1);
#endif
    // Switch SDL2 audio may report an already-open device during FAudio
    // startup. Use the dummy backend for the initial graphics/title-screen
    // validation; real Switch audio can be enabled after the port is stable.
    setenv("SDL_AUDIODRIVER", "dummy", 1);
    // ReLogic's Linux path service uses XDG_DATA_HOME/HOME. These paths
    // must remain POSIX-rooted for managed Path APIs; the cwd is SD when
    // they reach libnx, so /switch/... resolves to the writable SD device.
    setenv("XDG_DATA_HOME", "/switch/terraria", 1);
    setenv("XDG_CONFIG_HOME", "/switch/terraria/config", 1);
    setenv("HOME", "/switch", 1);
    // FNA normally derives its title root from the process base directory.
    // Keep content rooted in the embedded RomFS after switching cwd to SD.
    setenv("FNA_NX_TITLE_LOCATION", "romfs:/", 1);
    setenv("FNA_NX_CONTENT_LOCATION", "romfs:/Content", 1);

    if (!application_initialize(CONFIG_INI_PATH))
        return 1;
#if defined(MONO_NX_NIFM)
    nx_net_nifm_init();
#endif
    io_debugf("NX_CONFIG file=%s logging=%d runtime_logging=%d", CONFIG_INI_PATH,
        g_config.mononx_logging, g_config.mono_runtime_logging);
    if (g_config.mono_runtime_logging)
        io_debugf("NX_CONFIG verbose Mono tracing is enabled; timings include diagnostic overhead");
#if defined(MONO_NX_GL_COMPAT)
    io_debugf("NX_GRAPHICS requesting desktop OpenGL compatibility profile");
#endif
#if defined(MONO_NX_EMBEDDED_BCL)
    // This NRO carries its own CoreLib and Release framework. application_initialize()
    // has already read the SD config and ICU with the SD default device, so
    // mounting RomFS now (without chdir) keeps those paths intact.
    // Search order: CoreLib from romfs:/mono/lib_net9.0, then the RomFS root, which
    // holds the framework plus the game-specific overrides (patched mscorlib facade,
    // legacy System.Drawing). Both entries are device-absolute: after the entrypoint
    // the cwd returns to sdmc:/, where a bare "/" would be the SD root, so later
    // framework loads (System.Runtime, ...) would miss and fall into Terraria's
    // managed AssemblyResolve handler mid-cctor (build 58c crash). A separate
    // framework directory ahead of the root would shadow the overrides (58a crash).
    Result bcl_romfs_result = romfsInit();
    if (R_FAILED(bcl_romfs_result))
    {
        fatal_error("Failed to mount embedded RomFS for the BCL\n");
        return 1;
    }
    mono_set_assemblies_path("romfs:/mono/lib_net9.0;romfs:/");
    io_debugf("NX_RUNTIME embedded BCL: romfs:/mono/lib_net9.0;romfs:/");
#endif

    MonoDomain *domain = NULL;

#if defined(MONO_NX_USE_AOT)
    // Each symbol contains a pointer to MonoAotFileInfo, not the struct itself.
    #define REGISTER_AOT_MODULE(symbol) do { \
        extern void *symbol; \
        mono_aot_register_module(symbol); \
    } while (0)
    #include "mono_aot_modules.h"
    #undef REGISTER_AOT_MODULE
    mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP);
    io_debugf("NX_RUNTIME AOT plus interpreter fallback; full JIT disabled");
#else
    mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);
    io_debugf("NX_RUNTIME interpreter-only; full JIT disabled");
#endif
    // Keep ordinary interpreter optimizations, but omit the two passes
    // implicated by the Switch crash: super-instructions and tiering.
    enum { INTERP_INLINE = 1, INTERP_CPROP = 2, INTERP_BBLOCKS = 8,
           INTERP_SIMD = 32, INTERP_SSA = 128, INTERP_PRECISE_GC = 256 };
    mono_interp_opt = INTERP_INLINE | INTERP_CPROP | INTERP_BBLOCKS |
                      INTERP_SIMD | INTERP_SSA | INTERP_PRECISE_GC;

    nx_runtime_register_capabilities();
    io_debugf("NX_RUNTIME EventSource disabled: native EventPipe is unavailable");

    application_configure_mono();
#if defined(MONO_NX_FATAL_DIAG)
    mono_trace_set_log_handler(nx_fatal_diag_log, NULL);
    io_debugf("NX_RUNTIME fatal diagnostics: Mono fatal errors abort with a crash report");
#endif

    apply_gc_params_file();
    domain = mono_jit_init("embedded_mono");
    if (!domain)
    {
        fatal_error("Failed to initialize mono domain\n");
        return 1;
    }
#if defined(MONO_NX_GC_STATS)
    uint64_t gc_stats_start = armGetSystemTick();
    // Lowest priority on application cores (0x3B enables preemptive multithreading;
    // 0x3F is invalid on cores 0..2 and outside hbloader's allowed 0x1C..0x3B NPDM mask).
    Result gc_stats_rc = threadCreate(&gc_stats_thread, gc_stats_worker,
        (void *)(uintptr_t)gc_stats_start, NULL, 0x4000, 0x3B, -2);
    if (R_SUCCEEDED(gc_stats_rc)) {
        gc_stats_rc = threadStart(&gc_stats_thread);
        if (R_FAILED(gc_stats_rc))
            threadClose(&gc_stats_thread);
    }
    bool gc_stats_running = R_SUCCEEDED(gc_stats_rc);
    if (gc_stats_running)
        io_debugf("NX_GC stats every 5s: started (counts cumulative; times in ms; managed_alloc = SGen current GC OS allocation, malloc = newlib half)");
    else
        io_debugf("NX_GC stats every 5s: thread failed (rc=0x%x)", gc_stats_rc);
#endif
    // Mono initializes the internal-call table during mono_jit_init.
    nx_input_register();
#if defined(MONO_NX_USE_ROMFS)
    // Mount RomFS only after external config, ICU, and the Mono BCL have been
    // initialized from SD:/mono. Changing the default device earlier would
    // make /mono/config.ini resolve against RomFS and fail to load.
#if !defined(MONO_NX_EMBEDDED_BCL)
    Result romfs_result = romfsInit();
    if (R_FAILED(romfs_result))
    {
        fatal_error("Failed to mount embedded RomFS\n");
        return 1;
    }
#endif
    chdir("romfs:/");
#endif

    char *launch_dll = io_strdup(argc > 1 ? argv[1] : g_config.default_assembly);
    if (!launch_dll)
    {
        fatal_error("No .dll was specified");
        return 1;
    }

    io_debugf("Loading assembly %s", launch_dll);
#if !defined(MONO_NX_USE_ROMFS)
    application_chdir_to_assembly(launch_dll);
#endif

    MonoAssembly *assembly = mono_domain_assembly_open(domain, launch_dll);
    if (!assembly)
    {
        fatal_error("Failed to load assembly");
        return 1;
    }
#if defined(MONO_NX_USE_AOT)
    MonoImage *game_image = mono_assembly_get_image(assembly);
    uint32_t entry_token = mono_image_get_entry_point(game_image);
    if (!entry_token)
    {
        fatal_error("AOT game assembly has no entrypoint");
        return 1;
    }
    MonoError aot_error;
    // Embedding APIs return in GC-safe mode; the internal AOT lookup requires
    // a balanced GC-unsafe region while resolving managed metadata.
    void *stack_marker = NULL;
    void *gc_cookie = mono_threads_enter_gc_unsafe_region(&stack_marker);
    void *native_entry = mono_aot_get_method_from_token(game_image, entry_token, &aot_error);
    mono_threads_exit_gc_unsafe_region(gc_cookie, &stack_marker);
    if (!native_entry || !mono_error_ok(&aot_error))
    {
        io_debugf("NX_AOT entrypoint lookup failed: %s", mono_error_ok(&aot_error) ?
            "no usable native method" : mono_error_get_message(&aot_error));
        mono_error_cleanup(&aot_error);
        fatal_error("AOT entrypoint unavailable; inspect /mono/log.txt");
        return 1;
    }
    mono_error_cleanup(&aot_error);
    io_debugf("NX_AOT Terraria entrypoint resolved to native code");
#endif
#if defined(MONO_NX_USE_ROMFS)
    // Managed save/config paths need the SD default device. FNA's title root
    // is pinned to RomFS above so Content/ remains embedded and read-only.
    if (chdir("sdmc:/") != 0)
        io_debugf("Failed to switch writable data cwd to sdmc:/");
    ensure_sd_directory("/switch");
    ensure_sd_directory("/switch/terraria");
    ensure_sd_directory("/switch/terraria/Terraria");
    ensure_sd_directory("/switch/terraria/Terraria/Players");
    ensure_sd_directory("/switch/terraria/Terraria/Worlds");
    ensure_sd_directory("/switch/terraria/Terraria/ResourcePacks");
    ensure_sd_directory("/switch/terraria/config");
#endif

    char *monoargs[] = {launch_dll};

    mono_jit_exec(domain, assembly, 1, monoargs);
    nx_input_shutdown();
#if defined(MONO_NX_GC_STATS)
    if (gc_stats_running) {
        gc_stats_stop = true;
        threadWaitForExit(&gc_stats_thread);
        threadClose(&gc_stats_thread);
    }
    gc_stats_log(gc_stats_start, true);
#endif

    mono_jit_cleanup(domain);

    free(launch_dll);

#if defined(MONO_NX_USE_ROMFS)
    romfsExit();
#endif
#if defined(MONO_NX_NIFM)
    nx_net_nifm_exit();
#endif
    application_terminate();

    return 0;
}
