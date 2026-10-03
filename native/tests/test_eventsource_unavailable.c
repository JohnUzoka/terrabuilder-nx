#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <mono/jit/jit.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/class.h>
#include <mono/metadata/object.h>
#include <mono/metadata/image.h>
#include <mono/metadata/mono-config.h>
#include <mono/utils/mono-publib.h>

#include "../shared/nx_runtime_config.h"

static void report(int stage)
{
    printf("MANAGED_STAGE=%d\n", stage);
    fflush(stdout);
}

static void report_text(MonoString *value)
{
    char *text = mono_string_to_utf8(value);
    printf("MANAGED_TEXT=%s\n", text);
    mono_free(text);
    fflush(stdout);
}

int main(int argc, char **argv)
{
    if (argc != 4) {
        fprintf(stderr, "usage: %s default|false <assembly-search-path> <Probe.dll>\n", argv[0]);
        return 64;
    }
    setbuf(stdout, NULL);
    if (strcmp(argv[1], "false") == 0) {
        nx_runtime_register_capabilities();
    } else if (strcmp(argv[1], "default") != 0) {
        return 64;
    }
    mono_set_assemblies_path(argv[2]);
    if (getenv("REPRO_INTERP"))
        mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);
    printf("NATIVE_BEFORE_INIT mode=%s\n", argv[1]);
    MonoDomain *domain = mono_jit_init_version("eventsource-repro", "v4.0.30319");
    if (!domain) return 65;
    printf("NATIVE_CORELIB=%s\n", mono_image_get_filename(mono_get_corlib()));
    mono_add_internal_call("Probe::Report", report);
    mono_add_internal_call("Probe::ReportText", report_text);
    MonoAssembly *assembly = mono_domain_assembly_open(domain, argv[3]);
    if (!assembly) return 66;
    MonoClass *klass = mono_class_from_name(mono_assembly_get_image(assembly), "", "Probe");
    MonoMethod *run = mono_class_get_method_from_name(klass, "Run", 0);
    if (!run) return 67;
    printf("NATIVE_BEFORE_MANAGED\n");
    MonoObject *exception = NULL;
    MonoObject *result = mono_runtime_invoke(run, NULL, NULL, &exception);
    if (exception) {
        MonoClass *exception_class = mono_object_get_class(exception);
        fprintf(stderr, "UNHANDLED=%s.%s\n", mono_class_get_namespace(exception_class), mono_class_get_name(exception_class));
        return 68;
    }
    if (!result) return 69;
    int exit_code = *(int *)mono_object_unbox(result);
    printf("NATIVE_EXIT=%d\n", exit_code);
    mono_jit_cleanup(domain);
    return exit_code;
}
