# XinHeLianSheng Three-In-One Control Center: Public Test Guide

中文：[THREE_IN_ONE_R40_TEST_ZH.md](THREE_IN_ONE_R40_TEST_ZH.md)

## Read First

This is an **experimental three-in-one launcher** for three existing implementations, not one rewritten firmware or a universally validated stable product. Development is paused for this publication. Pairing, power-cycle reconnection, rumble and IMU compatibility issues may remain. Previous releases remain available for rollback.

Download: `XinHeLianSheng-Three-In-One-R40-test.exe`, the existing `新和联胜三合一控制中心-R40-test.exe` under an English download filename, with unchanged contents. Windows 10/11 x64. The UI currently uses Chinese; this guide provides English instructions. The approximately 514 MiB executable contains four separate backend applications and their runtimes. Allow time for initial extraction and hashing.

**The embedded Pico firmware is R40, not R55.** A separate optional R55 UF2 is provided for pairing diagnostics; it is not the default firmware and has not passed complete end-to-end acceptance.

## Choose a Route

| Entry | Embedded version | Data path | Intended use |
| --- | --- | --- | --- |
| Pico 2 W backpack | R40 candidate flasher | Wired Pro2 USB -> Pico USB Host -> target Bluetooth | A backpack prototype with Host wiring and stable external 5 V power |
| ESP32-S3 receiver | V5.9.19 candidate | Pro2 BLE -> ESP32-S3 -> target USB | Existing ESP receiver boards |
| ESP Switch 1 experiment | V5.9.18 Switch 1 R4 | Pro2 BLE -> ESP32-S3 -> Switch 1 USB | Experimental first-generation Switch compatibility |
| Windows software route | V6.2.32 Test R2 | Pro2 BLE -> Windows -> VIIPER / USBIP virtual USB | Windows users without a bridge board |

Use one route at a time. Close old Managers, serial monitors and unrelated virtual controller applications. Unplug other bridge boards when testing the software route to avoid extra Steam devices.

The center manages one backend **that it launched**: opening it again brings up the existing window, while changing backend asks you to close the previous one. This is not a global process lock and cannot prevent manually launched old applications. The stop button requests normal closure; it does not forcibly terminate flashing. Closing the center is not confirmation that its backend has exited. End the backend session first.

## Route 1: Pico 2 W

### Hardware and Flashing

Pico reads the real Pro2 through **wired USB**, not BLE. Connect Pro2 to the external USB Host port. Pico's own micro-USB is for flashing and CDC diagnostics, not a wired DualSense output device.

Use previously verified Host wiring and stable 5 V power. Pico 2 W has no lithium charging management and does not turn a bare 3.7 V battery into a regulated USB 5 V supply. Do not combine unknown power sources or change wiring while powered. This guide is not a substitute for a wiring diagram.

1. Disconnect Pro2 and turn off backpack power. Connect only Pico's own USB to Windows with a data cable.
2. Select the Pico route and launch its flasher.
3. If the installed firmware and CDC port respond, use automatic BOOTSEL entry.
4. Otherwise hold BOOTSEL while reconnecting Pico's computer USB, then release it.
5. The disk should be `RP2350`. `RPI-RP2`, RP2040 and Pico 1 are incompatible; do not flash them.
6. Click the embedded firmware flash button. Wait for verification of `firmware_revision=r40` and `runtime_ready=true`; disappearance of the disk alone is not acceptance.
7. Restore backpack power and reconnect Pro2 to the Host port. Pair the selected identity on your target device.

Flashing normally preserves the saved mode and bonds. Edge is the product default, but an existing saved mode takes precedence; flashing does not necessarily reset it to Edge.

### Wireless Modes and Buttons

With live Pro2 USB input, hold **GL + GR + ZL + ZR**, add one selector below, and hold the entire chord for approximately **1.5 seconds**:

