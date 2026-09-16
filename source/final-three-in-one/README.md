# 新和联胜最终源码 / Final Source Publication

**项目停止更新，不再计划新功能、优化、修复或新版本。此封存仅公开现有成果，不修改已发布程序，也不宣称已知问题全部解决。**

**The project is discontinued. No further features, optimizations, fixes or releases are planned. This source publication preserves existing work; it does not change the published application or declare all known issues fixed.**

## 目录与版本 / Layout and Versions

从本目录运行下面的构建命令。相对引用按原布局保留，Switch 1 实验有自己的独立根目录。

Run build commands from this directory. Original relative references are retained; the Switch 1 experiment has a separate project root.

| 路径 / Path | 内容 / Contents |
| --- | --- |
| `windows/unified_control_center` | 三合一控制中心，R40 发布的四个后端键及固定哈希 / Center with the four R40-publication backend keys/hashes |
| `windows/pico2w_flasher_app` | 内嵌 R40 UF2 的刷机工具 / Flasher embedding the retained R40 UF2 |
| `firmware/pico2w_pro2_wireless_bridge` | 最后现存的 R55 固件源码 / Latest available R55 firmware source |
| `windows/v55_manager_app` | ESP V5.9.19 Manager，含构建所需固件与工具资源 / ESP Manager with required embedded resources |
| `firmware/esp32s3_switch2_bridge` | ESP Nintendo / Xbox / macOS 等路线 / ESP Nintendo/Xbox/macOS routes |
| `firmware/esp32s3_dualsense_identity_experiment` | ESP DualSense / Edge，复用旁边桥接源码 / ESP DualSense/Edge with adjacent source reuse |
| `windows/dual_ns2pro_host` | ESP 原打包工具依赖的独立辅助程序 / Auxiliary program referenced by original ESP packaging |
| `windows/v60_viiper_app` | Windows BLE / VIIPER V6.2.32 Test R2 |
| `tools/viiper/haptic-src` | 随 Windows 后端使用的修改版 VIIPER GPL 源码 / Modified bundled VIIPER GPL source |
| `third_party/esp-viiper/haptic-src` | ESP 两个后端现存的 VIIPER GPL 源码，与 Windows 分支分开保留 / Available VIIPER GPL source for both ESP backends, separate from the Windows variant |
| `tools/viiper/haptic-v0.8.0` | Windows 工程内嵌的 VIIPER 运行资源 / VIIPER resource required by the Windows build |
| `tools/usbip-win2/v0.9.7.7` | 内嵌安装器及许可证，仍需用户系统安装 / Embedded installer/license, still requires system installation |
| `tools/tests` | 项目测试源码与脚本 / Tests and scripts |
| `tools/esp32s3` | ESP-IDF 环境、构建与管理脚本 / ESP-IDF environment/build/management scripts |
| `experiments/esp-switch1-r4` | 独立 Switch 1 R4 固件、Manager、工具与测试 / Separate R4 firmware, Manager, tools/tests |
| `release/pico2w-pro2-wireless-test-r40` | R40 刷机工具必须的已发布 UF2 / Published UF2 required by the R40 flasher |
| `release/pico2w-pro2-wireless-test-r55` | 可选 R55 UF2 与已知构建参数 / Optional R55 UF2/build parameters |
| `licenses` | 保留的项目及第三方许可证 / Retained project/third-party notices |
| `SOURCE_MANIFEST.json` | 文件大小、SHA256 与来源清单 / File sizes, hashes and origin inventory |

## 二进制与源码不是同一版本承诺 / Source versus Binary Provenance

三合一原 EXE 内置 Pico **R40**、ESP **V5.9.19**、ESP Switch 1 **R4**、Windows **V6.2.32 R2**。本次从各条现存开发目录收集源码，未回滚用户更改。现存 Pico 源码为 **R55**，没有可靠的逐字 R40 源码快照；保留 R40 UF2 不等于保留 R40 的历史源码。不要把 R55 指定为 `r40` 编译并宣称它就是原 R40。

The released center bundles Pico **R40**, ESP **V5.9.19**, ESP Switch 1 **R4**, and Windows **V6.2.32 R2**. Source is collected from the available development trees without reverting user changes. Available Pico source is **R55**; a reliable exact R40 source snapshot is not available. Retaining R40 UF2 is not retaining R40 source. Do not compile R55 with an `r40` label and claim historical equivalence.

这是现有路线的完整工程公开，不是全部历史实验与日志的转储，也不保证重编译哈希与过去 EXE 相同。已发布 EXE / 固件不替换。

This is publication of the existing route projects, not a dump of every historical experiment/log, and not a promise of byte-identical rebuilt EXEs. Published binaries are not replaced.

## Windows 工程 / Windows Applications

要求：Windows x64、.NET 8 SDK、可访问 NuGet。运行 USBIP 游戏会话仍需安装系统驱动，单纯构建不要求插上手柄。

Requirements: Windows x64, .NET 8 SDK and NuGet access. USBIP gameplay still needs its installed system driver; building does not require a controller.

```powershell
dotnet build windows/unified_control_center/XinHeLianShengControlCenter.csproj -c Debug
dotnet build windows/pico2w_flasher_app/Pico2WFlasherApp.csproj -c Debug
dotnet build windows/v55_manager_app/Y700Switch2V55Manager.csproj -c Debug
dotnet build windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Debug
dotnet run --project tools/tests/pico2w_flasher_test/Pico2WFlasherTest.csproj -c Debug
dotnet run --project tools/tests/v60_packet_mapper_test/V60PacketMapperTest.csproj -c Debug
```

