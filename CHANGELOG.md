# Changelog

## PRO2 手柄助手 v2.4

- 自动连接发现可信 Pro2 广播后提前结束扫描；Windows 断线事件直接唤醒重连，保留输入中断保护、初始化和真实速率验收。
- 新增连接阶段耗时日志，区分发现、BLE 就绪、游戏输出就绪和断线后恢复。
- 合入 v2.1.1 的 PS5 原生识别修复，普通 DualSense 使用标准 USB 报告声明；《影之刃零》已实测不依赖 Steam Input 操作。
- 合入 v2.3 的游戏图标管理：当前适配《影之刃零》Steam 版，按实际位置显示 Pro2 图标，支持多 Steam 库、手选目录、版本校验、安装、旧补丁更新与恢复。
- 保留四模式、背键 / C 键、电量、震动与体感。提供标准版和兼容启动版，虚拟设备后端沿用 v2.1.1。

连接速度与真实 133Hz 仍取决于本机条件；图标按游戏适配，PS5 触觉为转换输出。[版本说明](docs/RELEASE_v2.4_ZH.md)

## V5.9.2 新和联胜版本

- Replaced the old DualSense card and image with the `新和联胜` PS5 mode.
- Reduced the user-facing release to three modes: 新和联胜 / PS5, Pro2 / Nintendo, and Xbox / XInput.
- Removed the Xbox Elite 2 / GIP bring-up profile from the release bundle.
- Added guided controller workflows for first pairing, reconnecting the saved controller, and replacing it.
- Added the common `ble forget` firmware command to clear the saved BLE target safely.
- Preserved automatic reconnect after controller sleep or transient disconnect.
- Updated all firmware and Manager version reporting to V5.9.2.
- Fixed Pro2 status compatibility: Manager now accepts both `live_*` and PS5 `input_*` freshness fields.
- Pro2 status reads and tuning no longer require a live controller; physical rumble tests still require fresh BLE input.
- `重连已配对` now rebuilds a connected link when input notifications are stale.
- Added per-flash CH343 driver/version logging and a preflight block for the
  reproduced Windows 26300 + WCH `2.1.2025.7` kernel-hang combination.
- Locked Pro2 and Xbox release profiles to their compiled USB identities so a
  stale NVS mode cannot override a successful mode flash.
- Fixed whole-chip erase on ESP32-S3 by using the esptool RAM stub.
- Completed a real hardware matrix covering Pro2 `057E:2069`, Xbox
  `045E:028E`, 新和联胜 `054C:0CE6`, whole-chip erase, and PS5 restore.

```text
release/v5.9/新和联胜版本-aio-v5.9.2.exe
sha256 5ca65c85970795fa66fdc88a9fbdac7de0af4daf5407eb2e625a8e710eef806d
```

## V5.9.1 Final

This is the final archived release of the ESP32-S3 Switch 2 / Pro2 bridge.

### Final Features

- Three USB output modes: Pro2 / Nintendo, Xbox / XInput, and DualSense-like.
- Windows all-in-one Manager with bundled firmware, esptool, BLE controls, USB checks, and test tools.
- Pro2 / Nintendo raw HID `0x02` rumble preservation.
- Xbox / XInput and DualSense-like ordinary rumble conversion with left/right strength preservation and dynamic Pro2 frequency shaping.
- Corrected Xbox and generic HID Y-axis polarity.
- Safer ESP32-S3 N16R8 PSRAM defaults: memmap enabled, malloc heap disabled, PSRAM memtest disabled.
- Microsoft OS descriptor / BOS exposure for Nintendo mode WinUSB interface binding.
- BLE background auto reconnect after controller sleep or disconnect, with wake-scan matching for the saved Pro2 address.
- Manager-side protections against repeated flash clicks, stale esptool processes, busy COM ports, and UI stalls when both USB cables are connected.
- DualSense host-trace diagnostics identified HID IN endpoint submission failure after a USB bus reset while BLE and PnP remained healthy.
- HID submission is now gated from TinyUSB's DCD bus-reset event until configuration and endpoint reopening complete.
- Immediate HID submit failures and asynchronous transfer failures are tracked separately.
- Persistent HID IN failure triggers one cooldown-protected USB re-enumeration instead of retrying every 4 ms forever.
- V5.9-only repository cleanup: old release artifacts, prototype scripts, temporary references, and outdated docs removed.

### Final Release Artifact

```text
release/v5.9/PRO2手柄无线接收器控制板-aio-v5.9.1.exe
sha256 04513580f0fce83ccde22fad7b2aed8731eaf37146b310f2ae13f88747de8edd
```

No further feature releases are planned.
