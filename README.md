# 新和联胜三合一 / XinHeLianSheng Three-In-One

> **项目已停止更新。** 不再计划新的功能、优化、修复版本或 Release。现有源码、程序、历史下载及开源协议保留，供使用、学习和自行 fork；已知问题不承诺后续修复。
>
> **This project is discontinued.** No further features, optimizations, fixes or releases are planned. Existing source, binaries, historical downloads and licenses remain available for use, study and independent forks. Future fixes for known issues are not promised.

把真实 **Switch 2 Pro / Pro2 手柄**用于更多设备与游戏：一个 Windows 控制中心，三个独立入口——**Pico 2 W 背挂、ESP32-S3 接收板、Windows BLE / VIIPER 无开发板版**。

Use a real **Switch 2 Pro / Pro2 controller** with more devices and games through one Windows control center and three separate routes: **Pico 2 W backpack, ESP32-S3 receiver, and Windows BLE / VIIPER without a bridge board**.

> **目前为三合一公开测试版。** 连接、断电回连、震动、IMU 和驱动兼容仍可能有问题。Latest 表示当前下载入口，**不表示已完成全面稳定性验收**。旧版本继续保留。
>
> **This is a public test build.** Pairing, power-cycle reconnection, rumble, IMU and driver issues may remain. Latest identifies the current download entry, **not comprehensive stability acceptance**. Older releases remain available.

## 下载置顶 / Downloads First

| 下载入口 / Download | 内容 / Contents |
| --- | --- |
| [**三合一当前 Release / Current Three-In-One Release**](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/tag/three-in-one-r40-test) | EXE、完整双语指南、校验值、许可证、可选 R55 / EXE, bilingual guides, checksums, licenses, optional R55 |
| [**直接下载三合一 EXE / Download the EXE**](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/three-in-one-r40-test/XinHeLianSheng-Three-In-One-R40-test.exe) | Windows 10/11 x64，约 514 MiB，界面中文 / approximately 514 MiB, Chinese UI |
| [**完整中文操作说明**](docs/THREE_IN_ONE_R40_TEST_ZH.md) · [**Full English Guide**](docs/THREE_IN_ONE_R40_TEST_EN.md) | 按路线和模式说明刷写、配对、使用与故障 / Route-by-route and mode-by-mode setup and troubleshooting |
| [**全部 Release / All Releases**](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases) | 三合一及独立历史版本 / Integrated and historical standalone builds |
| [Windows 独立测试包 / Standalone Windows Test: V6.2.32 r2](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/tag/v6.2.32-test-r2) | 不需要开发板 / No bridge board required |
| [ESP 独立历史包 / Historical Standalone ESP: 5.9.20](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/tag/5.9.20) | 单独的历史 Release，**不等于三合一内置版本** / A separate historical release, **not the embedded version** |
| [历史双版本 / Historical Dual Edition: V6.2.25 + V5.9.13](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/tag/v6.2.25-v5.9.13-finale) | 旧版回退与开发记录 / Legacy fallback and development reference |

**首次下载建议使用三合一入口，再选择适合自己的路线；只需使用其中一条，不必安装或连接三套硬件。**

**Start with the three-in-one application, then choose one route. You do not need all three setups.**

## 包里究竟是什么 / Exactly What Is Bundled

| 入口 / Route | 内置后端 / Embedded backend | 数据路径 / Data path |
| --- | --- | --- |
| Pico 2 W 背挂 / Backpack | **R40 候选刷机工具 / Candidate flasher** | Pro2 有线 USB → Pico USB Host → 目标设备蓝牙 / target Bluetooth |
| ESP32-S3 接收板 / Receiver | **V5.9.19 候选 / Candidate** | Pro2 BLE → ESP32-S3 → 目标设备 USB / target USB |
| ESP Switch 1 独立实验入口 / Separate experiment | **V5.9.18 Switch 1 R4** | Pro2 BLE → ESP32-S3 → Switch 1 USB |
| Windows BLE / VIIPER | **V6.2.32 Test R2** | Pro2 BLE → Windows → VIIPER / USBIP 虚拟 USB / virtual USB |

