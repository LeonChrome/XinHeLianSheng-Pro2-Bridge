# USB → 无线 Pro1 转发实验 0.2

复用现有助手的实体 USB 输入、校准、体感和震动转换。独立入口，不启动 USBIP，不改变正式助手的默认功能。当前目标是完整 Pro1 Classic HID；PS5 / Xbox 蓝牙输出不包含在本轮。

## 已实现

- Pro2 USB / Pro1 USB 输入 → 独立原生蓝牙后端 → Pro1 身份输出。
- Pro1 SDP / 057E:2009 身份、主机握手、SPI 校准读取、按键、摇杆和体感报告。
- 震动反向接回现有转换器；实体 Pro1 保留原生 8 字节反馈。
- Pro2 USB 只读 NFC 命令通道；读卡过程中继续接收 HID 输入，暂时暂停震动。
- 将读出的 NTAG215 540 字节快照转换为 Pro1 NFC 状态及分块应答；也可载入已有 540 / 572 字节文件。可手动移除快照。
- 重复分块请求重发相同内容；拒绝写卡、写闪存及未知命令。读取超时或应答失步后，要求重新连接实体手柄。

已完成编译及离线协议回放。0.2 已在本机真实 Pro2 USB 的同一连接连续三次读出 540 字节 amiibo，UID/BCC 与格式检查全部通过；每次约 0.42～0.43 秒，结束后 5 秒输入窗口约 249.99Hz，队列丢弃为 0。这是有限窗口的真实 USB 读卡验收。完整 Pro1 蓝牙连接与接收端 amiibo 识别仍需验收。

## 0.2 修复

- 移除 USB NFC 初始化中不会应答的可选 `01/01` 命令，避免读卡前就超时。保留已经实测有应答的 `01/0C`，后续命令均按真实 ACK 判断。
- 日志补充 USB NFC 状态、实际 89 字节分块及末包声明长度；不会把 100 字节传输帧的尾部补零当成标签数据。
- USB 单独测试也持续消费输入队列，避免“尚未开始无线输出”导致的队列丢弃误报。未发送到无线的报告单列为基线消费。
- 窗口明确显示系统蓝牙 / WinUSB 状态与“无线尚未启动”的原因，不再只留一个禁用按钮。

## 运行

1. 从托盘退出正式助手，关闭正在使用同一只 USB 手柄的其他工具，再使用数据线连接 Pro2 / Pro1。实验窗口会检查占用，避免两个程序同时初始化实体手柄。
2. 运行 `PRO2UsbWirelessPro1.exe`，选择实体输入，点“连接 USB”。只测试 USB / NFC 时不需要启动发射端。
3. 选择已准备为 WinUSB 的实验蓝牙适配器，点“开始无线 Pro1”。接收设备搜索 **Pro Controller**。一轮配对等待最多 180 秒，连接后测试 180 秒。
4. NFC 可点“读取 amiibo”，或在发射端就绪后“载入 .bin”。接收端开始扫描且没有快照时，也会请求一次实体 Pro2 USB 读卡。贴卡、保持不动。
5. 点“停止”释放资源；“打开本轮日志”查看证据。

无线报告可选 Pro1 标准约 66.7Hz、133Hz 或 250Hz。默认先用标准节奏验证完整协议、体感和 NFC；两档高速是实验请求，不是接收端性能保证。USB 输入继续按真实频率接收。只选取真实新报告，保留短按/释放转换；不会用定时器重复旧状态凑数字。体感按报告跨度选取已有历史，原生 15ms / 三样本及高频实验分别记录；三样本顺序按照 [SDL 接收端实现](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_switch.c)处理。

有线输入只需一只蓝牙负责外发。当前后端支持此前测试过的 10D7:B012 与 0BDA:0852 USB 适配器，且仅打开选中的精确设备实例。适配器必须已绑定 WinUSB。工具不会自动安装、更换或恢复驱动，也不会接管仍使用 BTHUSB 的系统蓝牙；因此本轮没有需要恢复的系统修改。

接收端若已把同一只蓝牙记作“电脑”或旧测试手柄，可能缓存了旧名称、服务和配对信息。只在接收端忘记对应旧条目，再搜索 Pro Controller；不要删除 Windows 的实体 Pro2 配对记录。

