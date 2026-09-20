// SDL3 compatibility shim for Terraria 1.4.5 startup diagnostics.
//
// Terraria.Program.LogFNANativeLibVersions directly imports SDL3 and calls:
//   SDL_GetVersion()  -> SDL3 returns packed int, SDL2 takes SDL_version*.
//   SDL_GetRevision() -> same no-argument string-returning ABI in SDL2.
//
// FNA itself is forced to SDL2 by FNA_PLATFORM_BACKEND=SDL2. This file only
// handles the two direct Terraria calls; it is not a general SDL3 port.

#include <SDL2/SDL.h>
#include "../shared_mono_nx/dl_shim_base.h"
#include "../shared_mono_nx/io_util.h"

extern void *getsym_SDL2(const char *name);

static int SDL3_Compat_GetVersion(void)
{
    SDL_version version;
    SDL_GetVersion(&version);
    return version.major * 1000 + version.minor * 100 + version.patch;
}

static const char *SDL3_Compat_GetRevision(void)
{
    return SDL_GetRevision();
}


// SDL3 and SDL2 use different SDL_MessageBoxData layouts. Never forward this
// call to SDL2; return false so Terraria's optional error dialog is skipped.
static int SDL3_Compat_ShowMessageBox(const void *messageboxdata, int *buttonid)
{
    io_debugf("SDL3_ShowMessageBox called (data=%p, buttonid=%p)",
        messageboxdata, (void *)buttonid);
    (void)messageboxdata;
    if (buttonid)
        *buttonid = -1;
    return 0;
}

void *getsym_SDL3(const char *name)
{
    if (name && strcmp(name, "SDL_GetVersion") == 0)
        return (void*)SDL3_Compat_GetVersion;
    if (name && strcmp(name, "SDL_GetRevision") == 0)
        return (void*)SDL3_Compat_GetRevision;
    if (name && strcmp(name, "SDL_ShowMessageBox") == 0)
        return (void*)SDL3_Compat_ShowMessageBox;

    // Shared symbols with compatible ABI can use the existing SDL2 resolver.
    return getsym_SDL2(name);
}