| Button | Mode | Typical Bluetooth name | Capabilities and limits |
| --- | --- | --- | --- |
| A | PS5 / DualSense | Wireless Controller | Buttons, sticks, gyro and ordinary rumble implemented; compatibility issues remain |
| B | PS5 Edge | DualSense Edge Wireless Controller | Edge/back-button identity, gyro and ordinary rumble where supported |
| X | Xbox BLE | Xbox Wireless Controller | Ordinary rumble; no gyro in this protocol |
| Y | Switch 1 Pro | Pro Controller | First-generation Switch experiment; pairing and rumble not fully validated |

GL / GR are the two rear buttons; Plus / Minus are the + / - buttons. Do not press multiple selectors. A mode change disconnects the old host. Stored names on multiple PCs do not prove simultaneous input: the firmware is designed for one active identity and one active host.

To use native Pro2 / Switch 2 behavior, **physically disconnect Pro2's Type-C**. A prototype with permanently powered VBUS cannot emulate a true unplug merely by making its data interface silent.

### Pairing, Reconnection and Changing Hosts

1. Open Bluetooth settings on Windows, Android or an Apple device and add the current mode's controller. These are compatibility targets, not an exhaustive verified platform list.
2. An unbonded R40 identity remains open for pairing. A bonded identity alternates old-host reconnection with discovery windows; visibility need not be constant.
3. To replace a host or recover mismatched keys, hold **GL + GR + ZL + ZR + Plus + Minus for about 2 seconds**. This clears the current identity's bonds and reopens pairing, without clearing other identities or forcing Edge mode.
4. Also remove the old controller entry on the target OS, then scan again. Ensure another board is not advertising the same name.
5. Normal pairing should not require a PIN. R40's legacy fallback PIN is `0000`; a continuing PIN request/no-response failure can indicate stale bonds or security negotiation problems. Reopen pairing and collect logs instead of guessing passwords repeatedly.
6. Chords require live Pro2 input at Pico. If USB input has stopped, use a responsive CDC diagnostic connection or physical BOOTSEL recovery instead.

Platform steps: on Windows use Bluetooth & devices -> Add device -> Bluetooth; on Android pair in Bluetooth settings, then test a controller-aware game. On macOS use Bluetooth in System Settings; on iPhone/iPad use Settings -> Bluetooth. Edge support depends on OS/app versions. For Switch 1, select the Y/Pro identity, open Controllers -> Change Grip/Order, then reopen the current identity's pairing. Discovery as Pro Controller on Windows is not Switch acceptance. Pico wireless operation does not require USBIP or VIIPER on the target PC.

Rumble feedback depends on the Pico-to-Pro2 output path; its absence alone does not prove a chord failed. Pro2's yellow charging/activity LED is not a connection indicator. Use `USB_HEALTH` report counters and `wireless status` when diagnosing.

**Physical Xbox consoles and physical PS5 console authentication/initial USB pairing are not supported.** The names describe compatible identities, not official certification. Pico does not emulate a DualSense audio device: there is no audio-stream HD haptic, headphone or microphone forwarding.

### Optional R55 Recovery UF2 - Not Embedded

Asset: `XinHeLianSheng-Pico2W-Pro2-Wireless-Bridge-test-r55.uf2`.

On first use of the R55 recovery marker it selects Edge and clears old Edge bonds. Later boots preserve newly paired keys and the selected mode. Reflashing the same R55 does not necessarily repeat recovery because the marker may remain in flash.

Enter BOOTSEL manually and copy this UF2 to the `RP2350` disk. Pressing the three-in-one flasher's embedded firmware button afterward **flashes R40 again**. Do not interpret its R40 verification label as an R55 check.

R55 limits full Sony Bluetooth reports to approximately 66 Hz. That is a transmit ceiling, not measured fresh-input frequency. Faster USB input does not prove 250 Hz Bluetooth output. Build and existing source-contract checks passed, but this publication does not include successful comprehensive pairing, sustained input, rumble or IMU acceptance for R55. It is not a guaranteed connection fix.

## Route 2: ESP32-S3 Receiver

### First Setup and Everyday Use