三合一整合的是**入口与分发**，不是一个通用固件。控制中心启动独立后端；首次启动需要解包、计算 SHA256。它只管理自己启动的后端，不能阻止外部旧程序抢占串口或制造额外虚拟手柄。

The integration combines **launching and distribution**, not a universal firmware. Each backend remains separate. Initial launch extracts and hashes it. The center manages only backends it launches; manually opened older applications can still occupy COM or create extra controllers.

**另附的 Pico R55 UF2 不在三合一内部。** R55 是可选的配对恢复实验固件，首次恢复选择 Edge 并清除旧 Edge 绑定，完整 Sony 蓝牙报告限速约 66 Hz。手动刷 R55 后，再点击内置刷写会**刷回 R40**；它没有全面验收通过，不能当作已解决所有问题的升级。

**The optional Pico R55 UF2 is not embedded.** It is a separate pairing-recovery experiment: first recovery selects Edge and clears old Edge bonds; full Sony Bluetooth reports are limited to approximately 66 Hz. Flashing the embedded firmware afterward **restores R40**. R55 is not a comprehensively accepted fix.

## 模式与能力 / Modes and Capabilities

| 模式 / Mode | 用途 / Purpose | 注意 / Caveat |
| --- | --- | --- |
| PS5 / DualSense | PS5 兼容身份、陀螺仪、普通震动 / Compatible identity, gyro, ordinary rumble | ESP / Windows HD 依赖支持游戏与实际触觉流；Pico 不提供音频 HD / ESP and Windows HD need supported haptic output; Pico has no audio HD |
| PS5 Edge | Edge / 背键身份、陀螺仪、普通震动 / Edge/back-button identity, gyro, ordinary rumble | 游戏可能只识别标准 DualSense；本包 Windows Edge HD 仍被 feedback contract 阻挡 / Game support varies; bundled Windows Edge HD remains blocked |
| Pro2 / Nintendo | ESP / Windows 的 Nintendo 布局与 Steam Input 路线 / Nintendo layout and Steam Input on ESP/Windows | 不是 Switch 2 主机认证；Pico 返回原生 Pro2 建议物理拔 Type-C / No Switch 2 authentication claim; unplug Type-C for native Pico/Pro2 use |
| Xbox / XInput 或 BLE | 广泛按键、摇杆与普通震动兼容 / Broad buttons, sticks and ordinary rumble | **无陀螺仪**；Pico 使用 Xbox BLE，不支持 Xbox Wireless 主机 / **No gyro**; Pico Xbox BLE is not Xbox console Wireless |
| Switch 1 Pro | Pico 蓝牙或 ESP 独立固件连接一代 Switch / Pico Bluetooth or separate ESP firmware for Switch 1 | 实验功能，配对与震动尚需验收 / Experimental pairing and rumble |

**不提供实体 PS5 / Xbox 主机认证。** 名称和 USB / 蓝牙身份仅描述兼容行为。Pico 不转发耳机、麦克风或 DualSense 触觉音频。Windows 版 Edge HD 状态仍为 `blocked_by_viiper_feedback_contract`，不要理解成与标准 PS5 模式相同。

**Physical PS5 / Xbox console authentication is not provided.** Names and identities describe compatibility only. Pico does not forward headphone, microphone or DualSense haptic audio. Windows Edge HD remains `blocked_by_viiper_feedback_contract`, unlike the standard PS5 route.

## Pico：刷机与无线使用 / Flash and Play

中文步骤：

1. 断开 Pro2、关闭背挂电源，用数据线把 Pico 自身 micro-USB 接电脑，启动 Pico 入口。
2. 自动 BOOTSEL 失败时，按住 BOOTSEL 重新插入电脑 USB；必须识别到 **RP2350**，不是 Pico 1 的 RPI-RP2。
3. 点击“开始刷入内置固件”，等 `firmware_revision=r40` 与 `runtime_ready=true` 验证，不要仅凭磁盘消失判断成功。
4. 恢复已验证的 USB Host 接线与稳定 **5 V** 供电，把 Pro2 接 Pico 的 Host 口。Pico 自身 USB 是刷写 / 诊断口，不是有线 DualSense 输出口。
5. 真实 Pro2 输入正常时，长按 **GL+GR+ZL+ZR**，加 **A=PS5、B=Edge、X=Xbox、Y=Switch 1 Pro**，保持约 **1.5 秒**切换。
6. 换设备或回连失败时，长按 **GL+GR+ZL+ZR+Plus+Minus 约 2 秒**，清除当前模式绑定并重新配对；同时删除目标系统旧记录。此动作不会清除所有模式，也不会强制切 Edge。
7. Windows / Android / macOS / iPhone 在蓝牙设置中添加当前模式名称。Switch 1 切 Pro 模式后进入“控制器 → 更改握法 / 顺序”。系统和游戏支持情况仍需实际验证。

