# PRO2 手柄助手 · 源码

当前源码构建：**v3.2.1**。主要程序位于 `windows/v60_viiper_app`，离线回归位于 `tools/tests`，四模式虚拟设备后端位于 `tools/viiper/haptic-src/device`。

本目录保留原有布局，不另建独立架构。ESP、Pico、三合一相关目录仅作历史参考：[旧构建说明](README_THREE_IN_ONE_ARCHIVE.md)。

## 构建

在 Windows x64 上安装 .NET 8 SDK，Python 3，以及 `tools/viiper/haptic-src/go.mod` 指定的 Go 版本。以下命令均从本目录运行：

```powershell
Push-Location tools/viiper/haptic-src
$env:CGO_ENABLED = '0'
go test ./... -count=1 -timeout=120s
go build -buildvcs=false -tags release -trimpath -ldflags '-s -w -X main.Version=2.8-output-recovery' -o ../haptic-v0.8.0/viiper-haptic-v2.8.exe ./cmd/viiper
Pop-Location

dotnet publish windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -p:PublishSingleFile=false -p:CompatibilityRendering=true -p:DebugType=None -o artifacts/pure-software
python tools/windows-launcher/package.py artifacts/pure-software artifacts/PRO2ControllerAssistant-v3.2.1-win-x64.exe --go (Get-Command go).Source
```

发布时内嵌 VIIPER 和仓库保留的 USBIP 安装器 / 许可证。最终 EXE 自带 .NET；USBIP 内核驱动仍需在首次使用时按提示安装。

分发 EXE 使用独立启动封装，默认软件绘制，包含校验与一次兼容回退；目录版也可直接打 ZIP 作为备用。具体构建见 [启动封装](tools/windows-launcher/README.md)。

v2.8 使用 `viiper-haptic-v2.8.exe` 后端，新增 API 数据流生命周期和 USBIP 独占导入保护，不能用旧后端替代。真实 FD2、上游状态、协议报告和保持重复分别统计，重复报告不算新物理采样。触控手势由 `Ps5TouchGestures.cs` 和现有背键引擎按新输入帧推进，经 PS5 通道传递触点坐标，不制造 FD2。

ABXY 映射由 `FaceButtonMapping.cs` 在背键引擎前处理；四种输出模式分别保存，默认保持原有位置映射。`ViiperBridgeSession.UsbipHealth.cs` 在后台检查本机挂载与 PnP 状态，恢复仅针对对应导出，不重连 BLE，也不清除其他槽位。

游戏图标资源位于 `windows/v60_viiper_app/Assets/GameGlyphs/PBZ`，由独立服务管理三个补丁文件。发布包不含本地资源提取参数或解包工具。

## 离线检查

```powershell
dotnet build tools/tests/pure_software_core_test/PureSoftwareCoreTest.csproj -c Release -p:PureSoftwareRelease=true -o artifacts/qa-bin
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/qa-results

# 单独检查触控轨迹、旧配置迁移与按键安全
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/touch-results --touch-only
# 单独检查 ABXY 映射组合、配置切换与挂载恢复判定
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/v28-results --v28-only
# 单独检查四路同模式 / 混合模式输入、反馈隔离、模式切换、地址保留和速率保护
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/multi-results --multi-only
# 使用内置图标补丁，在临时游戏目录检查安装、升级与恢复
./artifacts/qa-bin/PureSoftwareCoreTest.exe artifacts/glyph-results --glyph-only
```

离线检查不修改真实蓝牙参数或驱动：参数恢复回归使用临时 HKCU 测试键，图标安装回归使用临时游戏目录。这些检查不证明某台电脑已达到 133Hz，真实速率需连接 Pro2 实测。

[使用说明](../../README.md) · [v3.2.1 更新说明](../../docs/RELEASE_v3.2.1_ZH.md)