1. Use a board matching the existing ESP32-S3 firmware configuration; arbitrary ESP boards are not automatically compatible. COM is for flashing/control, while native USB / OTG exposes the emulated controller.
2. Initially connect COM to the PC and native USB to the target PC for identity verification. Select the CH343/WCH port, not a Bluetooth virtual COM port.
3. Launch the ESP backend, choose the mode and flash its firmware. Changing ESP mode reflashes firmware; it is not Pico's button-chord switching.
4. Wait until flashing completes. Check the USB identity and replug native USB / OTG if needed; do not unplug COM during flashing.
5. Wake Pro2 and press its pairing button. Use first connection, or BLE scan then connect. Avoid a competing Pro2 connection to Windows, a console or another bridge.
6. Confirm live buttons/sticks before playing. Once the address is saved, everyday use normally needs only native USB for power/output and firmware BLE reconnection; COM is for management and diagnostics.

| Firmware identity | VID:PID | Notes |
| --- | --- | --- |
| PS5 / DualSense | 054C:0CE6 | Compatible input and gyro; the HD audio-haptic route requires supporting game output and coexists with ordinary rumble |
| PS5 Edge | 054C:0DF2 | Edge/back-button identity; motion/haptic support depends on the profile and host, not guaranteed native support in every PS5 game |
| Nintendo / Pro2 | 057E:2069 | Steam-oriented Pro2 layout; some games need Steam Input, not a Switch 2 authentication promise |
| Xbox / XInput | 045E:028E | Broad PC input and ordinary rumble; XInput provides no gyro |
| macOS standard HID candidate | CAFE:4037 | Additional HID input target, not full feature parity on every platform |

HD haptic requires a game actually delivering haptic audio to the appropriate endpoint. It does not mean all system audio becomes vibration. Audio endpoints, game support and Steam Input settings affect the result. Follow the Xbox page's allowed rear-button targets and single/turbo settings; it is not an arbitrary scripting engine.

### Separate Switch 1 Experiment

The ESP card has a separate **V5.9.18 R4** entry, kept apart from V5.9.19. It exposes the first-generation Pro USB identity `057E:2009`.

Keep COM connected to the management PC and connect native USB to Switch 1. Enable the console's Pro Controller wired communication setting, use Change Grip/Order if needed, then pair the real Pro2 to ESP. Connection and rumble remain experimental; eliminating every reconnection or firmware crash is not claimed.

### Flashing Failures

`Wrong boot mode` means ROM download mode was not entered; it does not establish hardware damage. Hold BOOT, press/release RESET / EN, and retry as instructed. Access denied can mean another application owns COM. For driver/kernel hangs, stop repeatedly opening the port, follow diagnostics for replug/restart and a compatible driver. Do not blindly rotate drivers, disable signature protection or force-kill flashing.

Real ESP BLE input frequency depends on Pro2, radio conditions and negotiated parameters. Approximately 133 Hz is a previous-condition reference, not a universal guarantee or proof that each virtual USB update is a new sample.

## Route 3: Windows BLE / VIIPER

### First Installation

No bridge board is needed, but **a system-level Windows USBIP driver is required**. The VIIPER runtime and usbip-win2 installer are bundled. VIIPER needs no separate installation; copying the EXE does not install the kernel driver.

1. Use Windows 10/11 x64 with a BLE GATT-capable adapter and Bluetooth enabled. Unplug Pro2's direct PC USB for wireless testing.
2. Launch the software backend. If environment checks request it, choose install/repair usbip-win2 and accept UAC/the installer.
3. Restart Windows if requested, then reopen the application. An installed executable path does not prove that the driver has loaded.
4. USBIP lookup checks the registry, PATH and common directories. An unregistered portable copy outside those locations may not be found; prefer the bundled official installer rather than repeatedly installing VIIPER.
5. Choose a mode and Enter Game. First confirm the virtual USB controller is created, then wait for/use Pro2 automatic connection.
6. The application directly reads Pro2 as a BLE central. **Do not require prior HID pairing through Windows Add Bluetooth Device.** Wake Pro2 and enter pairing mode; use manual scan/connect if automatic discovery fails.

### Modes and Four Independent Slots

