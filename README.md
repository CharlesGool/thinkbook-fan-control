# ThinkBook 风扇控制

通过联想 BIOS WMI 接口 `LENOVO_OTHER_METHOD`（`root\WMI`）**按 RPM 精确设定每个风扇的转速**，支持预设模式、固定转速和自定义温度曲线。

单个 exe，无需安装，只依赖 Windows 自带的 .NET Framework 4.x。

## 使用

1. 从 [Releases](../../releases) 下载 `ThinkBookFanControl.exe`，或按下文自行编译
2. 双击运行（需要管理员权限，会弹出 UAC）
3. 选择模式：安静 / 均衡（系统默认）/ 性能 / 全速 / 固定转速 / 自定义温度曲线
4. 需要常驻的话勾选「开机自动启动」，关闭窗口后程序留在托盘

> **免责声明**：本程序直接改写 EC 的风扇目标转速。不当设置（尤其是低速 / 停转）可能导致硬件过热，请自行承担风险。程序内置高温保护和守护进程，但不能替代你对温度的留意。

## 适用机型

| 机型 | 状态 |
|---|---|
| ThinkBook 14+ 2025（ThinkBook 14 G7+ IAH，21TK） | ✅ 已验证 |
| ThinkBook 14+ 2024 | ❓ 未测试，可能适用 |
| ThinkBook 16+ 2024 | ❓ 未测试，可能适用 |
| ThinkBook 16+ 2025 | ❓ 未测试，可能适用 |

程序启动时会显示本机型号，并标明是否为已验证机型。未验证机型请先在「固定转速」下确认实际转速能跟随目标，再使用曲线和低速功能。

判断是否支持（管理员 PowerShell）：

```powershell
$om = Get-CimInstance -Namespace root/wmi -ClassName LENOVO_OTHER_METHOD
(Invoke-CimMethod -InputObject $om -MethodName GetFeatureValue -Arguments @{ IDs = [uint32]0x04030001 }).value  # 风扇1 转速
Get-CimInstance -Namespace root/wmi -ClassName LENOVO_FAN_TEST_DATA                                                # 风扇列表与额定转速
```

## 原理

| Feature ID | 读 | 写 |
|---|---|---|
| `0x0403_00XX` | 风扇 XX 当前转速 (RPM) | 目标转速 (RPM，100 步进)；`0` = 交还 EC 自动控制 |
| `0x0504_0000` | CPU 温度 (°C) | — |

风扇列表与额定转速上下限来自 `LENOVO_FAN_TEST_DATA`（已验证机型：ID 1 为 1800–5500 RPM，ID 4 为 1700–5700 RPM）。实测低于额定最低转速时：

| 写入 | 实际 |
|---|---|
| 1500 | 1400–1600，稳定 |
| 1000 | 800–1300，可用但有波动 |
| ≤ 500（含 1） | 风扇停转 |

默认把目标限制在额定范围内；打开「允许低于额定最低转速 / 停转」后，可设 1000 RPM 以上或 0 = 停转（写入 `1`），且仅在温度低于设定值（默认 50°C）时生效，超过即提回额定最低转速。

与 [jiarandiana0307/Lenovo-Fan-Control](https://github.com/jiarandiana0307/Lenovo-Fan-Control) 的区别：后者使用 `\\.\EnergyDrv` 的“除尘”功能，只能在“正常 / 全速”之间切换，无法设定具体转速。

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
- 高温保护：达到设定温度两风扇全速，回落 5°C 后恢复
- 连续读不到温度时，曲线类模式自动交还 EC
- 退出、注销/关机、程序异常时自动交还 EC；从睡眠唤醒后重新下发目标
- 守护进程（同一 exe 以 `--watchdog <pid>` 启动）：主程序退出、崩溃或被任务管理器结束后，立即把风扇交还 EC
- 托盘常驻，托盘图标跟随系统深色 / 浅色任务栏自动切换颜色；开机自启（计划任务，最高权限运行，免 UAC，电池供电也启动）
- 设置保存在 `%AppData%\ThinkBookFanControl\settings.ini`（自动迁移旧版 `LenovoFanCurve` 的设置和开机自启任务）

> ⚠ 如果主程序和守护进程同时被结束（例如“结束进程树”），EC 会保持最后一次写入的转速，直到重新运行程序或重启电脑。

## 编译

只需 Windows 自带的 .NET Framework 4.x：运行 `build.cmd`，输出 `bin\ThinkBookFanControl.exe`（需要管理员权限运行）。

图标由 `src\FanIcon.cs` 用代码绘制（托盘图标运行时生成），运行 `tools\make-icon.cmd` 重新生成 exe 图标 `res\app.ico`。

## 反馈

如果你在其他 ThinkBook 机型上测试过（能用或不能用），欢迎提 Issue，附上机型（「安全与常规」里显示的型号）和 `LENOVO_FAN_TEST_DATA` 的输出。

## 许可证

[MIT](LICENSE)
