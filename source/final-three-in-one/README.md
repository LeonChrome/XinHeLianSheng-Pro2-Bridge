# PRO2 手柄助手 · 源码

当前纯软件版：**v2.0**。主要程序位于 `windows/v60_viiper_app`，离线回归位于 `tools/tests/pure_software_core_test`，四模式虚拟设备后端位于 `tools/viiper/haptic-src/device`。

本目录保留原有布局，不另建独立架构。ESP、Pico、三合一相关目录仅作历史参考：[旧构建说明](README_THREE_IN_ONE_ARCHIVE.md)。

## 构建

在 Windows x64 上安装 .NET 8 SDK，以及 `tools/viiper/haptic-src/go.mod` 指定的 Go 版本。以下命令均从本目录运行：

```powershell
Push-Location tools/viiper/haptic-src
$env:CGO_ENABLED = '0'
go test ./device ./device/nspro ./device/ns2pro ./device/dualsense ./device/xbox360 ./internal/server/usb ./diagnostic_rate_contract -count=1 -timeout=60s
go build -buildvcs=false -tags release -trimpath -ldflags '-s -w -X main.Version=2.0.0-pro2-data-fidelity' -o ../haptic-v0.8.0/viiper-haptic-v2.exe ./cmd/viiper
Pop-Location

dotnet publish windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -o artifacts/pure-software
```

发布时内嵌 VIIPER 和仓库保留的 USBIP 安装器 / 许可证。最终 EXE 自带 .NET；USBIP 内核驱动仍需在首次使用时按提示安装。

兼容启动备用包：在 publish 命令中增加 `-p:CompatibilityRendering=true`，并使用不同输出目录（如 `artifacts/pure-software-compatible`）。仅将本程序界面选择为软件渲染，不改手柄逻辑或全局图形设置；标准构建默认关闭。对应源码标签为 `pro2-v2.0-ui-compatible`。[说明](../../docs/RELEASE_v2.0_UI_COMPATIBLE_ZH.md)

v2.0 默认嵌入 `viiper-haptic-v2.exe`，不能用旧后端替代。真实 FD2、上游状态、协议报告和保持重复分别统计；Pro2 使用显式4ms协议重采样适配有线接收端，重复不算新物理采样。

## 离线检查

```powershell
dotnet build tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj -c Release -p:PureSoftwareRelease=true -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-results
```

离线检查不修改蓝牙参数或驱动；恢复机制仅使用临时 HKCU 测试键并在结束时清理。这些检查不证明某台电脑已达到 133Hz，真实速率需连接 Pro2 实测。

[使用说明](../../README.md) · [v2.0 更新说明](../../docs/RELEASE_v2.0_ZH.md)
