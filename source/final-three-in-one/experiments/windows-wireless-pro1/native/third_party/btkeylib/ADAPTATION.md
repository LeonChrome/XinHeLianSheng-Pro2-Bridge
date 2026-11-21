# btkeyLib for Pro1 Bluetooth output

The complete Windows example is included from [mizuyoukanao/btstack at 4b49cb955d08dc80a82673083fc0463cbc0c2540](https://github.com/mizuyoukanao/btstack/tree/4b49cb955d08dc80a82673083fc0463cbc0c2540/example).
The source, header and [original license](https://github.com/mizuyoukanao/btstack/blob/4b49cb955d08dc80a82673083fc0463cbc0c2540/LICENSE) are unmodified. The original license restricts use and distribution to personal, noncommercial purposes.

- `btkeyLib.c` Git blob `7cfc24d6613f4a8a4d2c8f4f1cd31dfbc8b06753`
- `btkeyLib.h` Git blob `e0ec455881bbb0cdeddc1fcce6bd964221e3351b`
- `LICENSE` Git blob `d0323cbda0ea0755f415e5803e5a8b52627e1580`

`relay_btkey_adapter.c` connects upstream HID, SDP, and handshake state to the existing relay's WinUSB transport and same-peer checks. It replaces the upstream blocking delay with a BTstack timer for regular reports, capped at 60 Hz. On CAN_SEND, it combines the upstream packet with the latest normalized controller snapshot and matching calibration. Native code owns the replies and the managed app sends physical rumble and light commands.

The adapter accepts a validated 540-byte NTAG215 snapshot from the private session directory. Empty scans report no tag, and an unsigned dump carries no borrowed originality signature. Console recognition and NFC transfer are subject to hardware verification.

Pro1 pairing uses the reduced regular-report cadence and one-second menu transition documented by [pcble2gamepad's Classic Pro Controller test](https://github.com/wozt/pcble2gamepad/blob/97084dbdc6f83d16663b32a0721ec6b713e2bbe6/docs/classic-pro-poc.md#change-griporder-transition-investigation). The Bluetooth backend also restarts the hold when the Switch exits Sniff, including exits initiated with another controller. Prompt replies to new host subcommands continue during the hold.

Every HID session starts in Nintendo simple mode (`0x3F`). While waiting for the host, the adapter can repeat the initial state up to six times at 500 ms intervals. Once the host sends a command, simple input becomes change-driven. Re-entering simple mode sends a fresh snapshot. The first full report (`0x30`) is scheduled after the host's mode ACK, and only a full report accepted for submission marks the current link as initialized. The startup sequence follows the capture notes from [Bluetooth NES Advantage](https://github.com/cajunpanda/bluetooth-nes-advantage/blob/main/docs/switch_pro_protocol.md).

Host subcommands and NFC requests share a FIFO of 32 entries. The active command survives a failed submission. On overflow the adapter retains the queued commands and counts the rejected new entry. Closing the HID session clears the queue. Ordinary rumble reports pass through independently.

The pinned sender zero-pads `0x21` subcommand replies to 64 bytes. The registered HID descriptor declares 48 bytes after the report ID. The adapter sends exactly 50 bytes including the `A1` transaction header, keeping every declared byte. It consumes an ordinary reply after successful submission so the next send returns to input. Unsupported `0x01` subcommands answer once with a negative ACK and echo the current command ID. The existing NFC state machine still owns its own transitions.

The `0x02` Device Info response for Output report `0x01` uses the real Bluetooth adapter address in `bd_addr_t` display order and preserves the pinned firmware, Pro Controller type and color flag. The `0x04`, `0x21`, `0x30`, `0x40` and `0x48` reply templates remain pinned. Standard input and ACK headers take the three battery-level bits from the shared snapshot and use the wireless Pro Controller low bits. SPI reads echo the requested address and byte count, with room for up to 29 bytes in the 50-byte response. The adapter supplies normalized public calibration, color and parameter bytes and returns erased bytes outside those regions. SPI diagnostics include the address, length and whether public data was patched.

The pinned HID SDP record enables Virtual Cable, Remote Wake and Reconnect Initiate. Its Supervision Timeout is `0x0c80` and its SSR Host Max Latency and Host Min Timeout are `0x0640` and `0x0320`, matching the [joycontrol HID record](https://github.com/mart1nro/joycontrol/blob/master/joycontrol/profile/sdp_record_hid.xml). The separate PnP SDP record uses USB vendor source, `057e:2009` and version `0x0001`. The pinned BTstack serializes these settings as SDP attributes. The adapter initializes HID in report protocol only, in line with the SDP Boot Device attribute.
