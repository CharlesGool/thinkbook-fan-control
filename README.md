# ThinkBook 风扇控制

通过联想 BIOS WMI 接口 `LENOVO_OTHER_METHOD`（`root\WMI`）**按 RPM 精确设定每个风扇的转速**，支持预设模式、固定转速和自定义温度曲线；同时显示 CPU 频率 / 占用 / 实时功耗 / PL1·PL2，可以设定 PL1 / PL2 / Tau、按电源模式和是否插电限制大小核频率，并把数据提供给 HWiNFO。

单个 exe，无需安装，只依赖 Windows 自带的 .NET Framework 4.x。功耗读取 / PL 设置和旧机型的 EC 控制需要 [PawnIO](https://pawnio.eu) 驱动，程序内置了官方安装程序，可一键安装（见下文）。

![ThinkBook 风扇控制截图：性能模式下的温度曲线和最近 5 分钟的转速 / 温度记录](docs/screenshot.webp)

## 使用

1. 从 [Releases](../../releases) 下载 `ThinkBookFanControl.exe`，或按下文自行编译
2. 双击运行（需要管理员权限，会弹出 UAC）
3. 选择模式：安静 / 均衡（系统默认）/ 性能 / 全速 / 固定转速 / 自定义温度曲线
4. 需要常驻的话勾选「开机自动启动」，关闭窗口后程序留在托盘

> **免责声明**：本程序直接改写 EC 的风扇目标转速和 CPU 功耗限制。不当设置（尤其是低速 / 停转、过高的 PL）可能导致硬件过热，请自行承担风险。程序内置高温保护和守护进程，但不能替代你对温度的留意。

## 适用机型

程序启动时按下面的顺序自动选择第一个可用的接口，「安全与常规」里会显示本机型号和正在使用的接口。

| 机型 | 接口 | 功能 | 状态 |
|---|---|---|---|
| ThinkBook 14+ 2025（ThinkBook 14 G7+ IAH，21TK） | 联想 BIOS WMI | 按 RPM 设定、读取转速 | ✅ 已验证 |
| ThinkBook 14+ / 16+ 2024、16+ 2025 等有 `LENOVO_FAN_TEST_DATA` 的机型 | 联想 BIOS WMI | 同上 | ❓ 未测试，可能适用 |
| ThinkBook 14+ / 16+ 2022–2023（G4+ IAP / G5+） | EC PWM 寄存器（需 PawnIO） | 按 % 占空比设定（每秒重写），无转速读数 | 🧪 实验性，未验证 |
| ThinkBook 14p / 16p 2021–2022（G2 ACH / G3 ARH） | EC 转速寄存器（需 PawnIO） | 按 RPM 设定、读取转速 | 🧪 实验性，未验证 |
| 其他带 `\\.\EnergyDrv` 的联想笔记本 | EnergyDrv（联想电源管理驱动） | 只有 自动 / 全速 | 🧪 实验性 |

EC 寄存器接口只在 `Win32_ComputerSystem.SystemFamily` 匹配上述机型、并且 EC 芯片 ID 像 ITE EC 时才会启用，未知机型不会写 EC。未验证机型请先在「固定转速」下确认实际转速能跟随目标，再使用曲线和低速功能。

**诊断信息**：在管理员命令行运行下面的命令，会生成只读的诊断报告（不改变风扇和功耗设置），反馈兼容性时请附上：

```
ThinkBookFanControl.exe --diag report.txt
```

## CPU 监控与 PL1 / PL2

| 项目 | 来源 | 需要 |
|---|---|---|
| 频率 | 性能计数器 `Processor Information(_Total)`：`Processor Frequency × % Processor Performance` | — |
| 占用率 | 性能计数器 `% Processor Utility`（与任务管理器一致） | — |
| 实时封装功耗 | Intel RAPL：MSR `0x611` 能量计数器的差值 ÷ 时间（单位来自 `0x606`） | PawnIO |
| PL1 / PL2 读取 | MSR `0x610`（另外读取 MCHBAR + `0x59A0` 的 MMIO 副本，仅显示） | PawnIO |
| PL1 / PL2 / Tau 设置 | 写 MSR `0x610`（PL1 bits 14:0、Tau bits 23:17、PL2 bits 46:32）；BIOS 设置了锁定位（bit 63）时只能读取，界面显示「不支持设置」 | PawnIO + 未锁定的 BIOS |
| PL1 / PL2（联想 WMI） | `LENOVO_CAPABILITY_DATA_01` 列出 `0x0102FF00` / `0x0101FF00` 的机型（Legion 风格）；ThinkBook 14+ 2025 不提供 | 「自定义」性能模式 |

- 打开「自定义 PL1 / PL2」后，程序会持续保持设定值（BIOS、Intel DTT、Fn+Q 改回时会再次写入）；关闭选项或退出程序时把 MSR `0x610` 原样写回接管前的值（包括 Tau 和使能位）。
- Tau（PL1 持续时间）只能取 2^Y×(1+F/4) 形式的值，程序会取最接近的（如 28 / 32 / 56 / 64 / 96 / 128 秒），界面显示 CPU 实际使用的值。
- 联想进程管理服务在本机的设置是 `Policy_DisablePLx = 1`，不会改 PL，所以 MSR 写入不会被它改回。
- 实测（ThinkBook 14+ 2025，Core Ultra 9 285H，平衡模式插电）：
  - 原值：MSR PL1 25 W / PL2 35 W / Tau 8 秒，MMIO PL1 53–58 W（由 Intel DTT 动态调整）/ PL2 75 W。这里 MSR 那组更低，所以写 MSR 直接生效；PL1 设到 MMIO 值以上则以 MMIO 为准。
  - 写入 40 / 60 W、Tau 56 秒后保持不变，没有被 BIOS / DTT / 联想服务改回。
  - PL1 = PL2 = 20 W 时满载封装功耗稳定在 20.0 W。
  - PL1 35 W / PL2 50 W / Tau 8 秒时，满载先以 50 W 运行约 3 秒，然后回落并稳定在 35 W。
  - 恢复后 MSR `0x610` 与接管前逐位一致。
- CPU 实际按 MSR 和 MMIO 两组限制中**较低**的一组生效。如果 MMIO 值更低（例如 Intel DTT 在调节），只改 MSR 不会提高实际功耗上限；PawnIO 的 MCHBAR 模块是只读的，本程序不改 MMIO。
- AMD 机型目前不支持功耗读取和 PL 设置。

## 大小核频率上限

按「电源模式（最佳能效 / 平衡 / 最佳性能）× 插电 / 电池」分别设定 P 核和 E 核的最高频率（MHz，0 = 不限制）：

| | 写入位置 |
|---|---|
| P 核 | `PROCFREQMAX1`（效率等级 1） |
| E 核 + 低功耗 E 核 | `PROCFREQMAX`（效率等级 0；Windows 不能单独限制 LP E 核） |
| 平衡 | 当前电源计划 |
| 最佳能效 / 最佳性能 | 对应的电源模式覆盖层（`961cc777…` / `ded574b5…`），覆盖层的值优先于电源计划（Windows 默认在「最佳能效 + 电池」下限制 3000 MHz） |

每种情况的插电 / 电池值都写进去，切换电源模式或插拔电源时由 Windows 自己换用对应的值。程序每 5 秒检查一次，被改回就重新写入；关闭选项或退出时恢复原值。实测把 P 核限制到 2000 MHz 后，满载时 P 核频率从 2900 MHz 降到约 2000 MHz。

**联想驱动会改回这些值**：联想进程管理服务（`LenovoProcessManagement` / `LNVDispatcherService`）会按场景 / 电量动态改写频率上限、EPP 和睿频，实测在 1–3 秒内就把写入的上限改回 4800 / 5000 MHz。它的注册表设置 `HKLM\SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider\Policy_DisablePPMCtrl` 设为 `1` 并重启服务后，实测不再改回。打开频率限制时程序会询问是否这样设置（也可以单独勾选「不让联想进程管理服务调节频率」）；这是系统设置，退出程序后仍保持，取消勾选恢复原值。如果仍被改回，界面会显示「1 分钟内被其他程序改回 N 次」。

联想默认在电池下关闭睿频（`Policy_DisableDCTurbo = 1` → `PERFBOOSTMODE` 电池值 = 0），此时高于基础频率的电池上限不起作用；勾选「电池供电时也允许睿频」会把插电时的睿频模式复制到电池（取消勾选恢复）。

## PawnIO 驱动

读取 CPU 功耗、设置 PL1 / PL2、以及 2021–2023 旧机型的 EC 控制都需要访问 MSR / I/O 端口，必须通过内核驱动。本程序使用 [PawnIO](https://pawnio.eu)：签名驱动，**兼容「内存完整性」（HVCI）**，只运行签名的、带白名单的小模块（IntelMSR 只允许写 `0x610` 等少数 MSR；LpcIO 只允许访问 Super I/O 端口；LpcACPIEC 只允许 `0x62/0x66`）。WinRing0 / inpout 已被微软易受攻击驱动列表和 Defender 拦截，不再使用。

**内置安装**：exe 里内置了 PawnIO 官方安装程序 `PawnIO_setup.exe` 2.2.0（namazso 签名，SHA-256 `1F519A22…3032`，官方允许转发未修改的安装程序）。没装或版本较旧时，「CPU 功耗限制」里会出现「安装 PawnIO 驱动」按钮，点击后以 `-install -silent` 静默安装，然后重启本程序即可；旧机型（EC 风扇控制）启动时也会直接询问是否安装。也可以手动安装：[pawnio.eu](https://pawnio.eu) 或 `winget install namazso.PawnIO`。

驱动本身必须作为系统设备安装（`Root\PawnIO`），程序不会自己释放 `.sys` 或创建服务。所需模块（`IntelMSR` / `IntelMCHBAR` / `LpcIO` / `LpcACPIEC`）已内置在 exe 里（来自 [namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) 0.2.11 的签名版本，LGPL-2.1，见 `res/pawnio/COPYING`）。没有安装 PawnIO 时，这些功能显示「--」/「不支持」，风扇控制（WMI 机型）照常工作。

**和 ThrottleStop 的区别**：ThrottleStop 不需要安装，是因为它每次运行时加载自带的 `ThrottleStop.sys`；这个驱动能任意读写物理内存（CVE-2025-7771，已被勒索软件用来关闭杀毒软件），所以它能写 MCHBAR 里的 MMIO 功耗限制（「MMIO Lock」「Sync MMIO」）。PawnIO 只允许写 MSR `0x610` 等少数寄存器，因此本程序只能改 MSR 那一组 PL，无法锁定 / 同步 MMIO；如果 MMIO 的值更低，以较低者为准（界面会同时显示两组值）。

## HWiNFO

勾选「在 HWiNFO 中显示」后，程序每秒把数据写入 HWiNFO 的自定义传感器注册表项
`HKCU\Software\HWiNFO64\Sensors\Custom\ThinkBook Fan Control`，HWiNFO 的传感器窗口里会多出一组「ThinkBook Fan Control」：

`Fan 1` / `Fan 2`（实际转速）、`Fan N Target`（目标，0 = 自动或停转）、`CPU`（温度）、`CPU Package Power`、`CPU PL1`、`CPU PL2`、`CPU Effective Clock`、`CPU Usage`

HWiNFO 只在启动时读取传感器列表，第一次打开这个选项后需要重启 HWiNFO。取消勾选会删除这些注册表项。这些值同样会进入 HWiNFO 的共享内存，RTSS / Rainmeter / FanControl 的 HWiNFO 插件等都能读到。

## 原理

| Feature ID | 读 | 写 |
|---|---|---|
| `0x0403_00XX` | 风扇 XX 当前转速 (RPM) | 目标转速 (RPM，100 步进)；`0` = 交还 EC 自动控制 |
| `0x0504_0000` | CPU 温度 (°C) | — |

风扇列表与额定转速上下限来自 `LENOVO_FAN_TEST_DATA`（已验证机型：ID 1 为 1800–5500 RPM，ID 4 为 1700–5700 RPM）；`LENOVO_CAPABILITY_DATA_00` 里没有「可写」位的风扇不会被控制。实测低于额定最低转速时：

| 写入 | 实际 |
|---|---|
| 1500 | 1400–1600，稳定 |
| 1000 | 800–1300，可用但有波动 |
| ≤ 500（含 1） | 风扇停转 |

默认把目标限制在额定范围内；打开「允许低于额定最低转速 / 停转」后，可设 1000 RPM 以上或 0 = 停转（写入 `1`），且仅在温度低于设定值（默认 50°C）时生效，超过即提回额定最低转速。

旧机型的 EC 寄存器（经 PawnIO 访问 ITE EC 的 D2EC 窗口 `0x4E/0x4F`，以及 ACPI EC `0x62/0x66`）：

| 机型 | 写入 | 读取 |
|---|---|---|
| ThinkBook 14+/16+ 2022–2023 | `0x1804` / `0x1805`：PWM 占空比 0–127（EC 会改回，需每秒重写；停止重写即交还 EC） | CPU 温度：ACPI EC `0x07` |
| ThinkBook 14p/16p 2021–2022 | `0xC83C` / `0xC83D`：目标转速 ÷ 100（`0` = EC 自动） | 转速 `0xC830..0xC833`，DTS 温度 `0xC41C` |

与 [jiarandiana0307/Lenovo-Fan-Control](https://github.com/jiarandiana0307/Lenovo-Fan-Control) 的区别：后者使用 `\\.\EnergyDrv` 的“除尘”功能，只能在“正常 / 全速”之间切换，无法设定具体转速（本程序把它作为最后的兼容接口）。

## 功能

- 模式：安静 / 均衡（系统默认，EC 控制）/ 性能 / 全速 / 固定转速 / 自定义温度曲线
- 预设曲线（内置只读，可一键复制为新的自定义曲线方案再修改）：

  | 温度 | 安静 | 性能 |
  |---|---|---|
  | 40°C | 最低（允许低速时 ≤45°C 停转） | 2400 |
  | 55°C | ~1870 | 3000 |
  | 65°C | 2000 | 3800 |
  | 75°C | 2600 | 4600 |
  | 85°C | 3600 | 最高 |
  | 92°C | 最高 | 最高 |
- 多套自定义曲线方案：新建 / 另存为 / 重命名 / 删除，托盘菜单「曲线方案」可直接切换
- 曲线编辑器：拖动点、双击添加、右键删除；可两风扇共用一条曲线或分别设置
- 温度平滑 + 降温回差，避免转速来回抖动（自定义曲线和安静 / 性能模式共用）
- **平滑调速**：目标转速按设定速率（默认升速 300 RPM/秒、降速 150 RPM/秒）逐步变化，不再突然加速 / 减速；高温保护时直接全速
- **CPU 监控**：频率、占用率、实时封装功耗、PL1 / PL2；功耗曲线和温度一起显示在历史图里
- **PL1 / PL2 / Tau 设置**（需 PawnIO，见上文）
- **大小核频率上限**：按电源模式 × 插电 / 电池分别限制 P 核 / E 核，处理联想进程管理服务的改写（见上文）
- **HWiNFO 自定义传感器**（见上文）
- 高温保护：达到设定温度两风扇全速，回落 5°C 后恢复
- 连续读不到温度时，曲线类模式自动交还 EC
- 退出、注销/关机、程序异常时自动交还 EC；从睡眠唤醒后重新下发目标
- 守护进程（同一 exe 以 `--watchdog <pid>` 启动）：主程序退出、崩溃或被任务管理器结束后，立即把风扇交还 EC
- 托盘常驻，托盘图标跟随系统深色 / 浅色任务栏自动切换颜色；开机自启（计划任务，最高权限运行，免 UAC，电池供电也启动）
- 设置保存在 `%AppData%\ThinkBookFanControl\settings.ini`（自动迁移旧版 `LenovoFanCurve` 的设置和开机自启任务）

> ⚠ 如果主程序和守护进程同时被结束（例如“结束进程树”），EC 会保持最后一次写入的转速，直到重新运行程序或重启电脑。守护进程不恢复 PL1 / PL2：程序被强制结束时，设定的功耗限制保持到重启（BIOS 会重新设定）。

## 编译

只需 Windows 自带的 .NET Framework 4.x：运行 `build.cmd`，输出 `bin\ThinkBookFanControl.exe`（需要管理员权限运行）。

图标由 `src\FanIcon.cs` 用代码绘制（托盘图标运行时生成），运行 `tools\make-icon.cmd` 重新生成 exe 图标 `res\app.ico`。

## 参考

- [jiarandiana0307/Lenovo-Fan-Control](https://github.com/jiarandiana0307/Lenovo-Fan-Control)、[FanControl.LenovoPlugin](https://github.com/jiarandiana0307/FanControl.LenovoPlugin)：EnergyDrv 全速 / 正常切换及保持全速的方法
- [markgoo/Thinkbook14PAutoFanController](https://github.com/markgoo/Thinkbook14PAutoFanController)：ThinkBook 14p 的 ITE EC 寄存器
- TB FanFun V2.6：ThinkBook 14+ 2022 的 EC PWM 寄存器
- [Gunther8/lenovo-fan-control](https://github.com/Gunther8/lenovo-fan-control)：`LENOVO_GAMEZONE_DATA.SetSmartFanMode`，面向联想台式机，切换的是整机性能模式而非风扇转速，未纳入
- [BartoszCichecki/LenovoLegionToolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit)、Linux `lenovo-wmi-other`：联想 WMI 功耗限制的能力 ID
- [namazso/PawnIO](https://github.com/namazso/PawnIO)、[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)：PawnIO 的调用方式

## 反馈

如果你在其他机型上测试过（能用或不能用），欢迎提 Issue，附上 `--diag` 生成的诊断报告，或者机型（「安全与常规」里显示的型号）和 `LENOVO_FAN_TEST_DATA` 的输出。

## 许可证

[MIT](LICENSE)。内置的 PawnIO 模块为 LGPL-2.1（[源码](https://github.com/namazso/PawnIO.Modules)）。
