// dl_shim_fna.h - Registration additions for dl_shim.c
//
// Add these lines to the existing dl_shim.c in mono-nx's native/shared/ directory.
// The REGISTER_LIBRARY and CHECK_LIB_NAME macros are already defined there.
//
// Place these new library registrations after the existing optional libraries:

// --- BEGIN FNA ADDITIONS ---

// FNA3D - 3D graphics backend
REGISTER_LIBRARY(FNA3D, "FNA3D", 0x110)

// FAudio - audio engine (covers FAudio, FAudioFX, FACT, FAPOFX)
REGISTER_LIBRARY(FNAudio, "FAudio", 0x111)

// --- END FNA ADDITIONS ---

// Then add to dlshim_loadLibrary's switch body:
//   CHECK_LIB_NAME(name, FNA3D);
//   CHECK_LIB_NAME(name, FNAudio);

// And add to dlshim_getSymbol's switch body:
//   CHECK_LIB_SYMBOL(FNA3D)
//   CHECK_LIB_SYMBOL(FNAudio)
