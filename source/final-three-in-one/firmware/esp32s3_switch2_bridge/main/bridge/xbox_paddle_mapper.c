#include "xbox_paddle_mapper.h"

#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include "app_log.h"
#include "esp_timer.h"
#include "nvs.h"

static const char *TAG = "xbox_paddle";
static const char *NVS_NAMESPACE = "bridge";

#define DEFAULT_TAP_MS 70
#define DEFAULT_TURBO_ON_MS 45
#define DEFAULT_TURBO_OFF_MS 45
#define MIN_PULSE_MS 20
#define MAX_PULSE_MS 1000

typedef struct {
    bool was_pressed;
    int64_t tap_until_us;
    bool turbo_phase_on;
    int64_t turbo_deadline_us;
} runtime_side_t;

typedef struct {
    const char *enabled;
    const char *action;
    const char *mask;
    const char *tap;
    const char *turbo_on;
    const char *turbo_off;
} nvs_keys_t;

typedef struct {
    const char *name;
    internal_gamepad_button_t button;
} button_alias_t;

static xbox_paddle_binding_t s_bindings[XBOX_PADDLE_SIDE_COUNT];
static runtime_side_t s_runtime[XBOX_PADDLE_SIDE_COUNT];

static const nvs_keys_t s_keys[XBOX_PADDLE_SIDE_COUNT] = {
    { "xpl_en", "xpl_mode", "xpl_mask", "xpl_tap", "xpl_on", "xpl_off" },
    { "xpr_en", "xpr_mode", "xpr_mask", "xpr_tap", "xpr_on", "xpr_off" },
};

