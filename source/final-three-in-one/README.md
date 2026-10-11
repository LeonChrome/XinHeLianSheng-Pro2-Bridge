# PRO2 手柄助手 · 源码

当前纯软件版：**v1.3.17**。主要程序位于 `windows/v60_viiper_app`，离线回归位于 `tools/tests/pure_software_core_test`；Pro1 虚拟设备后端位于 `tools/viiper/haptic-src/device/nspro`。

本目录保留原有布局，不另建独立架构。ESP、Pico、三合一相关目录仅作历史参考：[旧构建说明](README_THREE_IN_ONE_ARCHIVE.md)。

## 构建

在 Windows x64 上安装 .NET 8 SDK，以及 `tools/viiper/haptic-src/go.mod` 指定的 Go 版本。以下命令均从本目录运行：

```powershell
Push-Location tools/viiper/haptic-src
$env:CGO_ENABLED = '0'
go build -trimpath -ldflags '-s -w' -o ../haptic-v0.8.0/viiper-haptic-pro1.exe ./cmd/viiper
Pop-Location

dotnet publish windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -o artifacts/pure-software
```

发布时内嵌 VIIPER 和仓库保留的 USBIP 安装器 / 许可证。最终 EXE 自带 .NET；USBIP 内核驱动仍需在首次使用时按提示安装。

## 离线检查

```powershell
dotnet build tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj -c Release -p:PureSoftwareRelease=true -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-results
```

离线检查不修改蓝牙、注册表或驱动，也不证明某台电脑已达到 133Hz。真实速率需连接 Pro2 实测。

[使用说明](../../README.md) · [v1.3.17 更新说明](../../docs/RELEASE_v1.3.17_ZH.md)
