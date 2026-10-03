#include "../shared/nx_input_latch.h"

#include <assert.h>
#include <stdio.h>

static void check_button(uint32_t button)
{
    NxButtonLatch latch = {0};

    /* A complete tap between slow updates is not lost. */
    nx_button_latch_sample(&latch, button);
    nx_button_latch_sample(&latch, 0);
    assert(nx_button_latch_advance(&latch) == button);
    assert(nx_button_latch_advance(&latch) == 0);

    /* Holding never synthesizes releases/repeated rising edges. */
    nx_button_latch_sample(&latch, button);
    for (unsigned update = 0; update < 100; ++update)
        assert(nx_button_latch_advance(&latch) == button);

    /* A release/re-press must be visible even between two updates. */
    nx_button_latch_sample(&latch, 0);
    nx_button_latch_sample(&latch, button);
    assert(nx_button_latch_advance(&latch) == 0);
    assert(nx_button_latch_advance(&latch) == button);
    assert(nx_button_latch_advance(&latch) == button);
    nx_button_latch_sample(&latch, 0);
    assert(nx_button_latch_advance(&latch) == 0);

    /* Coalesce unseen taps rather than replaying an unbounded backlog. */
    for (unsigned tap = 0; tap < 1000; ++tap) {
        nx_button_latch_sample(&latch, button);
        nx_button_latch_sample(&latch, 0);
    }
    assert(nx_button_latch_advance(&latch) == button);
    assert(nx_button_latch_advance(&latch) == 0);
    assert(nx_button_latch_advance(&latch) == 0);
}

int main(void)
{
    /* L, R, plus, minus and the four raw SDL D-pad buttons. */
    const unsigned buttons[] = {6, 7, 10, 11, 12, 13, 14, 15};
    for (unsigned i = 0; i < sizeof(buttons) / sizeof(buttons[0]); ++i)
        check_button(UINT32_C(1) << buttons[i]);

    const uint32_t left = UINT32_C(1) << 6;
    const uint32_t right = UINT32_C(1) << 7;
    NxButtonLatch latch = {0};
    nx_button_latch_sample(&latch, left);
    assert(nx_button_latch_advance(&latch) == left);
    nx_button_latch_sample(&latch, right);
    for (unsigned read = 0; read < 100; ++read)
        assert(latch.presented == left);
    assert(nx_button_latch_advance(&latch) == right);

    /* Disconnect/style reset clears pending input, not the active snapshot. */
    nx_button_latch_sample(&latch, left);
    nx_button_latch_reset(&latch);
    assert(latch.presented == right);
    assert(nx_button_latch_advance(&latch) == 0);

    nx_button_latch_sample(&latch, left | right);
    nx_button_latch_sample(&latch, 0);
    assert(nx_button_latch_advance(&latch) == (left | right));
    assert(nx_button_latch_advance(&latch) == 0);

    puts("PASS: taps, holds, release/repress, bounded coalescing, stable reads, reset and simultaneous buttons");
    return 0;
}
