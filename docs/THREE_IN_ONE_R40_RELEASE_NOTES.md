# 新和联胜三合一测试版 / Three-In-One Public Test

**本版是三合一公开测试包，不是全面验收后的稳定版。仍可能有配对、回连、震动、IMU 或驱动兼容问题。旧版本保留；本次不再继续优化通信代码。**

**当前下载入口 / Current download:** [三合一 EXE / Three-In-One EXE](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/three-in-one-r40-test/XinHeLianSheng-Three-In-One-R40-test.exe) · [首页 README / Project README](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge#readme) · [完整中文指南](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/main/docs/THREE_IN_ONE_R40_TEST_ZH.md) · [Full English Guide](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/main/docs/THREE_IN_ONE_R40_TEST_EN.md)

为方便查找，本版设为 GitHub Latest。**Latest 只表示当前下载入口，不代表稳定性认证；本版仍是测试版。** GitHub 不允许预发布同时标为 Latest，因此平台预发布勾选取消，但本页测试性质与已知问题不变。

This build is marked GitHub Latest for discoverability. **Latest identifies the current download entry, not stability certification; this remains a test build.** GitHub does not allow a prerelease to also be Latest, so the platform prerelease checkbox is cleared without changing the testing caveats.

统一入口涵盖 Pico 背挂、ESP32-S3 接收板和 Windows 纯 BLE 软件版。首次解包较慢，主 EXE 约 514 MiB。界面目前为中文，附件提供完整中英文操作说明。

## 内置版本 / Embedded Versions

| 入口 / Route | 内置版本 / Version | 路径 / Data path |
| --- | --- | --- |
| Pico 2 W | **R40** 候选 / candidate | Pro2 USB → Pico → Bluetooth |
| ESP32-S3 | V5.9.19 候选 / candidate | Pro2 BLE → ESP → USB |
| ESP Switch 1 实验 / experiment | V5.9.18 R4 | Pro2 BLE → ESP → Switch 1 USB |
| Windows BLE / VIIPER | V6.2.32 Test R2 | Pro2 BLE → Windows → virtual USB |

**Pico R55 UF2 只是另附的可选配对恢复实验固件，不在三合一里。** 手动 BOOTSEL 刷 R55 后，三合一 Pico 工具的“刷入内置固件”会刷回 R40。R55 首次恢复选择 Edge / 清除旧 Edge 绑定，完整 Sony 蓝牙报告限速约 66 Hz；不是已验收的稳定修复。

## 操作重点

- **Pico：** 按住 BOOTSEL 接电脑，识别 RP2350 后刷写；运行时 Pro2 接 Pico USB Host，并提供稳定 5 V。GL+GR+ZL+ZR 加 **A=PS5、B=Edge、X=Xbox、Y=Switch 1 Pro**，保持约 1.5 秒切模式；加 Plus+Minus 保持约 2 秒清除当前绑定并重新配对。
- **ESP：** COM 用于刷写与控制，原生 USB / OTG 用于手柄输出；选模式烧录后配对真实 Pro2。首次连接建议两口接电脑，配置完成后通常只需原生 USB。Switch 1 固件在独立实验入口。
- **Windows：** VIIPER 运行程序与 USBIP 安装器内置，但 USBIP 内核驱动仍需 UAC 安装，按提示重启。选择模式“进入游戏”，由程序直接 BLE 连接 Pro2，不需要先在 Windows HID 配对。后端支持四个独立槽位，四实体并发未在本轮验收。
- **边界：** Pico 没有音频 HD haptic / 耳机 / 麦克风。Windows 版 Edge HD 仍被 VIIPER feedback contract 阻挡。Xbox/XInput 没有陀螺仪。不支持实体 PS5 / Xbox 主机认证。
- **未解决问题：** Pico 可发现但连接失败、断电 / 长待机回连失败、PS5/Edge 无震动或不停振；Switch 1 实验连接 / 震动；不同游戏的 IMU 手感和 Edge 识别差异。请看完整说明，不能按所有功能均已完成理解。

## 下载与核验

- `XinHeLianSheng-Three-In-One-R40-test.exe`：现有“新和联胜三合一控制中心-R40-test.exe”原包，英文下载名，内容未改、未重编译。
- `THREE_IN_ONE_R40_TEST_ZH.md` / `THREE_IN_ONE_R40_TEST_EN.md`：完整中英文指南，按路线及模式写明步骤、细节、已知问题和日志位置。
- `XinHeLianSheng-Three-In-One-R40-Guides.zip`：指南及许可证合集。
- `XinHeLianSheng-VIIPER-Bundled-Source.zip`：所内置修改版 VIIPER 的源码、构建文件和许可证；不是 GitHub 自动生成的源码快照。
- `XinHeLianSheng-Pico2W-Pro2-Wireless-Bridge-test-r55.uf2`：可选实验资产。
- `PACKAGE_VERIFY.txt` / `SHA256SUMS.txt`：内置四后端解包校验与所有资产校验值。**包校验通过不等于整机稳定性验收通过。**

---

## English

**This is an experimental three-in-one launcher, not a comprehensively accepted stable release. Pairing, reconnection, rumble, motion and driver issues may remain. Previous releases are retained; no further communication-code optimization is included.**

It combines the Pico backpack, ESP32-S3 receiver and Windows BLE software entry points, while keeping their backends separate. The approximately 514 MiB EXE is the existing unmodified integration package. Allow time for extraction. The UI is Chinese; full Chinese and English guides are attached.

- **Pico:** enter BOOTSEL, flash on the RP2350 disk, then connect Pro2 to the powered USB Host port. Hold GL+GR+ZL+ZR with **A=PS5, B=Edge, X=Xbox, Y=Switch 1 Pro** for ~1.5 seconds. With Plus+Minus for ~2 seconds, clear the current mode's bond and reopen pairing.
- **ESP:** COM is for flashing/control; native USB/OTG is the controller output. Flash a selected mode and pair Pro2 to ESP. Both ports help initial setup; daily use normally needs native USB only. Switch 1 uses a separate experimental entry.
- **Windows:** VIIPER and the USBIP installer are bundled; the USBIP kernel driver still requires system installation/UAC and a restart if requested. Enter Game in the selected mode and connect Pro2 directly through application BLE, not prior Windows HID pairing. Four independent slots are implemented but four physical controllers were not accepted in this publication.
- **R55 is optional and separate, not embedded.** Manually flash its UF2 through BOOTSEL. Using the center's embedded firmware flash button afterward restores R40. R55's first recovery selects Edge/clears old Edge bonds, with full Sony Bluetooth reports limited to ~66 Hz. It is not a proven comprehensive stability fix.
- **Limits:** no Pico audio HD haptic/headphone/microphone; Windows Edge HD remains blocked by the VIIPER feedback contract; Xbox/XInput has no gyro; physical PS5/Xbox console authentication is not supported.
- **Known issues:** discoverable-but-unconnectable Pico identities, power-cycle/idle reconnection, missing or stuck PS5/Edge rumble, experimental Switch 1 pairing/rumble, and game-specific motion/Edge support. Consult the detailed guides before use.

Download the EXE, bilingual guides/license archive, optional R55 UF2, bundled VIIPER source and checksums below. `PACKAGE_VERIFY.txt` confirms four embedded payload hashes, not end-to-end hardware stability. Project Apache-2.0 and all third-party licenses are retained separately.
