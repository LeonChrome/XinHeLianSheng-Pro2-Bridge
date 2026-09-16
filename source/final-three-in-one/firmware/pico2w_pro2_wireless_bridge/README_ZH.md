# 新和联胜 Pico 2 W Pro2 无线桥接 Rev B r38 候选固件

r38 继承 r37 的四模式、普通震动、IMU、USB 输入源分级恢复、重新配对组合键、单活动主机、摇杆满量程、NS1 震动诊断和密钥失配恢复。本轮修正 Classic HID 主动回连提前拆除未完成 page 所造成的 L2CAP 资源耗尽，并严格校验 PS5 / Edge 输出报告的长度、振动标志与蓝牙 CRC。游戏震动不再被统一截断为 250ms，只有本地诊断脉冲仍使用明确时长。UF2 写入范围不会覆盖末端的模式与蓝牙绑定存储区。

## Rev B 六键背板

Rev B PCB 增加六个已经进入网表和固件的实体侧按键：

- `重启配对`：长按 1.5 秒清除当前模式的绑定并重启；
- `PS5`：切换 DualSense；
- `Xbox`：切换 Xbox BLE；
- `PS5 Edge`：切换 DualSense Edge；
- `Pro2`：进入静默态，关闭 Pro2 口 5 V、释放 GP0/GP1、停止无线手柄广播；
- `NS1 Pro`：切换 Switch 1 Pro。

板载 LED 使用闪烁次数显示当前模式：PS5 1 次、Xbox 2 次、Edge 3 次、Pro2 静默 4 次、NS1 Pro 5 次；清除配对时快速闪烁。

> Pro2 静默态不是整机断电。Pico 仍保留按键扫描和 CDC 调试，以便不拆电池就能切回其他模式；物理总电源滑动开关才是完全断电。

> `GP3` 必须通过 MOSFET/负载开关控制 Pro2 口 5 V，静默态才能等效于物理拔线。若 Pro2 的 VBUS 直接接常通 5 V，`GP3` 不会凭空切断电源，此时仍需实体开关或拔线。

Pico 2 W 通过 USB Host 读取真实 Pro2，再以无线手柄身份连接主机：

`真实 Pro2 USB -> Pico 2 W -> Xbox BLE / PS5 / PS5 Edge / Switch 1 Pro`

本版明确不模拟 DualSense 音频设备，也不实现音频流驱动的 HD 震动。四个模式都只把主机震动命令转换为 Pro2 可执行的普通双通道震动。

## 四种无线模式 + Pro2 静默态

| 命令 | 无线身份 | 输入与传感器 | 震动 |
|---|---|---|---|
| `mode xbox` | Xbox 兼容 BLE HID | 按键、摇杆、扳机；Xbox 协议不输出陀螺仪 | 普通强弱震动 |
| `mode ps5` | DualSense `054C:0CE6` | 按键、摇杆、扳机、IMU | 普通强弱震动 |
| `mode edge` | DualSense Edge `054C:0DF2` | PS5 输入、IMU、GL/GR 背键 | 普通强弱震动 |
| `mode pro2` | 不广播无线 HID | Pro2 口 5 V 关闭、GP0/GP1 高阻；仅保留按键扫描 | 无 |
| `mode switch1` | Switch 1 Pro `057E:2009` | Nintendo 按键、摇杆、每帧 3 组真实 IMU | HD 编码幅度降级为普通震动 |

首次刷入或从旧存储格式升级时默认进入 PS5 Edge，且不会清空四种身份的蓝牙配对区。用户主动切换后，模式写入 Pico 闪存，重新上电继续使用最后一次选择。也可长按 `GP2` 约 1.5 秒循环切换。

四种无线身份拥有独立蓝牙地址和独立配对区。每个身份首次配对一次后，后续切换模式或重新上电会自动连接该模式上次保存的主机，不需要反复删除配对。`wireless forget` 只清除当前无线模式的配对资料。Pro2 静默态不建立配对区。

没有保存主机时，Classic 可发现/可连接和 Xbox BLE 广播会持续开放，固件不设置一分钟配对超时。已有主机时优先重连旧主机，并拒绝新主机抢占 Classic HID；需要换设备时使用下面的重新配对组合键。

## Pro2 组合键切换

同时按住 `GL + GR + ZL + ZR`，再按住且仅按住一个面键，连续保持 1.5 秒：