English steps:

1. Disconnect Pro2 and turn off backpack power. Connect Pico's own micro-USB to the PC using a data cable and launch the Pico backend.
2. If automatic BOOTSEL fails, hold BOOTSEL while reconnecting PC USB. Require **RP2350**, not Pico 1 / RPI-RP2.
3. Flash the embedded firmware and wait for `firmware_revision=r40` and `runtime_ready=true`; disk disappearance alone is insufficient.
4. Restore verified Host wiring and stable **5 V** power, then connect Pro2 to Pico's Host port. Pico's own USB is for flashing/diagnostics, not wired DualSense output.
5. With live input, hold **GL+GR+ZL+ZR** with **A=PS5, B=Edge, X=Xbox, Y=Switch 1 Pro** for approximately **1.5 seconds**.
6. To change hosts/recover pairing, hold **GL+GR+ZL+ZR+Plus+Minus for ~2 seconds** and remove the target OS's old entry. This clears only the current identity, not all modes, and does not force Edge.
7. Add the current identity in Windows/Android/macOS/iPhone Bluetooth settings. For Switch 1 Pro use Controllers -> Change Grip/Order. OS and game compatibility require verification.

不要把裸 3.7 V 电池当作 USB 5 V，也不要在电源接法不明时叠加多个电源。已保存模式会优先于默认 Edge。默认免 PIN 配对，R40 的旧式兼容 PIN 是 `0000`；反复要 PIN / 无响应可能是密钥或安全协商问题，不应只靠猜密码处理。静置黄灯熄灭不是断联证明。

Do not use a bare 3.7 V battery as USB 5 V or combine unknown supplies. Saved modes take precedence over default Edge. Normal pairing is PIN-free; R40's legacy fallback PIN is `0000`. Repeated PIN/no-response failures may involve keys/security negotiation, not a password to guess. An idle yellow LED turning off does not prove disconnection.

## ESP：烧录与连接 / Flash and Connect

中文步骤：

1. 使用适配项目固件的 ESP32-S3 板；既有路线主要面向 N16R8、16 MB Flash、8 MB PSRAM、CH343 控制口及原生 USB / OTG。
2. 首次将 COM 控制口和原生 USB 接电脑；选 CH343 / WCH 串口，不要选蓝牙虚拟 COM。
3. 点击目标模式烧录，等完成后检查 USB 身份；必要时重插原生 USB，刷写中不要拔 COM。
4. 唤醒 Pro2、按其配对键，用“首次连接”或 BLE 扫描后手动连接，确认实时输入再进游戏。
5. 配对保存后，日常通常只接原生 USB；COM 用于管理。ESP 切模式需要重新烧录。
6. Switch 1 使用独立 R4 实验入口，原生 USB 接主机、COM 留电脑；主机启用 Pro 手柄有线通信。

English steps:

1. Use a matching ESP32-S3 board; the established target is N16R8, 16 MB Flash, 8 MB PSRAM, CH343 control USB and native USB/OTG.
2. Initially connect both ports to the PC. Choose CH343/WCH COM, not Bluetooth virtual COM.
3. Flash the selected mode, wait for completion and check USB identity. Replug native USB if necessary; never unplug COM during flashing.
4. Wake Pro2, press its pairing button and use first connection or BLE scan/connect. Confirm live input before playing.
5. Once pairing is saved, everyday use normally needs native USB only. COM is for management. ESP mode changes require reflashing.
6. Use the separate R4 experiment for Switch 1, native USB to the console and COM to the PC; enable Pro Controller wired communication.

`Wrong boot mode` 是下载模式未进入，不是开发板损坏的结论；拒绝访问先排查串口占用。驱动内核级挂起按诊断处理，不要随机换驱动、关闭签名保护或在刷写中强杀。

