# v2.7.1：多手柄休眠保护与连接诊断

沿用 v2.7 的四手柄管理、独立重连和单只热加入。

- 每只手柄独立计时休眠，按对应地址监听唤醒，保留虚拟手柄与其他玩家的输入输出。
- 排队连接、调参或准备唤醒期间若再次操作手柄，取消本次休眠。命令失败不再强制停止输入或标成已休眠。
- 蓝牙监听异常会保留休眠状态并重试，不把扫描报错当作按键唤醒；暂停监听也不丢失休眠地址。
- 同一只手柄的定时电量查询与休眠握手互斥。多蓝牙接口无法确认归属时暂缓休眠，避免登记错误的主机地址。
- 首页新增连接失败提示；“系统与蓝牙信息”包含各槽位失败阶段、扫描广播数、候选数、系统错误、最近八次尝试。
- 保留主动／被动扫描兼容路径，并为设备打开与服务发现设置等待上限；不再无限等待某一候选。

诊断会区分：系统未提供 BLE 接口、未提供中央角色、蓝牙关闭、权限拒绝、扫描错误、未见 Pro2 广播、地址由其他槽位保留、GATT 不可达、缺少接口、通知无有效输入。失败提示会给出下一步；证据不足时明确说明原因尚未确认。

**驱动版本／日期与标称蓝牙版本不能单独证明故障原因。** 未查询厂商更新目录时，不声称驱动过旧；不会自动安装驱动或因连接失败静默重启蓝牙。连接成功后的低速检测与 Win10 授权优化、Win11 有限预设继续保留。

首次登记唤醒可能挤掉 Switch 2 主机配对记录；遇到时重新和主机配对即可。多只实体手柄同时休眠、唤醒及 133Hz 长测仍需实机验收，离线回归不等于无线稳定性证明。

实现依据：

- [Microsoft：BLE 中央角色等适配器能力](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothadapter?view=winrt-26100)
- [Microsoft：GATT 返回状态](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcommunicationstatus?view=winrt-26100)
- [Microsoft：蓝牙驱动更新途径](https://support.microsoft.com/en-us/windows/hardware/bluetooth/update-bluetooth-drivers-in-windows)