static const button_alias_t s_button_aliases[] = {
    { "B", INTERNAL_GAMEPAD_BUTTON_SOUTH },
    { "SOUTH", INTERNAL_GAMEPAD_BUTTON_SOUTH },
    { "CROSS", INTERNAL_GAMEPAD_BUTTON_SOUTH },
    { "XB_A", INTERNAL_GAMEPAD_BUTTON_SOUTH },
    { "XBOX_A", INTERNAL_GAMEPAD_BUTTON_SOUTH },

    { "A", INTERNAL_GAMEPAD_BUTTON_EAST },
    { "EAST", INTERNAL_GAMEPAD_BUTTON_EAST },
    { "CIRCLE", INTERNAL_GAMEPAD_BUTTON_EAST },
    { "XB_B", INTERNAL_GAMEPAD_BUTTON_EAST },
    { "XBOX_B", INTERNAL_GAMEPAD_BUTTON_EAST },

    { "Y", INTERNAL_GAMEPAD_BUTTON_WEST },
    { "WEST", INTERNAL_GAMEPAD_BUTTON_WEST },
    { "SQUARE", INTERNAL_GAMEPAD_BUTTON_WEST },
    { "XB_X", INTERNAL_GAMEPAD_BUTTON_WEST },
    { "XBOX_X", INTERNAL_GAMEPAD_BUTTON_WEST },

    { "X", INTERNAL_GAMEPAD_BUTTON_NORTH },
    { "NORTH", INTERNAL_GAMEPAD_BUTTON_NORTH },
    { "TRIANGLE", INTERNAL_GAMEPAD_BUTTON_NORTH },
    { "XB_Y", INTERNAL_GAMEPAD_BUTTON_NORTH },
    { "XBOX_Y", INTERNAL_GAMEPAD_BUTTON_NORTH },

    { "L", INTERNAL_GAMEPAD_BUTTON_L1 },
    { "LB", INTERNAL_GAMEPAD_BUTTON_L1 },
    { "L1", INTERNAL_GAMEPAD_BUTTON_L1 },
    { "R", INTERNAL_GAMEPAD_BUTTON_R1 },
    { "RB", INTERNAL_GAMEPAD_BUTTON_R1 },
    { "R1", INTERNAL_GAMEPAD_BUTTON_R1 },
    { "ZL", INTERNAL_GAMEPAD_BUTTON_L2 },
    { "LT", INTERNAL_GAMEPAD_BUTTON_L2 },
    { "L2", INTERNAL_GAMEPAD_BUTTON_L2 },
    { "ZR", INTERNAL_GAMEPAD_BUTTON_R2 },
    { "RT", INTERNAL_GAMEPAD_BUTTON_R2 },
    { "R2", INTERNAL_GAMEPAD_BUTTON_R2 },

    { "MINUS", INTERNAL_GAMEPAD_BUTTON_BACK },
    { "BACK", INTERNAL_GAMEPAD_BUTTON_BACK },
    { "SELECT", INTERNAL_GAMEPAD_BUTTON_BACK },
    { "PLUS", INTERNAL_GAMEPAD_BUTTON_START },
    { "START", INTERNAL_GAMEPAD_BUTTON_START },
    { "OPTIONS", INTERNAL_GAMEPAD_BUTTON_START },

    { "L3", INTERNAL_GAMEPAD_BUTTON_LSTICK },
    { "LS", INTERNAL_GAMEPAD_BUTTON_LSTICK },
    { "LSTICK", INTERNAL_GAMEPAD_BUTTON_LSTICK },
    { "R3", INTERNAL_GAMEPAD_BUTTON_RSTICK },
    { "RS", INTERNAL_GAMEPAD_BUTTON_RSTICK },
    { "RSTICK", INTERNAL_GAMEPAD_BUTTON_RSTICK },

    { "UP", INTERNAL_GAMEPAD_BUTTON_DPAD_UP },
    { "DOWN", INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN },
    { "LEFT", INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT },
    { "RIGHT", INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT },
    { "DPAD_UP", INTERNAL_GAMEPAD_BUTTON_DPAD_UP },
    { "DPAD_DOWN", INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN },
    { "DPAD_LEFT", INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT },
    { "DPAD_RIGHT", INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT },

    { "HOME", INTERNAL_GAMEPAD_BUTTON_HOME },
    { "GUIDE", INTERNAL_GAMEPAD_BUTTON_HOME },
    { "CAPTURE", INTERNAL_GAMEPAD_BUTTON_CAPTURE },
    { "SHARE", INTERNAL_GAMEPAD_BUTTON_CAPTURE },
    { "C", INTERNAL_GAMEPAD_BUTTON_AUX },
    { "AUX", INTERNAL_GAMEPAD_BUTTON_AUX },
};

static xbox_paddle_binding_t default_binding(void)
{
    return (xbox_paddle_binding_t) {
        .enabled = false,
        .action = XBOX_PADDLE_ACTION_HOLD,
        .target_mask = 0,
        .tap_ms = DEFAULT_TAP_MS,
        .turbo_on_ms = DEFAULT_TURBO_ON_MS,
        .turbo_off_ms = DEFAULT_TURBO_OFF_MS,
    };
}

static uint16_t clamp_pulse_ms(uint16_t value, uint16_t fallback)
{
    if (value == 0) {
        return fallback;
    }
    if (value < MIN_PULSE_MS) {
        return MIN_PULSE_MS;
    }
    if (value > MAX_PULSE_MS) {
        return MAX_PULSE_MS;
    }
    return value;
}

static void sanitize_binding(xbox_paddle_binding_t *binding)
{
    if (!binding) {
        return;
    }
    if (binding->action > XBOX_PADDLE_ACTION_TURBO) {
        binding->action = XBOX_PADDLE_ACTION_HOLD;
    }
    binding->tap_ms = clamp_pulse_ms(binding->tap_ms, DEFAULT_TAP_MS);
    binding->turbo_on_ms = clamp_pulse_ms(binding->turbo_on_ms, DEFAULT_TURBO_ON_MS);
    binding->turbo_off_ms = clamp_pulse_ms(binding->turbo_off_ms, DEFAULT_TURBO_OFF_MS);
    if (binding->target_mask == 0) {
        binding->enabled = false;
    }
}

