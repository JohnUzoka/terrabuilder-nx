#ifndef NX_INPUT_H
#define NX_INPUT_H

#ifdef __cplusplus
extern "C" {
#endif

/* Register after successful mono_jit_init, before executing the game assembly;
 * sampling starts on a managed phase hook once SDL's joystick subsystem is
 * initialized. Shut down after mono_jit_exec, before runtime/HID teardown.
 */
void nx_input_register(void);
void nx_input_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif
