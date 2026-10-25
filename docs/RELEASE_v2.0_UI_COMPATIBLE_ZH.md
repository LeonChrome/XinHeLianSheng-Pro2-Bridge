# v2.0 兼容启动版

普通版能正常打开的用户无需更换。出现启动即退出、窗口不显示时，可尝试 [兼容启动版 EXE](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v2.0/PRO2ControllerAssistant-v2.0-ui-compatible-win-x64.exe)。

兼容版使用软件绘制界面，手柄连接及 Pro2 / Pro1 / Xbox / PS5 四模式功能不变，不修改显卡驱动、系统图形设置或蓝牙参数。使用前从托盘退出旧程序，不要同时运行两个版本；设置继续沿用。

这是针对部分图形兼容问题的备用方案，本机启动及离线回归通过，尚未在报告闪退的电脑复测，不保证解决所有闪退。

## 源码与构建

[兼容版对应源码](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/tree/pro2-v2.0-ui-compatible)。原 `pro2-v2.0` 标签与原 EXE 保留；GitHub 原 v2.0 自动生成的源码包不包含本次新增开关。

从 `source/final-three-in-one` 目录执行（先按源码 README 准备内嵌后端）：

```powershell
dotnet publish windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -o artifacts/pure-software-compatible
```

新增内容仅包括启动渲染策略、构建开关、诊断日志及测试。标准构建默认不启用软件渲染；也可显式传入 `--software-rendering`。

## 校验与范围

- EXE：`PRO2ControllerAssistant-v2.0-ui-compatible-win-x64.exe`，125355773 字节。
- SHA256：`F4FEF4FAECDF08FA22A52145924BC43593C4535E248B8DAD8C3F987E25CA8478`。
- 内置后端与原 v2.0 相同，USBIP 安装器仍内置。
- 768322 项离线检查通过；最终交付 EXE 启动检查读回 `SoftwareOnly`。这些检查不代替远端故障电脑或实体手柄验收。
- 软件渲染仍依赖 Windows 原生图形组件，不等于完全不加载图形 DLL，也不能恢复已经损坏的原生进程。
