# ESP32-S3 Bridge Firmware

This firmware is the final V5.9 Pro2 / Nintendo and Xbox / XInput bridge for ESP32-S3.

## Target

- ESP32-S3 N16R8
- 16 MB flash
- 8 MB Octal PSRAM
- Native ESP32-S3 USB / OTG for the host gamepad interface
- CH343P / WCH USB serial for flashing, logs, and Manager control

## Modes

The same firmware source builds two packaged profiles:

| Profile | Default USB mode |
| --- | --- |
| `pro2_bridge_v5_5` | Pro2 / Nintendo HID |
| `xinput_bridge_v5_8` | Xbox / XInput |

The profile names are historical compatibility IDs used by the Windows Manager. The final package version is V5.9.

## Pro2 / Nintendo Mode

- USB VID/PID: `057E:2069`
- Product string: `Nintendo Switch Pro Controller`
- HID input report ID: `0x05`
- 64-byte report shape for Steam / SDL Switch Pro handling
- Raw HID output report `0x02` is the authoritative rumble path
- BOS / Microsoft OS 2.0 descriptor is exposed for MI_01 WinUSB binding

## Xbox / XInput Mode

- USB VID/PID: `045E:028E`
- XInput-style report path
- Left/right stick Y axes use host-expected polarity
- Ordinary weak/strong rumble is converted to Pro2 BLE output with strength preservation and dynamic frequency shaping

## BLE

The firmware acts as a BLE central for the real controller:

- scans and connects from Manager commands
- persists BLE auto-connect settings
- retries reconnect in the background after disconnect or failed connect
- keeps the USB gamepad interface alive while BLE is reconnecting

## PSRAM Defaults

The final defaults favor reliable boot on N16R8 / AP 8 MB Octal PSRAM boards:

```text
CONFIG_SPIRAM_USE_MEMMAP=y
# CONFIG_SPIRAM_USE_MALLOC is not set
# CONFIG_SPIRAM_MEMTEST is not set
```

## Build

From the repository root:

```powershell
.\tools\setup_dev_environment.ps1
.\tools\esp32s3\build.ps1
```

For the XInput profile:

```powershell
.\tools\esp32s3\build.ps1 -BuildDir work\b\xinput -DeviceDefaultMode XINPUT_EXPERIMENT_MODE
```
