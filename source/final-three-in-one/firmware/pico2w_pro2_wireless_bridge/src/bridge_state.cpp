#include "bridge_state.hpp"

#include <array>

#include "imu_converter.hpp"
#include "pico/critical_section.h"

namespace bridge {
namespace {

critical_section_t g_state_lock;
pro2::InputState g_latest_state;
uint32_t g_generation = 0;
bool g_have_state = false;
constexpr size_t kHistoryCapacity = 16;
std::array<pro2::InputState, kHistoryCapacity> g_history{};
std::array<uint32_t, kHistoryCapacity> g_history_generations{};
size_t g_history_write = 0;
size_t g_history_count = 0;

} // namespace

void state_init() {
    critical_section_init(&g_state_lock);
    g_latest_state = {};
    g_generation = 0;
    g_have_state = false;
    g_history_write = 0;
    g_history_count = 0;
}

void publish_input(const pro2::InputState &state) {
    imu::observe(state);
    critical_section_enter_blocking(&g_state_lock);
    g_latest_state = state;
    ++g_generation;
    g_have_state = true;
    g_history[g_history_write] = state;
    g_history_generations[g_history_write] = g_generation;
    g_history_write = (g_history_write + 1) % kHistoryCapacity;
    if (g_history_count < kHistoryCapacity) {
        ++g_history_count;
    }
    critical_section_exit(&g_state_lock);
}

void clear_input() {
    critical_section_enter_blocking(&g_state_lock);
    g_latest_state = {};
    ++g_generation;
    g_have_state = false;
    g_history_write = 0;
    g_history_count = 0;
    critical_section_exit(&g_state_lock);
}

bool read_latest_input(pro2::InputState *state, uint32_t *generation) {
    if (state == nullptr || generation == nullptr) {
        return false;
    }

    critical_section_enter_blocking(&g_state_lock);
    const bool have_state = g_have_state;
    if (have_state) {
        *state = g_latest_state;
        *generation = g_generation;
    }
    critical_section_exit(&g_state_lock);
    return have_state;
}

size_t read_recent_inputs(pro2::InputState *states, uint32_t *generations,
                          size_t capacity) {
    if (states == nullptr || capacity == 0) {
        return 0;
    }

    critical_section_enter_blocking(&g_state_lock);
    const size_t count = capacity < g_history_count ? capacity : g_history_count;
    const size_t oldest =
        (g_history_write + kHistoryCapacity - count) % kHistoryCapacity;
    for (size_t index = 0; index < count; ++index) {
        const size_t source = (oldest + index) % kHistoryCapacity;
        states[index] = g_history[source];
        if (generations != nullptr) {
            generations[index] = g_history_generations[source];
        }
    }
    critical_section_exit(&g_state_lock);
    return count;
}

size_t read_resampled_inputs(pro2::InputState *states,
                             uint32_t *generations, size_t capacity,
                             uint64_t sample_period_us) {
    if (states == nullptr || capacity == 0 || sample_period_us == 0) {
        return 0;
    }

    critical_section_enter_blocking(&g_state_lock);
    if (!g_have_state || g_history_count == 0) {
        critical_section_exit(&g_state_lock);
        return 0;
    }

    const size_t oldest =
        (g_history_write + kHistoryCapacity - g_history_count) %
        kHistoryCapacity;
    const size_t newest =
        (g_history_write + kHistoryCapacity - 1) % kHistoryCapacity;
    const uint64_t anchor_us = g_history[newest].received_at_us;
    for (size_t output = 0; output < capacity; ++output) {
        const uint64_t samples_before =
            static_cast<uint64_t>(capacity - output - 1);
        const uint64_t offset_us = samples_before * sample_period_us;
        const uint64_t target_us =
            anchor_us >= offset_us ? anchor_us - offset_us : 0;

        size_t best = oldest;
        uint64_t best_distance = UINT64_MAX;
        for (size_t history = 0; history < g_history_count; ++history) {
            const size_t candidate =
                (oldest + history) % kHistoryCapacity;
            const uint64_t timestamp =
                g_history[candidate].received_at_us;
            const uint64_t distance =
                timestamp >= target_us ? timestamp - target_us
                                       : target_us - timestamp;
            if (distance <= best_distance) {
                best = candidate;
                best_distance = distance;
            }
        }
        states[output] = g_history[best];
        if (generations != nullptr) {
            generations[output] = g_history_generations[best];
        }
    }
    critical_section_exit(&g_state_lock);
    return capacity;
}

} // namespace bridge
