#ifndef NX_RUNTIME_CONFIG_H
#define NX_RUNTIME_CONFIG_H

#ifdef __cplusplus
extern "C" {
#endif
void mono_runtime_register_appctx_properties(int count, const char **keys, const char **values);
#ifdef __cplusplus
}
#endif

/* libnx Mono has no native EventPipe provider. Register before runtime startup. */
static inline void nx_runtime_register_capabilities(void)
{
    const char *keys[] = { "System.Diagnostics.Tracing.EventSource.IsSupported" };
    const char *values[] = { "false" };
    mono_runtime_register_appctx_properties(1, keys, values);
}

#endif