| 面键 | 目标模式 |
|---|---|
| `A` | PS5 / DualSense |
| `B` | PS5 Edge |
| `X` | Xbox BLE |
| `Y` | Switch 1 Pro |

- 组合必须连续保持，任意保护键或选择键松开后立即取消计时。
- 同时按下多个面键不会选择模式。
- Pro2 输入断开或报告超过 100 ms 未更新时立即取消，旧按键状态不会触发切换。
- 一次按住只触发一次；必须完全松开组合才能重新触发。
- 若选择的就是当前模式，只记录提示而不重启。
- 切换会保存目标模式并重启 Pico，随后使用该模式独立保存的配对密钥自动重连。

## Pro2 重新配对组合键

同时按住 `GL + GR + ZL + ZR + Plus + Minus` 2 秒：

- 清除当前无线模式保存的主机并重新开放配对；
- 只影响当前模式，不删除另外三种身份的主机资料；
- Pro2 轻微低频震动两下，表示重新配对动作已经受理；
- 与 `A/B/X/Y` 模式选择键互斥，避免同一次长按既切模式又清配对；
- 输入超过 100 ms 未更新时立即取消，不会用卡住的旧按键状态误触发。

实体背板“重启配对”键仍然可用：长按 1.5 秒后执行同一清除动作，并在短暂反馈后重启当前模式。

## 平台连接边界

- Windows、Android、macOS、iPhone/iPad：可在系统蓝牙页面选择当前 PS5、Edge 或 Xbox BLE 名称；具体游戏是否支持陀螺仪、震动和背键由系统与游戏决定。
- Switch 1：切换到 NS1 Pro，进入“手柄 > 更改握法/顺序”后配对 `Pro Controller`。
- Xbox 主机：不支持。当前 Xbox 身份是 BLE，Xbox 主机使用 Xbox Wireless。
- 实体 PS5 主机：不支持。官方首次配对需要 DualSense USB 身份与主机认证，而本项目 Pico 原生 USB 当前仅用于刷机和诊断。

## IMU 原则

- 只对真实 Pro2 新采样做处理，不用重复帧伪造更高的传感器频率。
- 不加入平滑、低通、死区、回正或运动预测。
- 静止条件满足后，用 250 个样本完成一次陀螺仪零偏校准；运动时不更新零偏。
- PS5 / Edge 坐标映射为 `+X,+Z,-Y`。
- DualSense 比例为陀螺仪 `16.384 raw/(deg/s)`、加速度 `8192 raw/g`。
- Switch 1 保留 Nintendo 坐标，陀螺仪 `16.4 raw/(deg/s)`、加速度 `4096 raw/g`。
- DualSense 传感器时间戳取自真实 Pro2 样本时间，不取无线发送循环时间。

串口命令：

```text
imu status
imu calibrate
```

`imu calibrate` 会清除当前零偏并重新收集静止样本。

## 输入与稳定性

- Pico-PIO-USB 在 `GP0/GP1` 上作为 USB Host，认领 Pro2 `057E:2069`。
- Pro2 输入报告以真实新帧为准，不用重复状态伪造源频率；实机稳定源速率为 `250.0 Hz`。
- PS5 / Edge 按真实源节奏发送。r19 实机稳态分别约为 `237.3 Hz` 和 `239.8 Hz`，源数据年龄约 `1 ms`。
- Xbox BLE 请求 `7.5 ms` 连接间隔，并受 ATT 反压控制；实机应用层通知稳定为 `250.0 Hz`，没有发送错误。
- Switch 1 精确按 `15 ms` 周期发送，即 `66.7 Hz`；每帧装入按 `5 ms` 间隔从真实历史中取出的 3 个 IMU 样本。
- Pro2 断开或输入失效时只发送中立状态，避免卡键、卡摇杆和陀螺仪残留。
- USB 初始化、输入接收和无线发送都有超时、退避重试与健康日志。
- Classic 模式同一时刻只允许一个主机占用 HID。连接成功后关闭可发现/可连接状态，避免 Windows、Switch 或其他已配对主机互相抢占。
- Classic 模式采用“先接受主机回连，再主动补连”的互斥调度：初次被动窗口 0.5 秒，主动补连最多等待 6 秒；每次主动补连时临时关闭可连接状态，避免 Pico 与 Windows 同时发起连接而争抢 HID PSM。
- 只进行 1 轮快速主动补连；失败后立即进入 30 秒完整可发现窗口，再进行低频后台主动尝试。同一次 HID/ACL 失败只排程一次，避免连接请求逐步堆积。
- 主动或被动连接收到 ACL、但 HID 通道未完成时会记录并主动断开对应 ACL 句柄，再进入下一窗口，不让半连接与后续重试互相争抢。
- 若 Pico 保存着密钥、但 Windows 已删除设备并重新索要旧式 PIN，固件会拒绝无效 PIN、只删除当前身份的失配密钥并立即重新开放配对；不清空其他模式。
- 新主机正常应走 SSP Just Works，不显示 PIN。若旧蓝牙适配器或驱动退回传统配对，兼容 PIN 固定为 `0000`；日志同时记录双方 SSP 支持状态与 Secure Connections 状态。
- 启动时只等待电脑 CDC 0.25 秒，电池供电切换模式不再为可选调试串口固定停顿 1.5 秒。
- 摇杆中心严格为 DualSense `128`，全行程可到 `0/255`；Switch 1 为 12 位 `0/4095`。