| Mode | Purpose and limits |
| --- | --- |
| PS5 / DualSense | Compatible input, gyro and ordinary rumble; HD haptic requires a supporting game and matching audio stream |
| PS5 Edge | Back-button identity, gyro and ordinary rumble; this package still reports Edge HD as `blocked_by_viiper_feedback_contract`, not working HD support |
| Pro2 / Nintendo | Steam compatibility, gyro and supported Nintendo feedback; some games require Steam Input |
| Xbox / XInput | Broad compatibility, no gyro; the Xbox page supports GL/GR single/turbo mappings to its listed buttons |

The backend supports up to four slots with separate real Pro2 addresses, virtual instances, input and feedback routing. Do not bind one physical controller to multiple slots. Four physical controllers were not comprehensively tested in this publication; adapter resources and radio bandwidth affect concurrency.

For unexpected Steam names/devices, check other backends, real wired controllers, ESP boards and old virtual instances. Confirm VID/PID, layout and slot logs rather than judging identity only by its display name.

BLE sampling and virtual USB refresh are different measurements. The normal route follows source input timing rather than forcing repeated 250 Hz frames as evidence of low latency. Previous Windows observations were commonly 60-80 Hz, not a fixed limit for every Windows 11 system; use current telemetry.

Use the backend's reconnect, tray and last-mode settings as displayed. Explicitly turn off automatic connection or stop the session to suspend it. Test either game-native input or Steam Input deliberately; double mapping may alter controls, rumble and motion behavior.

### Stick Calibration

V6.2.32 R2 provides center and full-travel calibration. For center calibration, release both sticks and stay still for about 2 seconds. For full travel, during approximately 8 seconds cover up/down/left/right and make multiple outer-edge circles with both sticks, then wait for completion. Results are saved per controller address. Calibration corrects center/travel differences; it must not hide malformed packets, wrong layouts or damaged hardware.

## Known Issues at Publication

- Pico can be discoverable but fail to connect after mode changes or on a new host, sometimes recovering only after a long wait. Power-cycle/long-idle reconnection can also fail.
- Pico PS5/Edge may not respond to Steam's rumble test, or game rumble may stay on. R40 implements a stop state machine, but observed failures are not declared fully fixed. End the game session and disconnect bridge power if required to stop abnormal rumble.
- Pico and ESP Switch 1 experiments can fail after discovery or reconnect unexpectedly; rumble needs dedicated acceptance.
- Motion conversion exists, but equivalence to wired Pro2 in every game is not claimed. A game accepting Edge buttons but ignoring its gyro may support standard DualSense only.
- USBIP permissions/restart state, adapter compatibility and radio interference can cause failures. Logs cannot always distinguish battery depletion, manual shutdown and abnormal loss.
- Commercial code signing is not promised for these test EXEs. Verify SHA256 and do not disable system protections to run an untrusted download.

## Logs and Feedback

| Application | Default log directory |
| --- | --- |
| Control center | `%LOCALAPPDATA%\XinHeLianSheng\ControlCenter\logs` |
| Pico flasher | `%LOCALAPPDATA%\XinHeLianSheng\Pico2WFlasher\logs` |
| ESP Manager | `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\logs` |
| Windows BLE / VIIPER | `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\v6_logs` |

Backend cache: `%LOCALAPPDATA%\XinHeLianSheng\ControlCenter\backends`. Do not delete it while a backend runs or flashes. Some applications remove previous-session logs on startup; export them before reopening. A standalone Pico does not automatically create firmware serial logs on an unattached PC.

For reports include center/backend/firmware versions, mode, target OS/device, USB/battery arrangement, failure time, whether it followed pairing/mode change/power cycle/idle, and Manager/VIIPER logs. Redact personal paths and Bluetooth addresses before public upload.

`PACKAGE_VERIFY.txt` verifies extraction and SHA256 of all four embedded backends. **It is not end-to-end hardware stability certification.** Communication implementations were not further changed for this publication.

## Open Source and Licenses

Project code retains Apache-2.0. Third-party terms remain separate: aggregation does not relicense everything as Apache-2.0. The documentation archive includes VIIPER, usbip-win2, Pico SDK / BTstack / CYW43 / TinyUSB / PIO USB notices, and modified VIIPER source is attached separately. Controller identities, trademarks and character artwork do not imply Nintendo, Sony or Microsoft authorization/certification.