如果单独使用现有 Realtek 外发，必须先在另一个可恢复的准备步骤中临时改绑 WinUSB，Windows 蓝牙功能将暂时不可用。旧 Win10 时期的备份不能作为升级 Win11 后的新基线；不要直接运行旧恢复脚本覆盖当前驱动。当前实验包不会替你执行这一步。

## NFC 范围

当前是 **实体读取 → 本地只读快照 → Pro1 协议应答**。读取后移开实体卡，不会自动撤回已经缓存的虚拟标签；使用“移除标签快照”。快照可替换，正在进行的旧读取不会覆盖用户随后载入或移除的标签。

不转发 NFC 写入，不验证 amiibo 真伪，不承诺所有游戏或主机固件兼容。Pro1 官方有线模式会关闭 NFC，因此 Pro1 USB 本轮只提供普通操作、体感和震动；NFC 可用预先读好的文件。Pro2 官方支持有线 NFC，本项目也已完成真实 USB 连续读卡验收。见[任天堂说明](https://www.nintendo.com/en-gb/Support/Troubleshooting/How-to-Enable-Disable-Nintendo-Switch-Pro-Controller-Wired-Communication-on-Nintendo-Switch-2-2892719.html)。

## 日志与判定

每轮日志位于 `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\wireless_relay_logs\时间戳\`，仅当前 Windows 用户可读写。

- `experiment.log`：实体 USB 初始化、真实输入窗口、主机命令、NFC 命令耗时/状态/分块、震动和错误。异步队列写盘；队列丢弃单独计数。
- `radio-result.json`：原生后端提交数、HCI 完成回执、间隙、过期/覆盖次数、断开原因。
- `session-result.json`：系统版本、实体输入、主机命令、震动、日志完整性和退出状态。
- `last-read.bin`：完整性校验通过的 540 字节标签文件；含私人标签内容，不应随日志公开上传。

USB 250Hz、提交频率、HCI 完成回执和接收端真实输入频率分别记录。接收端未实测时 `receiverRateVerified=false`，不会把完成回执标成端到端 133 / 250Hz 成功。协议 ACK / NFC 应答不计入新的实体输入报告。

蓝牙发送使用带返回值的本地封装，保留上游 HID 状态机；MTU 不足或其他 L2CAP 错误会记录 `HID_SEND_REJECTED` 的状态、报文长度和接收端 MTU，并停止本轮。按键状态改变在原生发射端单独保序，模拟量可覆盖过期候选；积压过多或输入超过 500ms 不更新时停止，避免留下旧按键状态。仅当前目标连接的断开事件会结束本轮，其他连接事件不会误触发。

会话令牌不写日志，停止后删除。没有导入 Windows 配对密钥；本轮后端配对信息只保留在进程内存，重开实验可能需要重新配对。日志保留两段各约 16MB；完整测试窗口建议在 3 分钟内。

## 编译及离线验证

`Build-Native.ps1` 使用之前的官方 BTstack 源码和 LLVM-MinGW 工具链，产出 `pro2-wireless-pro1-radio.exe`。`Publish-Experiment.ps1` 构建实验窗口并打包本地文件夹。

共用回归项目新增 `WirelessPro1RegressionTests` 与 `UsbNfcRegressionTests`。`--wireless-only` 验证 IPC 信封、Pro1 握手、按键位置、六轴单位、NFC 分块重发/CRC/540 字节往返，以及 USB NFC 失败恢复。这些测试不打开蓝牙，不使用实体标签。原生 `--self-test` 也不打开无线电；`--verify-ipc-fixture 文件` 用于 .NET / C 数据格式互通检查。

## 来源与许可

- 原项目 `native-btstack` 发射器、Pico `switch1_protocol.cpp` HID 定义与共享 Windows 输入/反馈代码。
- [Pro1 HID 逆向资料](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering)：协议字段和命令。
- [PicoSwitch2](https://github.com/notsosaelin/PicoSwitch2)：本项目已有 Pro2 NFC 只读流程来源，原 Apache-2.0 声明继续保留。
- BTstack 使用原始许可证，具有个人、非商业用途限制。本实验原生后端仅用于本地个人验证，没有加入正式商业发行包；未来分发或销售前需另行解决授权。

没有复制 joycontrol 的 GPL 实现进发行源码；参考的是公开报文结构，读写策略、分块重传和实现使用本项目自己的代码。
