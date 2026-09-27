#---------------------------------------------------------------------------------
.SUFFIXES:
#---------------------------------------------------------------------------------

# Custom mono-nx interpreter with FNA3D + FAudio statically linked.
# Based on mono-nx's native/interpreter/Makefile.
#
# This Makefile adds:
#   - FNA3D and FAudio static libraries
#   - dl_shim entries for FNA3D and FAudio
#   - OpenGL (switch-mesa) for FNA3D's GL backend
#   - OpenAL for FAudio output (optional, can be disabled)

ifeq ($(strip $(DEVKITPRO)),)
$(error "Please set DEVKITPRO in your environment. export DEVKITPRO=<path to>/devkitpro")
endif

ifeq ($(strip $(MONO_NX_ROOT)),)
$(error "Please set MONO_NX_ROOT in your environment.")
endif

ifeq ($(strip $(ICU_NX_INSTALL_DIR)),)
$(error "Please set ICU_NX_INSTALL_DIR in your environment.")
endif

# Path to this project's native install dir (FNA3D, FAudio built here)
FNA_NX_INSTALL_DIR ?= $(CURDIR)/../install

TOPDIR ?= $(CURDIR)
include $(DEVKITPRO)/libnx/switch_rules

#---------------------------------------------------------------------------------
TARGET		:=	mono_nx_fna
BUILD		:=	build
SOURCES	:=	source \
			../shared_mono_nx \
			../shared_mono_nx/third_party/ini \
			../shared

DATA		:=	data
# NOTE: The devkitPro template prepends $(CURDIR)/ to each INCLUDES entry,
# so absolute paths break. We add mono-nx paths via CFLAGS instead.
INCLUDES	:=	include \
			../shared

#---------------------------------------------------------------------------------
# Feature toggles
#---------------------------------------------------------------------------------
MONO_NX_USE_SDL2 			:=  	1
MONO_NX_USE_OPENGL	 		:=  	1
MONO_NX_USE_FNA3D			:=  	1
MONO_NX_USE_FNAUDIO			:=  	1
# Disable cimgui — not needed for FNA
MONO_NX_USE_CIMGUI			:=  	0
# OpenAL for audio output (needed for FAudio)
MONO_NX_USE_OPENAL	 		:=  	1
# Stub libraries for Steam/Windows APIs (Terraria compatibility)
MONO_NX_USE_STUBS			:=  	1
# Set to 1 for a per-game NRO with assets embedded in RomFS
MONO_NX_USE_ROMFS			:=  	0
# Terraria 1.4.5 logs SDL3 versions before FNA's SDL2 backend is selected
MONO_NX_USE_SDL3_COMPAT		:=  	1
# Optional precompiled game/corelib modules, with interpreter fallback (not JIT)
MONO_NX_USE_AOT			?=	0
MONO_NX_AOT_DIR			?=	$(TOPDIR)/aot
# Leave SDL's known-working profile unchanged unless explicitly comparing GL
MONO_NX_GL_COMPAT		?=	0
# Load CoreLib/framework from RomFS (romfs:/mono/...) instead of the shared SD
# /mono/lib_net9.0 and /mono/framework_net9.0. Lets one NRO ship its own BCL
# (e.g. a Release build) without breaking other NROs' AOT MVID bindings.
MONO_NX_EMBEDDED_BCL		?=	0
# Fatal Mono errors: flush the log and abort with an Atmosphère crash report
# (native stack) instead of losing the message or exiting through the applet.
MONO_NX_FATAL_DIAG		?=	0

#---------------------------------------------------------------------------------
# Code generation options
#---------------------------------------------------------------------------------
ARCH	:=	-march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=soft -fPIE

CFLAGS	:=	-g -Wall -O2 -ffunction-sections \
			$(ARCH) $(DEFINES)

CFLAGS	+=	$(INCLUDE) -D__SWITCH__ \
			-I$(MONO_NX_DIR)/native/shared \
			-I$(MONO_NX_ROOT)/artifacts/bin/mono/libnx.arm64.Debug/include/mono-2.0 \
			-I$(ICU_NX_INSTALL_DIR)/include \
			-I/opt/devkitpro/portlibs/switch/include/SDL2/ \
			-I$(FNA_NX_INSTALL_DIR)/include \
			-DU_DISABLE_RENAMING=1

CXXFLAGS	:= $(CFLAGS) -fno-rtti -fno-exceptions -std=c++17

