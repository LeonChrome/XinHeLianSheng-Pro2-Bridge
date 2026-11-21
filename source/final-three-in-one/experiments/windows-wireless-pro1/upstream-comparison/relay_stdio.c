#include "relay_stdio.h"
#include "relay_nfc.h"
#include "btkeyLib.h"
#include <string.h>

enum { FRAME_SIZE = 28, MAX_EDGES = 16, STALE_MS = 250, BACKLOG_FRAMES = 24 };
struct relay_state {
    uint32_t buttons;
    uint16_t sticks[4];
    int16_t imu[6];
};
static const struct relay_state neutral = {0, {2048, 2048, 2048, 2048}, {0}};
static struct relay_state latest;
static uint32_t edges[MAX_EDGES];
static unsigned head, count;
static uint8_t partial[FRAME_SIZE];
static size_t buffered;
static HANDLE input_pipe;
static uint64_t last_frame, last_tick, discard_bytes, total, resyncs;
static uint32_t applied_mask, observed_mask;
static uint32_t probe_overlay;
static bool active, waiting_for_report, need_neutral_report;

static uint16_t word(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }

static bool accept_frame(const uint8_t *frame, uint64_t now, bool ready, bool stale) {
    if (!memcmp(frame, "P1NF", 4)) {
        relay_nfc_record(frame);
        return true;
    }
    if (memcmp(frame, "P1IN", 4) || frame[7]) return false;
    struct relay_state data;
    data.buttons = word(frame + 4) | ((uint32_t)frame[6] << 16);
    for (unsigned i = 0; i < 4; ++i) {
        data.sticks[i] = word(frame + 8 + i * 2);
        if (data.sticks[i] > 4095) return false;
    }
    for (unsigned i = 0; i < 6; ++i) data.imu[i] = (int16_t)word(frame + 16 + i * 2);
    ++total;
    if (stale) return true;
    if (ready && data.buttons != latest.buttons) {
        if (count == MAX_EDGES) {
            head = count = 0;
            waiting_for_report = false;
            ++resyncs;
        }
        edges[(head + count) % MAX_EDGES] = data.buttons;
        ++count;
    }
    latest = data;
    last_frame = now;
    return true;
}

static void apply(const struct relay_state *state, uint32_t buttons) {
    applied_mask = buttons;
    send_button(buttons, 0);
    send_stick_l(state->sticks[0], state->sticks[1], 0);
    send_stick_r(state->sticks[2], state->sticks[3], 0);
    send_accel(state->imu[0], state->imu[1], state->imu[2]);
    send_gyro(state->imu[3], state->imu[4], state->imu[5]);
}

void relay_stdio_observe_input(uint32_t mask) {
    observed_mask = mask;
    if (!mask) need_neutral_report = false;
    if (count && waiting_for_report && mask == (edges[head] | probe_overlay)) {
        head = (head + 1) % MAX_EDGES;
        --count;
        waiting_for_report = false;
    }
}

bool relay_stdio_input_live(uint64_t now) {
    return active && !need_neutral_report && last_frame && now - last_frame <= STALE_MS;
}

bool relay_stdio_probe_home(bool press, uint64_t now) {
    if (!press) {
        probe_overlay = 0;
        return true;
    }
    if (!relay_stdio_input_live(now) || ((latest.buttons | applied_mask | observed_mask) & 0x1000)) return false;
    probe_overlay = 0x1000;
    return true;
}

bool relay_stdio_open(HANDLE pipe) {
    if (!pipe || pipe == INVALID_HANDLE_VALUE || GetFileType(pipe) != FILE_TYPE_PIPE) return false;
    input_pipe = pipe;
    latest = neutral;
    return true;
}

enum relay_pipe_state relay_stdio_tick(uint64_t now, bool ready) {
    uint8_t bytes[FRAME_SIZE * 64];
    DWORD available = 0, used = 0;
    if (!PeekNamedPipe(input_pipe, NULL, 0, NULL, &available, NULL) ||
        (available && !ReadFile(input_pipe, bytes, available < sizeof(bytes) ? available : sizeof(bytes), &used, NULL))) {
        DWORD err = GetLastError();
        apply(&neutral, 0);
        return !buffered && (err == ERROR_BROKEN_PIPE || err == ERROR_PIPE_NOT_CONNECTED)
             ? RELAY_PIPE_CLOSED : RELAY_PIPE_INVALID;
    }
    /* P1IN has no sender timestamp. After a stall, discard all bytes known to
     * precede this observation, including the tail of an incomplete record.
     * Only a complete subsequent record can restore non-neutral output. */
    bool stalled = last_tick && now - last_tick >= STALE_MS;
    /* A complete signed tag occupies 30 control records in one write.
     * These bytes do not represent elapsed gameplay samples. */
    unsigned gameplay_bytes = available;
    unsigned aligned = buffered ? FRAME_SIZE - (unsigned)buffered : 0;
    for (unsigned pos = aligned; pos + FRAME_SIZE <= used; pos += FRAME_SIZE) {
        if (!memcmp(bytes + pos, "P1NF", 4)) gameplay_bytes -= FRAME_SIZE;
    }
    if (stalled || (!discard_bytes && gameplay_bytes > FRAME_SIZE * BACKLOG_FRAMES)) {
        uint64_t pending = available;
        unsigned tail = (unsigned)((buffered + pending) % FRAME_SIZE);
        discard_bytes = pending + (tail ? FRAME_SIZE - tail : 0);
        need_neutral_report = ready && (need_neutral_report || applied_mask || observed_mask);
        apply(&neutral, 0);
        active = waiting_for_report = false;
        latest = neutral;
        head = count = 0;
        last_frame = 0;
        ++resyncs;
    }
    last_tick = now;
    if (!ready) need_neutral_report = false;
    for (DWORD i = 0; i < used; ++i) {
        bool stale = discard_bytes != 0;
        if (stale) --discard_bytes;
        partial[buffered++] = bytes[i];
        if (buffered == FRAME_SIZE) {
            buffered = 0;
            if (!accept_frame(partial, now, ready && !need_neutral_report, stale)) {
                apply(&neutral, 0);
                return RELAY_PIPE_INVALID;
            }
        }
    }
    if (ready && !need_neutral_report && last_frame && now - last_frame <= STALE_MS) {
        active = true;
        waiting_for_report = count > 0;
        apply(&latest, (count ? edges[head] : latest.buttons) | probe_overlay);
    } else {
        if (active) apply(&neutral, 0);
        active = waiting_for_report = false;
        head = count = 0;
        if (last_frame && now - last_frame > STALE_MS) latest = neutral;
    }
    return RELAY_PIPE_OPEN;
}