## 普通震动

- Xbox、PS5 和 Edge：普通强弱分量同时送到 Pro2 两侧执行器；左右扳机贡献仍保留在各自一侧。
- 停止或单侧无振动时发送 Pro2 可识别的合法静默频率帧，不再发送五个全零字节；编码与项目中已验证的 ESP 普通震动链路保持一致。
- Switch 1：分别解析 Nintendo 左右 HD 震动包的幅度编码，再按左右位置降级为 Pro2 普通双通道震动。
- 等效的重复震动状态只刷新命令寿命，不重复写 Pro2 USB。r19 实机 Switch 验证中，889 次主机更新有 887 次被合并，Pro2 侧仅生成 4 次必要报告，提交和传输错误均为 0。
- 主机明确发送停止、无线断开或切换模式时发送停止帧，避免残振；游戏震动状态本身不再被固定时长截断。
- 不创建 DualSense 音频端点，不宣称 HD haptic。

可直接测试实体 Pro2 普通震动：

```text
rumble test
rumble status
rumble stop
```

`rumble test` 是绕过无线主机输出的实体执行器测试。若它能震，说明 Pico -> Pro2 USB OUT 正常；若 Steam 测试不震，再查看 `[BT_CLASSIC_OUTPUT]`：

- 没有该行：主机没有向模拟手柄发送普通震动报告；
- 有该行但 `weak=0 strong=0`：主机选择了音频触觉或停止状态，本固件没有可转换的普通震动源；
- 数值非零且 `rumble status` 的 `reports` 增长：无线解析和 USB OUT 均已执行；若实体仍不震，应继续核对 Pro2 固件接受的输出报告。

Xbox 模式按协议不提供陀螺仪。PS5、Edge 和 Switch 1 模式均携带真实 Pro2 IMU；可用 `imu status` 检查零偏校准状态。

## 原型接线

接线前断开所有 USB 电源。线色不一定可靠，优先按 USB-A 母座针脚或万用表确认。

| USB-A 母口 | Pico 2 W | 物理针脚 |
|---|---|---:|
| VBUS / 5V | VBUS | 40 |
| D+ | GP0 | 1 |
| D- | GP1 | 2 |
| GND | GND | 3 |

注意：

- 不要把 VBUS 接到 `3V3` 或 `VSYS`。
- Pico 必须从 Micro-USB、VBUS 或 VSYS 获得电源；作为 USB Host 时，还必须由 Pico 一侧向 Pro2 的 VBUS 提供 5V。
- 不要同时向 VBUS 接入另一组独立 5V。
- D+、D- 建议各串联 22 欧姆电阻，短距离绞合。
- 母口到 Pro2 的 Type-C 线必须支持数据。

## 刷入与配对

1. 先断开 Pro2。
2. 按住 `BOOTSEL`，连接 Pico Micro-USB，随后松开。
3. 向 Pico 2 W 的 `RP2350` 磁盘写入 r38 UF2。不要刷入 Pico 1 / RP2040。
4. Pico 重启后，打开 `Pico 2 W Pro2 Bridge Debug` 串口。
5. 接入 Pro2，等待十步 USB 初始化完成。
6. 用 `mode ...` 选择身份，Pico 会重启。
7. 首次使用该身份时在目标主机完成一次配对；已有有效配对时会自动重连。