Switch 1 Manager：

```powershell
dotnet build experiments/esp-switch1-r4/windows/v55_manager_app/Y700Switch2V55Manager.csproj -c Debug
```

固件资源、图标、VIIPER 与 USBIP 安装器是工程内嵌依赖，不要只复制 `.cs` 文件后声称工程完整。其许可证不因本项目 Apache-2.0 而改变。

Firmware resources, icons, VIIPER and the USBIP installer are required build inputs. Copying only `.cs` files is insufficient. Their licenses remain independent of this project's Apache-2.0.

## Pico 固件 / Pico Firmware

锁定依赖见 `firmware/pico2w_pro2_wireless_bridge/DEPENDENCIES.md`：Pico SDK 2.3.0、Arm GNU 14.2.Rel1、picotool 2.3.0、指定 Pico-PIO-USB 提交。SDK / 编译器不复制进源码库，需另外安装，SDK 的子模块也必须完整。

Dependency versions are recorded in `DEPENDENCIES.md`. Install the SDK/compiler/picotool separately, including the SDK submodules; development-machine toolchains are not vendored here.

原 `build_r6.ps1` 对 `.toolchain` 和 Visual Studio BuildTools 有目录要求，需按源码说明准备路径或改用 CMake 参数。R55 原构建的关键参数：

The original `build_r6.ps1` assumes particular `.toolchain` and Visual Studio BuildTools paths. Prepare them or use explicit CMake parameters. Key R55 parameters:

```text
PICO_BOARD=pico2_w
XHLS_FIRMWARE_REVISION=r55
XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ=66
XHLS_PAIRING_RECOVERY_TOKEN=55
XHLS_CLASSIC_BT_ONLY_DIAGNOSTIC=OFF
XHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC=OFF
```

普通协议与配对契约测试不需要 Pico SDK，但需要 C++17 编译器 / CMake：

Protocol and pairing-contract tests need CMake/a C++17 compiler, not Pico SDK:

```powershell
cmake -S tools/tests/pico2w_protocol_test -B build/protocol
cmake --build build/protocol --config Debug
cmake -S tools/tests/pico2w_r41_pairing_contract_test -B build/pairing
cmake --build build/pairing --config Debug
```

运行生成的测试 EXE，不能只把编译成功当作测试通过。频率参数是请求 / 上限，不证明真实蓝牙采样率。

Run the generated tests; compilation alone is not a test pass. Rate parameters are requests/ceilings, not evidence of actual BLE sampling.

## ESP 固件 / ESP Firmware

需要 ESP-IDF **5.4.x**（原环境 5.4.2），项目 `idf_component.yml` 与 SDK 默认配置保留。主机工具链和 `managed_components` 不作为项目源码上传，由 ESP-IDF 环境 / 组件管理器准备。

Use ESP-IDF **5.4.x** (original environment 5.4.2). Component manifests and default SDK configurations are retained; install toolchains/components through the SDK environment/component manager.

DualSense 工程通过 `components/pro2_bridge_reuse` 引用旁边的 `esp32s3_switch2_bridge/main`，两个目录必须同时保留。模式 profile 与打包流程见 `tools/package_v5_9_manager.ps1` 及 `tools/esp32s3`；独立 R4 从自己的实验根目录操作，不能混用 V5.9.19 的嵌入资源。

The DualSense project reuses adjacent bridge sources. Keep both firmware directories. See the original packaging/helpers for profiles. Run R4 from its independent experiment root rather than mixing V5.9.19 resources.

## 三合一打包 / Center Packaging

`dotnet build` 构建控制中心本体，不自动把四个后端附到单文件尾部。`BackendCatalog.cs` 固定了发布后端哈希，`PayloadBundle.cs` 定义追加包格式。原打包脚本一并保留，但其输入位置依赖原开发目录中的 `_worktrees` 与已发布后端，需要显式准备 / 调整路径，不能当成通用一键重建命令。

Building the center does not automatically append its four payloads. `BackendCatalog.cs` pins their hashes and `PayloadBundle.cs` defines the appended format. Original packaging scripts are retained as references; their `_worktrees`/binary input paths must be prepared or adapted, not assumed universally portable.

自行修改后端后必须更新实际后端文件及对应哈希，再按原格式封装。否则运行时校验应失败，不能绕过校验后把包当作同一发布版本。

For modified backends, update the actual payloads and corresponding hashes before assembling the bundle. An unchanged catalog should reject changed binaries; do not bypass verification and claim the original release.

## 许可证与排除项 / Licenses and Exclusions

项目 Apache-2.0、VIIPER GPL、USBIP / Pico 各第三方原文保留。素材、名称与主机身份不代表厂商授权。日志、真实蓝牙地址记录、凭据、临时缓存、`bin/obj`、固件 build 产物、SDK 和不相关 CAD 库不属于这次工程公开内容。

Original project/third-party licenses are retained. Artwork, names and identities do not imply manufacturer endorsement. Logs, real-device records, credentials, caches, bin/obj/build outputs, SDKs and unrelated CAD libraries are excluded.
