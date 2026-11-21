# PRO2 手柄助手 4.0 · 源码与构建

主程序：`windows/v60_viiper_app`。原生外发后端：`experiments/windows-wireless-pro1/native`。虚拟设备后端及其修改源码：`tools/viiper/haptic-src`。离线检查：`tools/tests/pure_software_core_test`。均沿用现有项目结构。

## 获取源码

4.0 的一代 Switch 外发组件位于 `experiments/windows-wireless-pro1/upstream-comparison`。它使用单独固定版本的 btkeyLib / BTstack。原有 Xbox、PS5 等输出使用下方的 BlueKitchen 版本。

## 环境

Windows x64、PowerShell 7、.NET SDK 8.0.420 或同一 8.0.4xx 系列的更高补丁版本（见根目录 global.json）。若重建 VIIPER，安装 `tools/viiper/haptic-src/go.mod` 要求的 Go 版本。原生转发需要 **LLVM-MinGW x64 的 clang**，不能直接用缺少 MinGW 头文件和导入库的 MSVC clang 替代。把 LLVM-MinGW 的 bin 目录加入 PATH，确保 clang、mingw32-make、llvm-strip 和 x86_64-w64-mingw32-windres 来自同一套工具链。本版使用 llvm-mingw 20260922 UCRT x86_64。仓库和依赖建议解压到无空格目录，例如 D:\pro2，避免上游 Makefile 解析路径时截断。

以下命令从仓库或源码依赖包的根目录运行。

## 1. 准备依赖

[v4.0 源码与构建依赖包](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v4.0/PRO2ControllerAssistant-v4.0-source.zip) 已提供两个固定提交的构建源文件、SHA-256 清单、固件与原许可。解压后直接跳到第 2 步。使用 GitHub 自动生成的 Source code ZIP 或 Git 克隆时，在项目根目录准备依赖：

```powershell
git clone --filter=blob:none https://github.com/bluekitchen/btstack.git third_party/btstack
git -C third_party/btstack checkout e38553977a25fb0b55b383c72c289be0975f422c

git clone --filter=blob:none https://github.com/mizuyoukanao/btstack.git third_party/btkey-btstack
git -C third_party/btkey-btstack checkout 4b49cb955d08dc80a82673083fc0463cbc0c2540

New-Item -ItemType Directory -Force third_party/realtek-firmware | Out-Null
Invoke-WebRequest https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/rtl_bt/rtl8852cu_fw.bin -OutFile third_party/realtek-firmware/rtl8852cu_fw
Invoke-WebRequest https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/LICENCE.rtlwifi_firmware.txt -OutFile third_party/realtek-firmware/LICENCE.rtlwifi_firmware.txt
```

构建脚本校验固件 SHA256：`9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0`。若上游文件更新导致不匹配，使用该发布源码依赖包的匹配文件，不绕过校验。

v4.0 的 VIIPER 已用本版修改后的 Go 源码重新编译。项目沿用原内嵌路径 `tools/viiper/haptic-v0.8.0/viiper-haptic-v2.8.exe` 及后端版本标识 `2.8-output-recovery`。本版后端文件的 SHA-256 为 `38dd3e18e254cb16dd15fe669dfe8e0b86b908b709939fbbc856d61ef31130c4`。如需重建，执行下列命令。

```powershell
Push-Location source/final-three-in-one/tools/viiper/haptic-src
$env:CGO_ENABLED='0'
go test ./... -count=1 -timeout=120s
go build -buildvcs=false -tags release -trimpath -ldflags '-s -w -X main.Version=2.8-output-recovery' -o ../haptic-v0.8.0/viiper-haptic-v2.8.exe ./cmd/viiper
Pop-Location
```

USBIP 安装器和原许可保留在 `source/final-three-in-one/tools/usbip-win2/v0.9.7.7`。内核驱动仍需由最终用户按 Windows 提示安装；复制 EXE 不会绕过安装或管理员要求。

## 2. 构建完整 EXE

```powershell
$relay='source/final-three-in-one/experiments/windows-wireless-pro1'
$out=Join-Path (Get-Location) 'artifacts/pro2-v4.0'
& "$relay/Publish-Integrated.ps1" -ArtifactDirectory $out -ToolBin (Split-Path (Get-Command mingw32-make).Source) -Dotnet (Get-Command dotnet).Source
```

完整程序位于 `artifacts/pro2-v4.0/publish/PRO2ControllerAssistant.exe`。打包前会检查 Switch 1 后端是否具有 4.0 的发射器选择与 NFC 只读接口，并运行离线自测。EXE 包含 VIIPER、USBIP 安装器、无线后端、匹配固件、恢复脚本及许可。

单独构建本机模式可使用：

```powershell
dotnet publish source/final-three-in-one/windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -o artifacts/local-only
```

## 3. 离线检查

```powershell
$tests='source/final-three-in-one/tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj'
$bundle=Join-Path (Get-Location) 'artifacts/pro2-v4.0/wireless-runtime.zip'
dotnet build $tests -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -p:EmbedWirelessRelay=true -p:EmbedUpstreamRealtek=true "-p:WirelessRelayBundlePath=$bundle" -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-wireless --wireless-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-relay --relay-integration-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-ui --relay-ui-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-physical --physical-only
./source/final-three-in-one/tools/tests/Test-TrayBehavior.ps1 -Executable ./artifacts/pro2-v4.0/publish/PRO2ControllerAssistant.exe -Results ./artifacts/qa-tray
```

回环检查需要允许 localhost TCP/UDP；UI 检查使用预览数据。上述离线入口不打开实体蓝牙、不 attach USBIP、不修改驱动。源码也保留明确命名的实机测试入口，不要把它们与离线检查混用。

## 协议与许可

当前蓝牙外发为 Pro1、Xbox One S Classic HID、DualSense Classic HID。原生 Pro2 BLE 身份、PS5 蓝牙触觉音频及主机认证未实现；Xbox / PS5 接收端仍待实测。真实物理输入、外发提交、HCI 回执和接收端实际频率分别看待，不用虚拟重复帧充当新输入。

BTstack 具有个人、非商业用途限制；完整构建据此按免费个人非商业用途分发。自有源码及所有依赖的具体许可见根目录 [NOTICE](../../NOTICE.md)。

[使用说明](../../README.md) · [v4.0 更新](../../docs/RELEASE-v4.0.md) · [转发细节](experiments/windows-wireless-pro1/INTEGRATED.md)