`Wrong boot mode` indicates download mode was not entered, not proven board damage. For access denied, check COM ownership. Follow diagnostics for kernel/driver hangs instead of blindly rotating drivers, disabling signatures or force-killing flashing.

## Windows：安装与直连 / Install and Connect

中文步骤：

1. Windows 10/11 x64，打开蓝牙，使用支持 BLE GATT 的适配器；无线测试拔掉 Pro2 的有线电脑连接。
2. 启动纯软件入口，按提示“安装 / 修复 usbip-win2”，确认 UAC；要求重启时先重启再使用。
3. **VIIPER 运行程序和 USBIP 安装器已内置，但 USBIP 内核驱动仍要系统安装。** 程序会查注册表、PATH、常见目录；非常规便携复制可能找不到，优先用正式安装器。
4. 选择 PS5 / Edge / Pro2 / Xbox，点击“进入游戏”，确认虚拟 USB 成功，再唤醒真实 Pro2 等待自动 BLE 连接。**不需要先在 Windows 添加设备里完成 Pro2 HID 配对。**
5. 自动连接失败再用手动 BLE 扫描 / 连接。最多四槽，每槽对应不同真实地址与虚拟实例；四实体并发未在本次全面验收。
6. Xbox 页面按提示设置 GL / GR 的规定键位单发 / 连发。摇杆零位校准约 2 秒，完整行程约 8 秒，需覆盖两根摇杆四方向与外圈，并等待完成提示。

English steps:

1. Use Windows 10/11 x64, Bluetooth enabled and a BLE GATT-capable adapter. Unplug Pro2's direct PC USB for wireless tests.
2. Launch the software route, install/repair usbip-win2 if requested, accept UAC and restart when instructed.
3. **VIIPER and the USBIP installer are bundled, but the USBIP kernel driver still needs system installation.** Lookup checks registry, PATH and common directories; unusual portable copies may be missed. Prefer the official installer.
4. Choose PS5/Edge/Pro2/Xbox and Enter Game. Confirm virtual USB creation, then wake Pro2 for direct application BLE connection. **Prior Windows Pro2 HID pairing is not required.**
5. Use manual BLE scan/connect if automatic discovery fails. Up to four independent addresses/instances are supported; four physical controllers were not fully accepted in this publication.
6. Follow the Xbox page for GL/GR mappings to listed single/turbo buttons. Center calibration is approximately 2 seconds; full travel approximately 8 seconds, covering both sticks' directions and outer circles, followed by completion.

真实 BLE 采样与虚拟 USB 刷新不是同一指标；本包正常路线按输入源推送，不用强制重复 250 Hz 证明跟手。此前 Windows 常见约 60–80 Hz、ESP 约 133 Hz，是实测条件参考，不是所有设备上限或保证。

Real BLE sampling and virtual USB refresh are different metrics. The normal route follows source input rather than forcing repeated 250 Hz frames. Previous Windows 60–80 Hz / ESP ~133 Hz observations are condition-specific references, not universal ceilings or guarantees.

## 已知问题与日志 / Known Issues and Logs

- Pico 可能可发现但无法连接，模式切换或断电 / 长待机后回连仍有失败。 / Pico may be discoverable but fail to connect, including after mode changes, power cycles or long idle.
- Pico PS5 / Edge 可能没有测试震动，或游戏触发后不停振；不能因为已实现停振逻辑就宣称彻底解决。 / Pico PS5/Edge may lack test rumble or retain game rumble; a stop implementation does not prove every failure fixed.
- Switch 1 配对 / 震动仍是实验；IMU 在不同游戏和路线上不保证等同有线 Pro2。 / Switch 1 pairing/rumble is experimental; IMU equivalence to wired Pro2 is not guaranteed across games/routes.
- 蓝牙适配器、干扰、驱动权限、安装后未重启都可能造成异常；日志未必能分辨没电与主动关机。 / Adapter/radio/driver/restart issues can cause failures; logs may not distinguish low battery from shutdown.