若主机长时间不回应，Pico 会进入低频后台重连。只有主机显示重复身份、明确提示密钥错误，或需要更换目标主机时，才使用 `wireless forget` 后重新配对。

当前身份与可用命令：

```text
mode
version
help
wireless forget
bootsel
```

`wireless forget` 只清除当前模式的配对资料。原生 Pro2 模式需要让 Pico 完整释放 Pro2 的 USB 主机关系：Rev B 板由 `GP3` 控制的 5 V 负载开关自动完成；直连 VBUS 的原型必须使用实体开关或拔线。

## 验收状态

r38 的协议实现须通过完整主机侧自动测试：

- Pro2 输入解析与普通震动打包；
- DualSense / Edge 描述符、报告长度、CRC、特征校准、摇杆端点；
- PS5 / Edge IMU 轴序、方向与量纲；
- Edge GL/GR 独立背键；
- Switch 1 报告、子命令回复、三组真实 IMU 样本与震动降级；
- Pro2 组合键映射、1.5 秒长按、断线取消和防重复触发。
- 重新配对组合键映射、2 秒长按、模式键互斥、断线取消和防重复触发。

r19 曾完成四模式实机回归；r26 调整 Classic 首次配对失败恢复，r28 新增显式重新配对入口，r29 修复快速 USB 枚举后的状态清零，r30 增加单主机状态、统一摇杆门限和 NS1 震动诊断，r34 修正启动延迟、主动回连退避、ACL 半连接和失配密钥恢复，r35 再固定 SSP 自动确认与传统 PIN 诊断。因此连接耗时、失败后再次可发现、配对反馈和实体震动仍须做最终成品实机验收：

- PS5、Edge 均使用保存密钥在第一次尝试连接成功，未发现第二主机抢占；
- Xbox BLE 订阅成功，`7.5 ms` 连接间隔下稳定通知且发送错误为 0；
- Switch 1 使用保存密钥在第一次尝试连接成功，稳态精确 `66.7 Hz`；
- 四模式的 Pro2 USB 输入源均稳定为 `250.0 Hz`，解析拒绝和重复源帧均为 0；
- r22 起提供分层震动诊断，不能把“USB OUT 已提交”等同于实体执行器已验证；以 `rumble test` 和实际游戏结果为准。
- r38 裸板启动时必须稳定看到 CDC 串口，日志应依次出现 `native_usb_ready`、`pro2_state_ready_before_pio_usb`、`pio_usb_host_init_done`、`cyw43_init_done` 和 `wireless_backend_init_done`；`version` 必须返回 `firmware_revision=r38` 和 `runtime_ready=true`。
- 新主机配对优先出现 `io_capability_request ... ssp_path=confirmed`；若出现 `legacy_pin_request`，日志必须包含 `ssp_both_sides` 与 `secure_connections_active`，Windows 端只输入 `0000`。
- `BT_CLASSIC_POLICY` 必须显示 `incoming_first_ms=500 active_timeout_ms=6000 background_ms=30000 fast_attempts=1`；无响应主机不应让身份反复处于不可发现状态。
- Windows 删除当前身份但 Pico 保留密钥时，重新连接应出现 `reason=stale_local_bond`，并在同一轮清除当前失配密钥、重新开放配对。
- `wireless status` 必须返回 `max_active_hosts=1 simultaneous_multi_mode=off`。
- `input status` 应显示中心 `2048`、死区 `64`、满量程行程 `1600`，物理门限能映射到协议端点。
- NS1 模式的 `rumble test switch` 必须解码出左右 `255` 并形成非零 Pro2 USB OUT 命令。
- Classic 首次配对若失败，日志必须出现 `BT_CLASSIC_RECOVERY pairing_reopened`，设备应保持可再次搜索，无需断电重启。

自动测试和本次实机记录仍不能替代所有主机、系统版本和游戏的兼容性验收。HD 震动、DualSense 音频端点、耳机和麦克风不在本固件能力范围内。

## 本地构建

```powershell
powershell -ExecutionPolicy Bypass -File firmware\pico2w_pro2_wireless_bridge\build_r6.ps1 -Revision r38 -Clean
```

输出目录：

```text
release\pico2w-pro2-wireless-test-r38
```