ASFLAGS	:=	-g $(ARCH)
LDFLAGS	=	-specs=$(DEVKITPRO)/libnx/switch.specs -g $(ARCH) -Wl,-Map,$(notdir $*.map)
# Wrap calls and function-address relocations in the static SDL/FNA3D dl-shims.
LDFLAGS += -Wl,--wrap=SDL_PollEvent -Wl,--wrap=FNA3D_SwapBuffers

#---------------------------------------------------------------------------------
# Libraries
#---------------------------------------------------------------------------------
# Keep the shipped SDK as the default; a matching source-built fix is explicit.
MONO_NX_RUNTIME_LIBRARY ?= $(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/out/lib/libmonosgen-2.0.a

ifeq ($(MONO_NX_USE_AOT),1)
	ifeq ($(wildcard $(MONO_NX_AOT_DIR)/mono_aot_modules.h),)
		$(error "AOT requested without a generated mono_aot_modules.h")
	endif
	ifeq ($(wildcard $(MONO_NX_AOT_DIR)/System.Private.CoreLib.dll.o),)
		$(error "AOT plus interpreter requires precompiled corelib trampolines")
	endif
	AOT_OBJECTS := $(wildcard $(MONO_NX_AOT_DIR)/*.o)
	CFLAGS += -DMONO_NX_USE_AOT=1 -I$(MONO_NX_AOT_DIR)
	AOT_LINKER_SCRIPT := $(TOPDIR)/aot-method-tables.ld
	AOT_LINKER_SPECS := $(TOPDIR)/aot-method-tables.specs
	LDFLAGS += -specs=$(AOT_LINKER_SPECS)
endif
ifeq ($(MONO_NX_GL_COMPAT),1)
	CFLAGS += -DMONO_NX_GL_COMPAT=1
endif
ifeq ($(MONO_NX_EMBEDDED_BCL),1)
	ifneq ($(MONO_NX_USE_ROMFS),1)
		$(error "MONO_NX_EMBEDDED_BCL=1 requires MONO_NX_USE_ROMFS=1")
	endif
	CFLAGS += -DMONO_NX_EMBEDDED_BCL=1
endif
ifeq ($(MONO_NX_FATAL_DIAG),1)
	CFLAGS += -DMONO_NX_FATAL_DIAG=1
endif

LIBS	:=  \
			$(AOT_OBJECTS) \
			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.IO.Compression.Native.a \
  			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.Globalization.Native.a \
			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.Native.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicui18n.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicuuc.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicudata.a \
			$(MONO_NX_RUNTIME_LIBRARY) \
			$(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/out/lib/libmono-component-debugger-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/out/lib/libmono-component-diagnostics_tracing-stub-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/out/lib/libmono-component-hot_reload-stub-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/out/lib/libmono-component-marshal-ilgen-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/libnx.arm64.Debug/_deps/fetchzlibng-build/libz.a \
			-pthread

#---------------------------------------------------------------------------------
# Optional library linking
#---------------------------------------------------------------------------------

ifeq ($(MONO_NX_USE_SDL2),1)
	SOURCES		+=	../shared_mono_nx/dl_shim_sdl2 \
					../shared_mono_nx/dl_shim_sdl2_image
	CFLAGS		+=	-DDLSHIM_SDL2=1 -DDLSHIM_SDL2_IMAGE=1
	LIBS		+=	`$(PREFIX)pkg-config --libs sdl2 SDL2_image`
endif

ifeq ($(MONO_NX_USE_OPENGL),1)
	SOURCES		+=	../shared_mono_nx/dl_shim_opengl
	CFLAGS		+=	-DDLSHIM_OPENGL=1
	LIBS		+=	-lglad -lEGL -lglapi -ldrm_nouveau
endif

ifeq ($(MONO_NX_USE_OPENAL),1)
	SOURCES		+=	../shared_mono_nx/dl_shim_openal
	CFLAGS		+=	-DDLSHIM_OPENAL=1
	LIBS		+=	`$(PREFIX)pkg-config --libs openal`
endif

ifeq ($(MONO_NX_USE_FNA3D),1)
	SOURCES		+=	../shared/dl_shim_FNA3D
	CFLAGS		+=	-DDLSHIM_FNA3D=1
	LIBS		+=	$(FNA_NX_INSTALL_DIR)/lib/libFNA3D.a $(FNA_NX_INSTALL_DIR)/lib/libmojoshader.a
endif

ifeq ($(MONO_NX_USE_FNAUDIO),1)
	SOURCES		+=	../shared/dl_shim_FAudio
	CFLAGS		+=	-DDLSHIM_FNA=1
	LIBS		+=	$(FNA_NX_INSTALL_DIR)/lib/libFAudio.a
endif

ifeq ($(MONO_NX_USE_STUBS),1)
	CFLAGS		+=	-DDLSHIM_STUBS=1
endif
ifeq ($(MONO_NX_USE_SDL3_COMPAT),1)
	CFLAGS		+=	-DDLSHIM_SDL3=1
endif
ifeq ($(MONO_NX_USE_ROMFS),1)
	CFLAGS		+=	-DMONO_NX_USE_ROMFS=1
	ROMFS		:=	romfs
endif
LIBS	+=	-lnx -lm -lstdc++

#---------------------------------------------------------------------------------
# Library directories
#---------------------------------------------------------------------------------
LIBDIRS	:= $(PORTLIBS) $(LIBNX) \
			$(FNA_NX_INSTALL_DIR)

#---------------------------------------------------------------------------------
# no real need to edit anything past this point
#---------------------------------------------------------------------------------
ifneq ($(BUILD),$(notdir $(CURDIR)))
#---------------------------------------------------------------------------------

export OUTPUT	:=	$(CURDIR)/$(TARGET)
export TOPDIR	:=	$(CURDIR)

export VPATH	:=	$(foreach dir,$(SOURCES),$(CURDIR)/$(dir)) \
			$(foreach dir,$(DATA),$(CURDIR)/$(dir))

export DEPSDIR	:=	$(CURDIR)/$(BUILD)

CFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.c)))
CPPFILES	:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.cpp)))
SFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.s)))
BINFILES	:=	$(foreach dir,$(DATA),$(notdir $(wildcard $(dir)/*.*)))

#---------------------------------------------------------------------------------
ifeq ($(strip $(CPPFILES)),)
	export LD	:=	$(CC)
else
	export LD	:=	$(CXX)
endif
#---------------------------------------------------------------------------------

export OFILES_BIN	:=	$(addsuffix .o,$(BINFILES))
export OFILES_SRC	:=	$(CPPFILES:.cpp=.o) $(CFILES:.c=.o) $(SFILES:.s=.o)
export OFILES 	:=	$(OFILES_BIN) $(OFILES_SRC)
export HFILES_BIN	:=	$(addsuffix .h,$(subst .,_,$(BINFILES)))

export INCLUDE	:=	$(foreach dir,$(INCLUDES),-I$(CURDIR)/$(dir)) \
			$(foreach dir,$(LIBDIRS),-I$(dir)/include) \
			-I$(CURDIR)/$(BUILD)

export LIBPATHS	:=	$(foreach dir,$(LIBDIRS),-L$(dir)/lib)

ifeq ($(strip $(ICON)),)
	icons := $(wildcard *.jpg)
	ifneq (,$(findstring $(TARGET).jpg,$(icons)))
		export APP_ICON := $(TOPDIR)/$(TARGET).jpg
	else
		ifneq (,$(findstring icon.jpg,$(icons)))
			export APP_ICON := $(TOPDIR)/icon.jpg
		endif
	endif
else
	export APP_ICON := $(TOPDIR)/$(ICON)
endif

ifeq ($(strip $(NO_ICON)),)
	export NROFLAGS += --icon=$(APP_ICON)
endif

ifeq ($(strip $(NO_NACP)),)
	export NROFLAGS += --nacp=$(CURDIR)/$(TARGET).nacp
endif
ifneq ($(ROMFS),)
	export NROFLAGS += --romfsdir=$(CURDIR)/$(ROMFS)
endif

.PHONY: $(BUILD) clean all

#---------------------------------------------------------------------------------
all: $(BUILD)

$(BUILD):
	@[ -d $@ ] || mkdir -p $@
	@$(MAKE) --no-print-directory -C $(BUILD) -f $(CURDIR)/Makefile

#---------------------------------------------------------------------------------
clean:
	@echo clean ...
	@rm -fr $(BUILD) $(TARGET).nro $(TARGET).nacp $(TARGET).elf

#---------------------------------------------------------------------------------
else
.PHONY:	all

DEPENDS	:=	$(OFILES:.o=.d)

#---------------------------------------------------------------------------------
# main targets
#---------------------------------------------------------------------------------
all	:	$(OUTPUT).nro

$(OUTPUT).nro	:	$(OUTPUT).elf $(OUTPUT).nacp

$(OUTPUT).elf	:	$(OFILES) $(AOT_OBJECTS) $(AOT_LINKER_SCRIPT) $(AOT_LINKER_SPECS) $(MONO_NX_RUNTIME_LIBRARY)

$(OFILES_SRC)	: $(HFILES_BIN)

-include $(DEPENDS)

#---------------------------------------------------------------------------------------
endif
#---------------------------------------------------------------------------------------