| 程序 / Application | 默认日志 / Default logs |
| --- | --- |
| Control center / 控制中心 | `%LOCALAPPDATA%\XinHeLianSheng\ControlCenter\logs` |
| Pico flasher / 刷机工具 | `%LOCALAPPDATA%\XinHeLianSheng\Pico2WFlasher\logs` |
| ESP Manager | `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\logs` |
| Windows BLE / VIIPER | `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\v6_logs` |

部分程序启动会清除旧会话日志，请在重启前导出。反馈写明后端 / 固件版本、模式、设备、供电、失败时间、是否切模式或静置后出现，公开日志前遮盖个人路径和蓝牙地址。

Some applications delete prior-session logs on startup; export before reopening. Include backend/firmware version, mode, target, power setup, failure time and whether it followed switching/idle. Redact private paths and Bluetooth addresses.

## 校验、源码与协议 / Verification, Source and License

Release 的 `SHA256SUMS.txt` 可核对下载；主 EXE 原包 SHA256：

```text
d32df651d33e6964c4fae41b07823bfddc5fb3809d176ec367410da27def3202
```

`PACKAGE_VERIFY.txt` 证明四个内置后端解包与哈希通过，**不证明硬件整机稳定性**。本次发布未继续修改通信代码，未覆盖旧版。

Release checksums verify downloads. `PACKAGE_VERIFY.txt` verifies extraction/hashes of four embedded backends, **not end-to-end hardware stability**. Publication does not further modify communication code or overwrite legacy builds.

**整体源码已整理到 [source/final-three-in-one](source/final-three-in-one)，构建与版本对应详见 [源码说明](source/final-three-in-one/README.md)。** 包含控制中心、Pico 刷机工具与现存 R55 固件、ESP V5.9.19 固件 / Manager、独立 Switch 1 R4 实验、Windows V6.2.32 R2、修改版 VIIPER、工具和测试。固定源码检查点：[final-source-20260916](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/tree/final-source-20260916/source/final-three-in-one)。

**Complete maintained-route source is collected in [source/final-three-in-one](source/final-three-in-one); see its README for building and version provenance.** It includes the center, Pico flasher/current R55 firmware, ESP V5.9.19 firmware/Manager, separate Switch 1 R4 experiment, Windows V6.2.32 R2, modified VIIPER, tooling and tests. The fixed source checkpoint is `final-source-20260916`.

**源码封存不改变已发布 EXE。** Pico 固件源码是现存 R55，不是历史 R40 的逐字快照；R40 UF2 保留供 R40 刷机工具编译使用，不能仅指定 `r40` 编译标签就把 R55 源码还原成旧固件。其他源码也不承诺与已发布二进制逐字节重现。SDK、编译器、系统驱动安装、日志、凭据与缓存不属于项目源码。

**Source publication does not change the released EXE.** Pico firmware source is the available R55, not an exact historical R40 snapshot. R40 UF2 is retained for rebuilding the R40 flasher; setting a revision string to `r40` cannot restore old firmware behavior. Byte-identical reproduction is not promised for other binaries either. SDKs, compilers, installed drivers, logs, credentials and caches are not project source.

历史源码路线仍保留在 `codex/v6.2.25-finale-dual-release` 和 `codex/v5.9.13-finale-dual-release`。Release 原来附带的 [VIIPER Bundled Source](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/three-in-one-r40-test/XinHeLianSheng-VIIPER-Bundled-Source.zip) 也继续保留。GitHub 默认分支旧目录属于历史代码，以 `source/final-three-in-one` 为本次封存入口。

Historical source branches and the previously attached VIIPER archive remain available. Legacy directories on the default branch are historical references; use `source/final-three-in-one` as the final publication entry.

项目自有代码保留 [Apache-2.0](LICENSE)，第三方组件继续适用各自协议，详见 [分发说明 / Distribution Notice](docs/THREE_IN_ONE_DISTRIBUTION_NOTICE.md) 和 [ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md)。本项目独立于 Nintendo、Sony、Microsoft、Valve 等厂商；模式、商标与素材不代表官方授权或认证。

Project code retains [Apache-2.0](LICENSE); third-party terms remain separate. See the distribution notice and acknowledgements. This independent project is not affiliated with Nintendo, Sony, Microsoft or Valve. Identities, trademarks and artwork do not imply official endorsement/certification.
