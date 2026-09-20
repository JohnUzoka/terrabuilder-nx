#ifndef NX_INPUT_LATCH_H
#define NX_INPUT_LATCH_H

#include <stdint.h>

/* One pending rising edge per button, not an unbounded event queue. The caller
 * serializes sampling/reset/advance. Only advance writes presented, so reads
 * remain identical throughout a managed update even if the device disconnects.
 */
typedef struct NxButtonLatch {
    uint32_t held;
    uint32_t pending;
    uint32_t presented;
    uint8_t reset_pending;
} NxButtonLatch;

static inline void nx_button_latch_reset(NxButtonLatch *latch)
{
    latch->held = 0;
    latch->pending = 0;
    latch->reset_pending = 1;
}

static inline void nx_button_latch_sample(NxButtonLatch *latch, uint32_t held)
{
    latch->pending |= held & ~latch->held;
    latch->held = held;
}

static inline uint32_t nx_button_latch_advance(NxButtonLatch *latch)
{
    if (latch->reset_pending) {
        latch->presented = 0;
        latch->reset_pending = 0;
    }

    /* A release/re-press while the last update saw down needs a released update
     * before the new press. Keep that one edge for the following update. Other
     * buttons advance independently; steady holds never manufacture repeats.
     */
    uint32_t deferred = latch->presented & latch->pending;
    latch->presented = (latch->held | latch->pending) & ~deferred;
    latch->pending = deferred;
    return latch->presented;
}

#endif
