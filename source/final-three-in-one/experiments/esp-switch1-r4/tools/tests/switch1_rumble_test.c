#include <stdio.h>
#include <stdlib.h>

#include "normalized_rumble.h"

static void expect_u8(const char *name, unsigned expected, unsigned actual)
{
    if (expected != actual) {
        fprintf(stderr, "%s: expected %u, got %u\n", name, expected, actual);
        exit(1);
    }
}

int main(void)
{
    const uint8_t neutral[4] = {0x00, 0x01, 0x40, 0x40};
    const uint8_t maximum[4] = {0x00, 0xc9, 0xc0, 0x72};
    normalized_rumble_t rumble;

    normalized_rumble_from_switch1_hd(neutral, neutral, 70, &rumble);
    expect_u8("neutral stop", 1, rumble.stop);
    expect_u8("neutral weak", 0, rumble.weak);
    expect_u8("neutral strong", 0, rumble.strong);

    normalized_rumble_from_switch1_hd(maximum, maximum, 70, &rumble);
    expect_u8("maximum active", 0, rumble.stop);
    expect_u8("maximum weak", 255, rumble.weak);
    expect_u8("maximum strong", 255, rumble.strong);
    expect_u8("maximum left", 100, rumble.left_gain_percent);
    expect_u8("maximum right", 100, rumble.right_gain_percent);

    normalized_rumble_from_switch1_hd(maximum, neutral, 70, &rumble);
    expect_u8("left-only left", 100, rumble.left_gain_percent);
    expect_u8("left-only right", 0, rumble.right_gain_percent);

    puts("Switch 1 rumble translation tests passed");
    return 0;
}