static bool side_valid(xbox_paddle_side_t side)
{
    return side >= 0 && side < XBOX_PADDLE_SIDE_COUNT;
}

static void load_side(nvs_handle_t handle, xbox_paddle_side_t side)
{
    xbox_paddle_binding_t binding = default_binding();
    const nvs_keys_t *keys = &s_keys[side];
    uint8_t u8 = 0;
    uint16_t u16 = 0;
    uint64_t u64 = 0;

    if (nvs_get_u8(handle, keys->enabled, &u8) == ESP_OK) {
        binding.enabled = u8 != 0;
    }
    if (nvs_get_u8(handle, keys->action, &u8) == ESP_OK) {
        binding.action = (xbox_paddle_action_t)u8;
    }
    if (nvs_get_u64(handle, keys->mask, &u64) == ESP_OK) {
        binding.target_mask = u64;
    }
    if (nvs_get_u16(handle, keys->tap, &u16) == ESP_OK) {
        binding.tap_ms = u16;
    }
    if (nvs_get_u16(handle, keys->turbo_on, &u16) == ESP_OK) {
        binding.turbo_on_ms = u16;
    }
    if (nvs_get_u16(handle, keys->turbo_off, &u16) == ESP_OK) {
        binding.turbo_off_ms = u16;
    }

    sanitize_binding(&binding);
    s_bindings[side] = binding;
}

void xbox_paddle_mapper_init(void)
{
    for (int i = 0; i < XBOX_PADDLE_SIDE_COUNT; ++i) {
        s_bindings[i] = default_binding();
        memset(&s_runtime[i], 0, sizeof(s_runtime[i]));
    }

    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &handle);
    if (err == ESP_OK) {
        load_side(handle, XBOX_PADDLE_SIDE_LEFT);
        load_side(handle, XBOX_PADDLE_SIDE_RIGHT);
        nvs_close(handle);
    } else if (err != ESP_ERR_NVS_NOT_FOUND) {
        APP_LOGW(TAG, "failed to open NVS for paddle bindings err=%d", (int)err);
    }

    char left[96];
    char right[96];
    xbox_paddle_mapper_format_targets(s_bindings[XBOX_PADDLE_SIDE_LEFT].target_mask,
                                      left,
                                      sizeof(left));
    xbox_paddle_mapper_format_targets(s_bindings[XBOX_PADDLE_SIDE_RIGHT].target_mask,
                                      right,
                                      sizeof(right));
    APP_LOGI(TAG, "loaded left=%s/%s/%s right=%s/%s/%s",
             s_bindings[XBOX_PADDLE_SIDE_LEFT].enabled ? "on" : "off",
             xbox_paddle_mapper_action_string(s_bindings[XBOX_PADDLE_SIDE_LEFT].action),
             left,
             s_bindings[XBOX_PADDLE_SIDE_RIGHT].enabled ? "on" : "off",
             xbox_paddle_mapper_action_string(s_bindings[XBOX_PADDLE_SIDE_RIGHT].action),
             right);
}

static bool source_paddle_pressed(const internal_gamepad_state_t *state,
                                  xbox_paddle_side_t side)
{
    internal_gamepad_button_t button = side == XBOX_PADDLE_SIDE_LEFT ?
        INTERNAL_GAMEPAD_BUTTON_PADDLE_LEFT :
        INTERNAL_GAMEPAD_BUTTON_PADDLE_RIGHT;
    return internal_gamepad_state_get_button(state, button);
}

