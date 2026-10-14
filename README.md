# PRO2 手柄助手

让 **Nintendo Switch 2 Pro（Pro2）手柄无线连接 Windows**，在 Steam 和其他 PC 游戏中使用。利用电脑的 BLE 蓝牙，无需专用接收器。

**[推荐下载：v1.3.22 Windows EXE](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v1.3.22/PRO2ControllerAssistant-v1.3.22-win-x64.exe)** · [版本说明](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/tag/pro2-v1.3.22)

## 能做什么

- **四种模式**：Steam / Pro2、Pro1、Xbox、PS5 兼容输出。
- **背键 / C 键自定义**：Pro1、Xbox、PS5 模式支持单键、组合键、连发、顺序动作和延迟；没连接手柄也能设置。
- **震动与陀螺仪**：震动强度调节、PS5 触觉转换、陀螺仪校准。
- **手柄状态**：真实输入频率、电量档位、固件版本、耳机插入状态；安全检查通过时每 10 分钟更新电量。
- **连接与诊断**：自动重连、首页低速提示、系统与蓝牙信息、逐组优化结果；设置中可手动重启蓝牙。

## 怎么用

1. 下载 EXE，退出旧版；手柄保持 **PRO2 模式**，开启配对广播。
2. 打开软件，点击“连接 Pro2”，选择游戏模式。无需先在 Windows 蓝牙设置中添加手柄。
3. 首次使用按提示安装内置 **USBIP 驱动**，可能需要管理员授权或重启。无需另装 .NET 或 VIIPER；已有 USBIP 支持自动查找或手动指定位置。

## 速率与兼容性

支持 **Windows 10 2004 及以上 / Windows 11 x64**，电脑需有可用的 BLE 蓝牙。

目标是真实 **133Hz**，达到 **60Hz** 也允许游玩；低速时首页给出诊断和优化建议。Win11 自动尝试连接参数；Win10 在首页点击“优化蓝牙 · Win10”授权测试，修改前备份，支持恢复。Win10 优化会改变系统全局 BLE 参数，可能影响其他蓝牙设备或需要重启。

实际速率受系统、适配器和驱动影响，不保证所有电脑达到 133Hz，以实测为准。PS5 模式提供兼容输出与触觉转换，不等于完整复刻 DualSense；无线耳机音频和跨设备蓝牙转发尚不支持。

v1.3.22 修复托盘恢复后的速率停刷，电量更新改为 10 分钟，PS5 默认音频保护已内置。

[反馈问题](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/issues) · [源码与构建](source/final-three-in-one/README.md) · [许可证](LICENSE) · [第三方致谢](ACKNOWLEDGEMENTS.md) · [历史三合一版本](docs/HISTORICAL_THREE_IN_ONE_README.md)
