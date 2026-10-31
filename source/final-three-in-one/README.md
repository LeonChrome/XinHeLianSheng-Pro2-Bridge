# PRO2 手柄助手 · 源码

当前纯软件版：**v2.4**。主要程序位于 `windows/v60_viiper_app`，离线回归位于 `tools/tests`，四模式虚拟设备后端位于 `tools/viiper/haptic-src/device`。

本目录保留原有布局，不另建独立架构。ESP、Pico、三合一相关目录仅作历史参考：[旧构建说明](README_THREE_IN_ONE_ARCHIVE.md)。

## 构建

在 Windows x64 上安装 .NET 8 SDK，以及 `tools/viiper/haptic-src/go.mod` 指定的 Go 版本。以下命令均从本目录运行：

```powershell
Push-Location tools/viiper/haptic-src
$env:CGO_ENABLED = '0'
go test ./device ./device/nspro ./device/ns2pro ./device/dualsense ./device/xbox360 ./internal/server/usb ./diagnostic_rate_contract -count=1 -timeout=60s
go build -buildvcs=false -tags release -trimpath -ldflags '-s -w -X main.Version=2.1.1-ps5-native-hid48' -o ../haptic-v0.8.0/viiper-haptic-v2.1.1.exe ./cmd/viiper
Pop-Location

dotnet publish windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -o artifacts/pure-software
```

发布时内嵌 VIIPER 和仓库保留的 USBIP 安装器 / 许可证。最终 EXE 自带 .NET；USBIP 内核驱动仍需在首次使用时按提示安装。

兼容启动备用包：在 publish 命令中增加 `-p:CompatibilityRendering=true`，并使用不同输出目录（如 `artifacts/pure-software-compatible`）。仅将本程序界面选择为软件渲染，不改手柄逻辑或全局图形设置；标准构建默认关闭。

v2.4 默认嵌入 `viiper-haptic-v2.1.1.exe`，本次未更改该后端，不能用旧版替代。真实 FD2、上游状态、协议报告和保持重复分别统计，重复报告不算新物理采样。

游戏图标资源位于 `windows/v60_viiper_app/Assets/GameGlyphs/PBZ`，由独立服务管理三个补丁文件。发布包不含本地资源提取参数或解包工具。

## 离线检查

```powershell
dotnet build tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj -c Release -p:PureSoftwareRelease=true -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-results

dotnet run --project tools/tests/game_glyph_patch_test/GameGlyphPatchTest.csproj -c Release
```

离线检查不修改真实蓝牙参数或驱动：参数恢复回归使用临时 HKCU 测试键，图标安装回归使用临时游戏目录。这些检查不证明某台电脑已达到 133Hz，真实速率需连接 Pro2 实测。

[使用说明](../../README.md) · [v2.4 更新说明](../../docs/RELEASE_v2.4_ZH.md)