static bool side_output_active(xbox_paddle_side_t side, bool pressed, int64_t now_us)
{
    xbox_paddle_binding_t *binding = &s_bindings[side];
    runtime_side_t *runtime = &s_runtime[side];

    if (!binding->enabled || binding->target_mask == 0) {
        runtime->was_pressed = pressed;
        runtime->tap_until_us = 0;
        runtime->turbo_phase_on = false;
        runtime->turbo_deadline_us = 0;
        return false;
    }

    bool rising = pressed && !runtime->was_pressed;
    runtime->was_pressed = pressed;

    switch (binding->action) {
    case XBOX_PADDLE_ACTION_HOLD:
        return pressed;

    case XBOX_PADDLE_ACTION_TAP:
        if (rising) {
            runtime->tap_until_us = now_us + (int64_t)binding->tap_ms * 1000LL;
        }
        return runtime->tap_until_us > now_us;

    case XBOX_PADDLE_ACTION_TURBO:
        if (!pressed) {
            runtime->turbo_phase_on = false;
            runtime->turbo_deadline_us = 0;
            return false;
        }
        if (rising || runtime->turbo_deadline_us == 0) {
            runtime->turbo_phase_on = true;
            runtime->turbo_deadline_us = now_us + (int64_t)binding->turbo_on_ms * 1000LL;
        } else if (now_us >= runtime->turbo_deadline_us) {
            runtime->turbo_phase_on = !runtime->turbo_phase_on;
            runtime->turbo_deadline_us = now_us +
                (int64_t)(runtime->turbo_phase_on ? binding->turbo_on_ms : binding->turbo_off_ms) * 1000LL;
        }
        return runtime->turbo_phase_on;

    default:
        return pressed;
    }
}

void xbox_paddle_mapper_apply(const internal_gamepad_state_t *src,
                              internal_gamepad_state_t *dst)
{
    if (!src || !dst) {
        return;
    }

    *dst = *src;
    int64_t now_us = esp_timer_get_time();
    for (xbox_paddle_side_t side = XBOX_PADDLE_SIDE_LEFT;
         side < XBOX_PADDLE_SIDE_COUNT;
         side = (xbox_paddle_side_t)(side + 1)) {
        bool pressed = source_paddle_pressed(src, side);
        if (side_output_active(side, pressed, now_us)) {
            dst->buttons |= s_bindings[side].target_mask;
        }
    }
}

void xbox_paddle_mapper_get(xbox_paddle_side_t side,
                            xbox_paddle_binding_t *out)
{
    if (!out) {
        return;
    }
    *out = side_valid(side) ? s_bindings[side] : default_binding();
}

esp_err_t xbox_paddle_mapper_set(xbox_paddle_side_t side,
                                 const xbox_paddle_binding_t *binding)
{
    if (!side_valid(side) || !binding) {
        return ESP_ERR_INVALID_ARG;
    }

    xbox_paddle_binding_t sanitized = *binding;
    sanitize_binding(&sanitized);

    nvs_handle_t handle;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err != ESP_OK) {
        return err;
    }

    const nvs_keys_t *keys = &s_keys[side];
    if (err == ESP_OK) err = nvs_set_u8(handle, keys->enabled, sanitized.enabled ? 1 : 0);
    if (err == ESP_OK) err = nvs_set_u8(handle, keys->action, (uint8_t)sanitized.action);
    if (err == ESP_OK) err = nvs_set_u64(handle, keys->mask, sanitized.target_mask);
    if (err == ESP_OK) err = nvs_set_u16(handle, keys->tap, sanitized.tap_ms);
    if (err == ESP_OK) err = nvs_set_u16(handle, keys->turbo_on, sanitized.turbo_on_ms);
    if (err == ESP_OK) err = nvs_set_u16(handle, keys->turbo_off, sanitized.turbo_off_ms);
    if (err == ESP_OK) err = nvs_commit(handle);
    nvs_close(handle);

    if (err == ESP_OK) {
        s_bindings[side] = sanitized;
        memset(&s_runtime[side], 0, sizeof(s_runtime[side]));
    }
    return err;
}

