# PRO2 手柄助手 v3.5.0 · 源码与构建

主程序：`windows/v60_viiper_app`。原生外发后端：`experiments/windows-wireless-pro1/native`。虚拟设备后端及其修改源码：`tools/viiper/haptic-src`。离线检查：`tools/tests/pure_software_core_test`。均沿用现有项目结构。

## 获取源码

GitHub `pro2-v3.5.0` 标签提供完整仓库快照；Release 的 `PRO2ControllerAssistant-v3.5.0-source.zip` 提供当前 Windows 程序、四模式后端、原生转发代码、测试、界面资源、构建依赖及许可。后者还包含固定版本的 BTstack 编译输入和 Realtek 运行固件，避免依赖作者电脑目录。

二进制 EXE 构建自 `a3b7188`；该版本发布标签随后整理文档、许可和构建入口，未改变已测试的运行逻辑。

## 环境

Windows x64、PowerShell 7、.NET 8 SDK。若重建 VIIPER，安装 `tools/viiper/haptic-src/go.mod` 要求的 Go 版本。原生转发需要 **LLVM-MinGW x64 的 clang**，不能直接用缺少 MinGW 头文件和导入库的 MSVC clang 替代。把工具加入 PATH，或在脚本参数中指定完整路径。

以下命令从仓库或源码依赖包的根目录运行。

## 1. 准备依赖

源码依赖包已提供下述 `third_party` 内容，可跳过下载。直接克隆仓库时执行：

```powershell
git clone --filter=blob:none https://github.com/bluekitchen/btstack.git third_party/btstack
git -C third_party/btstack checkout e38553977a25fb0b55b383c72c289be0975f422c

New-Item -ItemType Directory -Force third_party/realtek-firmware | Out-Null
Invoke-WebRequest https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/rtl_bt/rtl8852cu_fw.bin -OutFile third_party/realtek-firmware/rtl8852cu_fw
Invoke-WebRequest https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/LICENCE.rtlwifi_firmware.txt -OutFile third_party/realtek-firmware/LICENCE.rtlwifi_firmware.txt
```

构建脚本校验固件 SHA256：`9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0`。若上游文件更新导致不匹配，使用该发布源码依赖包的匹配文件，不绕过校验。

VIIPER 可使用仓库保留的 `tools/viiper/haptic-v0.8.0/viiper-haptic-v2.8.exe`。重建方法：

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
$out=Join-Path (Get-Location) 'artifacts/pro2-v3.5.0'
& "$relay/Build-Native.ps1" -BuildDirectory "$out/native" -Compiler (Get-Command clang).Source
& "$relay/Publish-Integrated.ps1" -ArtifactDirectory $out -SkipNativeBuild -Dotnet (Get-Command dotnet).Source
```

输出：`artifacts/pro2-v3.5.0/publish/PRO2ControllerAssistant.exe`。脚本嵌入 VIIPER、USBIP 安装器、无线后端、匹配固件、恢复脚本和许可，并校验无线运行包文件哈希。不会运行实机蓝牙测试或自动修改驱动。

单独构建本机模式可使用：

```powershell
dotnet publish source/final-three-in-one/windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -o artifacts/local-only
```

## 3. 离线检查

```powershell
$tests='source/final-three-in-one/tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj'
$bundle=Join-Path (Get-Location) 'artifacts/pro2-v3.5.0/wireless-runtime.zip'
dotnet build $tests -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -p:EmbedWirelessRelay=true "-p:WirelessRelayBundlePath=$bundle" -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-wireless --wireless-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-relay --relay-integration-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-ui --relay-ui-only
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-physical --physical-only
```

回环检查需要允许 localhost TCP/UDP；UI 检查使用预览数据。上述离线入口不打开实体蓝牙、不 attach USBIP、不修改驱动。源码也保留明确命名的实机测试入口，不要把它们与离线检查混用。

## 协议与许可

当前蓝牙外发为 Pro1、Xbox One S Classic HID、DualSense Classic HID。原生 Pro2 BLE 身份、PS5 蓝牙触觉音频及主机认证未实现；Xbox / PS5 接收端仍待实测。真实物理输入、外发提交、HCI 回执和接收端实际频率分别看待，不用虚拟重复帧充当新输入。

BTstack 具有个人、非商业用途限制；完整构建据此按免费个人非商业用途分发。自有源码及所有依赖的具体许可见根目录 [NOTICE](../../NOTICE.md)。

[使用说明](../../README.md) · [v3.5.0 更新](../../docs/RELEASE-v3.5.0.md) · [转发细节](experiments/windows-wireless-pro1/INTEGRATED.md)
