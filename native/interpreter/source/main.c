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
// mono_jit_parse_options does not expose --interp=... in this embedding API.
// The interpreter option bitmask is exported by the bundled Mono runtime.
extern int mono_interp_opt;

static void ensure_sd_directory(const char *path)
{
    if (mkdir(path, 0777) != 0 && errno != EEXIST)
        io_debugf("mkdir failed for %s (errno=%d)", path, errno);
}

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

    domain = mono_jit_init("embedded_mono");
    if (!domain)
    {
        fatal_error("Failed to initialize mono domain\n");
        return 1;
    }
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

    mono_jit_cleanup(domain);

    free(launch_dll);

#if defined(MONO_NX_USE_ROMFS)
    romfsExit();
#endif
    application_terminate();

    return 0;
}