esp_err_t xbox_paddle_mapper_reset(void)
{
    xbox_paddle_binding_t binding = default_binding();
    esp_err_t left = xbox_paddle_mapper_set(XBOX_PADDLE_SIDE_LEFT, &binding);
    esp_err_t right = xbox_paddle_mapper_set(XBOX_PADDLE_SIDE_RIGHT, &binding);
    return left != ESP_OK ? left : right;
}

static void normalize_token(const char *src, char *dst, size_t dst_len)
{
    if (!dst || dst_len == 0) {
        return;
    }
    size_t used = 0;
    while (src && *src && used + 1 < dst_len) {
        unsigned char ch = (unsigned char)*src++;
        if (ch == '-' || ch == ' ') {
            ch = '_';
        }
        dst[used++] = (char)toupper(ch);
    }
    dst[used] = 0;
}

static bool alias_to_mask(const char *token, uint64_t *mask)
{
    if (!token || !mask || !*token) {
        return false;
    }
    if (strcmp(token, "NONE") == 0 || strcmp(token, "OFF") == 0 || strcmp(token, "DISABLED") == 0) {
        *mask = 0;
        return true;
    }
    for (size_t i = 0; i < sizeof(s_button_aliases) / sizeof(s_button_aliases[0]); ++i) {
        if (strcmp(token, s_button_aliases[i].name) == 0) {
            *mask = 1ULL << s_button_aliases[i].button;
            return true;
        }
    }
    return false;
}

bool xbox_paddle_mapper_parse_targets(const char *text, uint64_t *out_mask)
{
    if (!text || !out_mask) {
        return false;
    }

    uint64_t mask = 0;
    char token[32];
    size_t used = 0;
    bool saw_token = false;

    for (const char *p = text;; ++p) {
        char ch = *p;
        bool separator = ch == 0 || ch == '+' || ch == ',' || ch == '|' ||
                         ch == '&' || isspace((unsigned char)ch);
        if (!separator) {
            if (used + 1 >= sizeof(token)) {
                return false;
            }
            token[used++] = ch;
            continue;
        }

        if (used > 0) {
            token[used] = 0;
            char normalized[32];
            normalize_token(token, normalized, sizeof(normalized));
            uint64_t token_mask = 0;
            if (!alias_to_mask(normalized, &token_mask)) {
                return false;
            }
            mask |= token_mask;
            saw_token = true;
            used = 0;
        }

        if (ch == 0) {
            break;
        }
    }

    if (!saw_token) {
        return false;
    }
    *out_mask = mask;
    return true;
}

bool xbox_paddle_mapper_parse_side(const char *text, xbox_paddle_side_t *out)
{
    if (!text || !out) {
        return false;
    }
    char normalized[16];
    normalize_token(text, normalized, sizeof(normalized));
    if (strcmp(normalized, "LEFT") == 0 || strcmp(normalized, "L") == 0 ||
        strcmp(normalized, "GL") == 0) {
        *out = XBOX_PADDLE_SIDE_LEFT;
        return true;
    }
    if (strcmp(normalized, "RIGHT") == 0 || strcmp(normalized, "R") == 0 ||
        strcmp(normalized, "GR") == 0) {
        *out = XBOX_PADDLE_SIDE_RIGHT;
        return true;
    }
    return false;
}

bool xbox_paddle_mapper_parse_action(const char *text,
                                     xbox_paddle_action_t *out)
{
    if (!text || !out) {
        return false;
    }
    char normalized[16];
    normalize_token(text, normalized, sizeof(normalized));
    if (strcmp(normalized, "HOLD") == 0 || strcmp(normalized, "PRESS") == 0) {
        *out = XBOX_PADDLE_ACTION_HOLD;
        return true;
    }
    if (strcmp(normalized, "TAP") == 0 || strcmp(normalized, "SINGLE") == 0 ||
        strcmp(normalized, "ONCE") == 0) {
        *out = XBOX_PADDLE_ACTION_TAP;
        return true;
    }
    if (strcmp(normalized, "TURBO") == 0 || strcmp(normalized, "RAPID") == 0 ||
        strcmp(normalized, "REPEAT") == 0) {
        *out = XBOX_PADDLE_ACTION_TURBO;
        return true;
    }
    return false;
}

