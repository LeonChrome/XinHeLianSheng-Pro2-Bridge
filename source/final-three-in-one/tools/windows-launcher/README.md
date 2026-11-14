# Windows 启动封装

给现有 WPF 程序提供独立启动入口，不另建手柄实现。输入、背键、震动、USBIP 和蓝牙逻辑仍在原项目中。

先将 WPF 程序发布为自包含目录，再运行 `package.py`。Python 只用于构建，最终用户不需要 Python、Go 或额外 .NET。

```powershell
dotnet publish ../../windows/v60_viiper_app/Y700Switch2V60Viiper.csproj -c Release -r win-x64 --self-contained true -p:PureSoftwareRelease=true -p:PublishSingleFile=false -p:CompatibilityRendering=true -p:DebugType=None -o artifacts/runtime
python package.py artifacts/runtime artifacts/PRO2ControllerAssistant-v3.1.1-win-x64.exe --go (Get-Command go).Source
```

构建时从 Go 安装目录、NuGet 运行库缓存和项目中收集对应许可证，生成 ZIP 和逐文件哈希清单，运行启动器回归后编译 Windows GUI EXE，写入应用图标与版本资源。生成的载荷与 EXE 不提交 Git。

运行时解包到当前用户 `LocalAppData/PRO2WirelessReceiverControlBoard/app`，校验文件后启动现有程序。缓存损坏时使用独立的新目录，不覆盖或删除正在使用的运行目录。启动器只记录、等待主进程；不在后台反复扫描、重连或配置系统。

启动 20 秒内发生指定原生异常退出，最多尝试一次 `PRO2_STARTUP_SAFE` 兼容分支；暂停托盘与自动连接，采用进程内软件绘制及保守运行库选项。运行中崩溃不自动反复拉起，也不承诺覆盖任意原生故障。自启动注册使用分发 EXE 路径，不绑定临时解包目录。

`launcher_*.log` 与托管 `startup_*.log` 放入现有 `v6_logs`。不关闭其他应用、不提权、不改驱动、全局图形设置或蓝牙参数。
