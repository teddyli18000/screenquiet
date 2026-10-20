# ScreenQuiet

> A Windows utility that moves the desktop to a virtual display and restores the built-in panel when physical input is detected.

Fn+F9 将桌面切换到 Parsec 虚拟显示器，并把内置屏从 Windows 活动布局移除；识别到物理输入后恢复原布局。ScreenQuiet 通过联想 WMI 事件识别 Fn+F9，不改联想 Fn 服务、亮度或音量热键。正常运行时没有托盘图标、任务栏窗口或弹窗，也不提供联网功能。

这是一个实验性工具，参考设备为 **Lenovo ThinkBook16 G7+ IAH，Windows 11 x64 build 26200**。近期验证覆盖构建、被动启动、虚拟显示保活、空闲崩溃后的任务补拉，以及百应原启动器的客服命令拦截；本轮命令入口修订尚未重新进行真实 Fn+F9/UU 关屏操作。证据范围见[设计与验证记录](docs/design-and-validation.md)。

## 工作方式

- 监听 `root\WMI` 下的 `LENOVO_UTILITY_EVENT`，仅在 `PressTypeDataVal = 1` 时处理 Fn+F9。
- 进入关屏模式前保存当前显示布局，创建 Parsec 虚拟显示器，并把桌面切换到只含该虚拟显示器的 Windows 活动布局；内置屏从活动布局暂时移除。程序请求系统保持唤醒。
- 仅在关屏期间注册 Raw Input。键盘按下、鼠标移动或按键，以及触控板新增接触，可以请求恢复。输入来源必须能从 PnP 设备树识别为物理设备；`ROOT`、`SWD`、`UUYC` 来源和无法识别来源不会唤醒。程序不记录按键、文本、鼠标坐标或原始输入报告，只记录粗粒度状态和输入类别。
- 为抑制 Fn+F9 原本触发的联想百应界面，管理员步骤只为两个固定目标程序 `LenovoBaiyingLauncher.exe` 与 `LenovoBaiying.exe` 配置 IFEO 启动桥。桥直接阻断联想热键程序调用的 `LenovoBaiyingLauncher.exe -customerService`，在启动器通知百应前返回成功；其他命令连同原参数转发。

## 适用范围与限制

- 仅接受“单个内置屏”作为进入前的显示布局。有扩展屏、其他虚拟屏或其他程序正在改变显示目标时，会拒绝切换或尝试恢复并退出关屏模式。
- 唤醒要求物理键盘可被识别；只通过虚拟设备或未知设备输入的环境可能无法唤醒。部分远程软件若经物理设备路径注入，来源分类也无法保证识别。
- 切换完成后的前 0.9 秒会忽略输入，以免 Fn+F9 按键收尾立即触发恢复。
- IFEO 规则只匹配安装脚本指定的两个百应可执行文件的精确路径。安装时若找不到目标，或检测到已有对应 IFEO 设置，脚本会停止而不覆盖现有配置。
- 该匹配只接受启动器的单个 `-customerService` 参数，不依赖 WMI 事件时序。其他软件若显式使用同一客服命令，也会被抑制；普通手动打开和 `StartUp` 启动不匹配。联想后续更改命令或路径时需要重新核实。

## 构建与安装

需要 Windows x64、Windows PowerShell 和 .NET Framework 自带的 C# 5 编译器。构建会在项目目录生成 `ScreenQuiet.exe`、`LauncherBridge.exe` 和 `tests\ScreenQuietBridgeProbe.exe`。

1. 从 Parsec 官方地址单独下载并安装 [Parsec Virtual Display Driver 0.45.0.0](https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe)。本项目不附带或重新分发驱动。参考安装包的 Authenticode 签名验证有效，签名者为 Parsec Cloud, Inc.，SHA-256 为：

   ```text
   E23332448FDAF5AA017CB308DB5EF6855FAC526A7DED05D80C039404126D5362
   ```

   可在下载后自行复核：

   ```powershell
   Get-AuthenticodeSignature .\parsec-vdd-0.45.0.0.exe | Format-List Status,SignerCertificate
   Get-FileHash .\parsec-vdd-0.45.0.0.exe -Algorithm SHA256
   ```

2. 在项目目录运行 `Build.ps1`，例如：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
   ```

3. 在将使用 ScreenQuiet 的登录用户会话中运行 `Finish-Setup.ps1`：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Finish-Setup.ps1
   ```

   脚本先请求一次管理员确认，测试并配置百应启动桥，再为当前用户安装控制程序和计划任务。重复运行会在验证现有规则归属后更新受保护的桥程序；更新失败会恢复旧桥。只有配置或更新受保护启动桥需要管理员确认；计划任务以当前用户交互会话和有限权限运行，无需管理员权限或保存密码。安装会将旧的当前用户登录启动项迁移为计划任务。测试覆盖启动参数转发、客服命令拦截、不同路径隔离及被动启动检查；**安装流程不会自动执行关屏测试**。若系统中存在冲突的 IFEO 设置，脚本会停止并保留原配置。

## 使用与恢复

按 **Fn+F9** 切换关屏模式，再按一次可恢复。关屏期间，识别到的物理键盘、鼠标或触控板活动也会恢复显示。开始菜单中的 **ScreenQuiet - Restore screen** 可手动恢复；命令行也可运行安装目录中的 `ScreenQuiet.exe --restore`。

状态日志和恢复快照存放在 `%LOCALAPPDATA%\ScreenQuiet`。主程序退出或心跳中断超过 20 秒时，关屏期间启动的独立恢复进程会尝试还原已保存布局。当前用户计划任务在登录时启动控制程序，并每分钟检查一次；运行中的实例会阻止重复启动。任务启动失败时也配置了每分钟间隔的有限重试。任务不会唤醒睡眠中的电脑，错过的启动会在系统可用后补跑。计划任务负责登录启动和进程补拉，不是另一个常驻的守护进程。

要长时间暂停，先禁用自动启动任务，再请求程序恢复并退出：

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = "ScreenQuiet-$sid"
Disable-ScheduledTask -TaskName $task
& "$env:LOCALAPPDATA\Programs\ScreenQuiet\ScreenQuiet.exe" --stop
```

恢复自动启动并立即运行：

```powershell
Enable-ScheduledTask -TaskName $task
Start-ScheduledTask -TaskName $task
```

单独运行 `ScreenQuiet.exe --stop` 只会停止当前实例；若任务仍启用，下一次每分钟检查可能重新启动它。

卸载时运行当前用户安装目录中的 `Uninstall.ps1`。脚本会先禁用计划任务，再请求程序恢复显示并退出，然后删除该任务、快捷方式、程序和本项目的启动规则；系统规则清理需要管理员确认。独立安装的 Parsec 驱动会保留，可从 Windows“设置 → 应用”中单独卸载。卸载脚本不会清除 `%LOCALAPPDATA%\ScreenQuiet` 下的日志和状态文件。

## 参考与许可说明

- 虚拟显示驱动协议参考：[nomi-san/parsec-vdd](https://github.com/nomi-san/parsec-vdd)。相关上游许可文本见 [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt)。
- Lenovo WMI 热键事件参考：[ThinkBookToolkit](https://github.com/lhzlhz419/ThinkBookToolkit)。
- Parsec 驱动安装包由 Parsec 官方单独提供，适用其自身许可；本项目不分发驱动安装包。
- 本项目使用 [MIT License](LICENSE)。

验证记录和已知边界见[设计与验证记录](docs/design-and-validation.md)。