const char *xbox_paddle_mapper_side_string(xbox_paddle_side_t side)
{
    switch (side) {
    case XBOX_PADDLE_SIDE_LEFT:
        return "left";
    case XBOX_PADDLE_SIDE_RIGHT:
        return "right";
    default:
        return "unknown";
    }
}

const char *xbox_paddle_mapper_action_string(xbox_paddle_action_t action)
{
    switch (action) {
    case XBOX_PADDLE_ACTION_HOLD:
        return "hold";
    case XBOX_PADDLE_ACTION_TAP:
        return "tap";
    case XBOX_PADDLE_ACTION_TURBO:
        return "turbo";
    default:
        return "unknown";
    }
}

static const char *button_name(internal_gamepad_button_t button)
{
    switch (button) {
    case INTERNAL_GAMEPAD_BUTTON_SOUTH:
        return "B";
    case INTERNAL_GAMEPAD_BUTTON_EAST:
        return "A";
    case INTERNAL_GAMEPAD_BUTTON_WEST:
        return "Y";
    case INTERNAL_GAMEPAD_BUTTON_NORTH:
        return "X";
    case INTERNAL_GAMEPAD_BUTTON_L1:
        return "L";
    case INTERNAL_GAMEPAD_BUTTON_R1:
        return "R";
    case INTERNAL_GAMEPAD_BUTTON_L2:
        return "ZL";
    case INTERNAL_GAMEPAD_BUTTON_R2:
        return "ZR";
    case INTERNAL_GAMEPAD_BUTTON_BACK:
        return "MINUS";
    case INTERNAL_GAMEPAD_BUTTON_START:
        return "PLUS";
    case INTERNAL_GAMEPAD_BUTTON_LSTICK:
        return "L3";
    case INTERNAL_GAMEPAD_BUTTON_RSTICK:
        return "R3";
    case INTERNAL_GAMEPAD_BUTTON_DPAD_DOWN:
        return "DOWN";
    case INTERNAL_GAMEPAD_BUTTON_DPAD_RIGHT:
        return "RIGHT";
    case INTERNAL_GAMEPAD_BUTTON_DPAD_LEFT:
        return "LEFT";
    case INTERNAL_GAMEPAD_BUTTON_DPAD_UP:
        return "UP";
    case INTERNAL_GAMEPAD_BUTTON_HOME:
        return "HOME";
    case INTERNAL_GAMEPAD_BUTTON_CAPTURE:
        return "CAPTURE";
    case INTERNAL_GAMEPAD_BUTTON_AUX:
        return "C";
    default:
        return NULL;
    }
}

void xbox_paddle_mapper_format_targets(uint64_t mask, char *out, size_t out_len)
{
    if (!out || out_len == 0) {
        return;
    }
    out[0] = 0;
    if (mask == 0) {
        snprintf(out, out_len, "NONE");
        return;
    }

    size_t used = 0;
    for (internal_gamepad_button_t button = 0;
         button < INTERNAL_GAMEPAD_BUTTON_COUNT;
         button = (internal_gamepad_button_t)(button + 1)) {
        if ((mask & (1ULL << button)) == 0) {
            continue;
        }
        const char *name = button_name(button);
        if (!name) {
            continue;
        }
        int written = snprintf(out + used,
                               used < out_len ? out_len - used : 0,
                               "%s%s",
                               used > 0 ? "+" : "",
                               name);
        if (written <= 0) {
            break;
        }
        used += (size_t)written;
        if (used >= out_len) {
            out[out_len - 1] = 0;
            break;
        }
    }
    if (out[0] == 0) {
        snprintf(out, out_len, "NONE");
    }
}