unsigned long long relay_stdio_frames(void) { return total; }
unsigned long long relay_stdio_coalesced_edges(void) { return resyncs; }

bool relay_stdio_fixture(void) {
    HANDLE rd, wr;
    if (!CreatePipe(&rd, &wr, NULL, 0)) return false;
    bool ok = relay_stdio_open(rd);
    uint8_t frame[FRAME_SIZE] = {'P','1','I','N',0,0x10,0,0,0xaa,0x03,0x55,0x0b};
    frame[16] = 0xff; frame[17] = 0xff;
    DWORD written;
    ok = ok && WriteFile(wr, frame, 7, &written, NULL) &&
        relay_stdio_tick(100, true) == RELAY_PIPE_OPEN && !total &&
        WriteFile(wr, frame + 7, FRAME_SIZE - 7, &written, NULL) &&
        relay_stdio_tick(110, true) == RELAY_PIPE_OPEN && total == 1 &&
        latest.buttons == 0x1000 && latest.sticks[0] == 0x3aa && latest.sticks[1] == 0xb55 &&
        latest.imu[0] == -1 && count == 1;
    frame[5] = 0;
    ok = ok && WriteFile(wr, frame, FRAME_SIZE, &written, NULL) &&
         relay_stdio_tick(120, true) == RELAY_PIPE_OPEN && count == 2 && edges[head] == 0x1000;
    relay_stdio_observe_input(0x1000);
    ok = ok && relay_stdio_tick(121, true) == RELAY_PIPE_OPEN && count == 1 && edges[head] == 0;
    relay_stdio_observe_input(0);
    ok = ok && count == 0 && relay_stdio_tick(380, true) == RELAY_PIPE_OPEN && !active;
    frame[4] = 8;
    ok = ok && WriteFile(wr, frame, FRAME_SIZE, &written, NULL) &&
         relay_stdio_tick(382, true) == RELAY_PIPE_OPEN && applied_mask == 8;
    relay_stdio_observe_input(8);
    ok = ok && relay_stdio_probe_home(true, 382) && relay_stdio_tick(383, true) == RELAY_PIPE_OPEN &&
         applied_mask == 0x1008 && latest.buttons == 8;
    relay_stdio_observe_input(0x1008);
    ok = ok && relay_stdio_probe_home(false, 384) && relay_stdio_tick(384, true) == RELAY_PIPE_OPEN &&
         applied_mask == 8 && !relay_stdio_probe_home(true, 900);
    relay_stdio_observe_input(8);
    /* A paused loop resumes with a partial stale press, then gets a fresh
     * press on the same read. The old bit never reaches the controller. */
    frame[4] = 4;
    ok = ok && WriteFile(wr, frame, 11, &written, NULL) &&
         relay_stdio_tick(690, true) == RELAY_PIPE_OPEN && discard_bytes == FRAME_SIZE - 11 &&
         need_neutral_report && !applied_mask && !last_frame &&
         WriteFile(wr, frame + 11, FRAME_SIZE - 11, &written, NULL);
    frame[4] = 2;
    ok = ok && WriteFile(wr, frame, FRAME_SIZE, &written, NULL) &&
         relay_stdio_tick(691, true) == RELAY_PIPE_OPEN && !discard_bytes && !count &&
         !applied_mask && latest.buttons == 2;
    relay_stdio_observe_input(0);
    ok = ok && relay_stdio_tick(692, true) == RELAY_PIPE_OPEN && applied_mask == 2;
    /* Flush more than a single read budget without applying any old edge. */
    uint8_t backlog[65 * FRAME_SIZE];
    for (unsigned i = 0; i < 65; ++i) memcpy(backlog + i * FRAME_SIZE, frame, FRAME_SIZE);
    ok = ok && WriteFile(wr, backlog, sizeof(backlog), &written, NULL) &&
         relay_stdio_tick(693, true) == RELAY_PIPE_OPEN && discard_bytes == FRAME_SIZE &&
         relay_stdio_tick(694, true) == RELAY_PIPE_OPEN && !discard_bytes && !last_frame && !applied_mask;
    relay_stdio_observe_input(0);
    frame[9] = 0xff;
    ok = ok && WriteFile(wr, frame, FRAME_SIZE, &written, NULL) &&
         relay_stdio_tick(697, true) == RELAY_PIPE_INVALID;
    CloseHandle(wr);
    ok = ok && relay_stdio_tick(698, true) == RELAY_PIPE_CLOSED;
    CloseHandle(rd);
    return ok;
}
