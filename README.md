# TinyMouseMacro · 多屏切换与操作录制

Windows 轻量键鼠宏。核心流程是保存“屏幕上的目标位置 + 快捷键”，以及录制操作之间的真实时间间隔。

## 日常使用

1. 左侧选择或新建方案。现有 `上 / 右 / 中` 等方案直接兼容。
2. 在显示器地图上选择位置，或点“3 秒后取点”后把鼠标放到目标上。地图和取点只编辑坐标，不会自动移动或点击。
3. 设置快捷键。推荐组合键或专用键（F13–F24）。避免将常用字母设置为单键触发。
4. “运行方案”立即回放；停止按钮或 `Ctrl+Alt+F10` 停止；`Ctrl+Alt+P` 暂停／恢复快捷键与定时触发。

更改会在短暂停顿后自动保存，也可按 `Ctrl+S`。输入快捷键期间暂不触发方案，离开输入框后启用。关闭窗口会留在托盘，托盘菜单可退出。

## 录制与编辑

- “录制操作”倒计时 3 秒后开始，追加到当前方案。再次点击或 `Ctrl+Alt+F10` 停止。
- 记录鼠标移动、左／右／中键点击、滚轮及动作间隔；勾选“录制键盘”才录制键盘，组合键保留修饰键。
- 本程序窗口中的操作、自家回放注入、停止／暂停组合键不加入录制。录制达到 100,000 步会自动停止并提示。
- 当前录制是离散动作记录，不保留按键按住时长或连续拖拽轨迹。需要拖拽时在“添加动作”中添加拖拽步骤。
- 双击动作编辑坐标、时长或参数，支持复制、删除、拖动排序；新录制动作可以编辑“录制间隔（毫秒）”。
- “运行参数”提供重复、速度、定时和目标窗口。定时从首次检查后等待完整间隔，不会一启动就运行。复制和导入方案默认停用，确认快捷键后再启用。
- “添加动作”保留已有 20 种动作。步骤编号在界面从 1 开始；像素跳转填 0 表示不跳转。

## 全局键鼠回放（默认启用）

所有方案统一使用系统热键、键盘监听和后台输入补充，无需额外设置。旧方案的名称、快捷键、步骤、坐标和时间间隔照常使用。后台输入无法拦截传给远端的同一按键，建议选择不与远端业务动作冲突的组合键。

鼠标点击、键盘按键、组合键和文本输入在同一条回放流程中执行：

- 跨窗口点击前先交接焦点并确认稳定，再确认鼠标到位，最后执行一次点击。因捕获卡在边框时，内部仅重试焦点请求和移动，不重放点击，也不注入试探用的 Ctrl+Alt。
- 这套处理适用于多步宏、录制和循环；鼠标仍在客户机画面内时，保持对应远程窗口焦点，让远程软件接收后续输入。普通录制中的悬停移动不激活后台窗口；从远程窗口移出或移出捕获范围时自动交接。
- 后续键盘输入核对当前目标，焦点被其他窗口抢走时停止并记录原因；防止把按键误发到其他应用。点击或按键打开的同应用对话框会跟随。显式 Alt+Tab / Win+D 等切换动作允许更换目标。
- 普通按键使用 `SendInput` 扫描码（扩展键带扩展标志），不支持扫描码的键保留虚拟键方式。文本输入使用 Unicode；远端程序是否接受 Unicode 输入取决于远程软件及应用。
- 回放前等待触发修饰键松开，点击和键盘输入前再次检查；取消会释放本程序已按下的键和鼠标按钮。
- 焦点、捕获与位置检查的等待不随回放速度压缩。正常切窗额外约 220 毫秒；异常时有界重试，后续动作等交接结束再继续，实际回放可能比录制稍慢。

坐标始终是宿主显示器坐标；在 VMware / UU 画面内操作，是由远程软件把宿主模拟输入转发到对应系统。它不是在三台系统中各装一个驱动，也不能越过 USB 直通、驱动独占或安全桌面。宿主收不到物理按键，或远程软件拒绝转发模拟输入时，用户态程序无法保证生效。没有安装服务、驱动或修改 VMware / UU 配置。

## 疑难诊断与高级工具

“疑难诊断”显示最近触发来源和活动日志。“临时只检测”仅用于确认是否收到快捷键，此时停止当前运行且不触发宏或定时任务；切换页签或关闭到托盘自动恢复正常执行。切到其他软件进行按键测试时保留诊断状态，诊断状态不保存，重新启动也恢复执行。自动监听恢复不需要手动干预，“重置输入监听”只在排查异常时使用。

后台输入默认启用。VMware 进入或新一轮捕获时会有界重连键盘监听，每轮最多两次，不循环争抢；这个实现细节不再提供日常开关。诊断只记录配置过的快捷键及其主键不匹配信息，不记录普通打字内容。

高级工具默认折叠，保留可选的外部切屏启动器：生成当前方案的 exe，将其分配给 iCUE 的 **Launch App / 启动应用**，再开启“允许外部启动器触发切屏”。这不是驱动模式或正常运行的前置条件。每个 exe 的文件名含方案 ID，主程序须运行，方案须启用。

外部入口只允许同一桌面本地程序触发重复一次、无指定窗口的一步移动／左键点击，不接受任意程序、脚本或网络命令。启动器确认的是“收到触发”，不是“成功切回”。错误记录在 `%LOCALAPPDATA%\TinyMouseMacro\launcher-last-error.txt`，执行结果在主程序最近活动中。

iCUE 的宏在客户机可用，不等于其 Launch App 在捕获时也一定启动宿主程序，仍需实际验证。具体差别可能来自板载映射、驱动或输入方式，不能仅凭宏能用判定两者覆盖相同。

### 本机验收

1. 从原安装位置启动程序即可（本机为桌面上的 `TinyMouseMacro.exe`）。
2. 在 VMware 和 UU 中各按一次切回快捷键，确认一次即到达目标；再测试“切换窗口 → 按键”的多步方案。
3. 若异常，在“疑难诊断”的最近活动中区分未收到触发、焦点交接失败、位置被限制和输入发送失败。只有收到快捷键不能证明实际切换成功。

## 数据与构建

- 配置：`%APPDATA%\TinyMouseMacro\macros.json`。外观与外部启动器偏好在同目录 `preferences.json`。
- 最近活动同时写入同目录的 `activity.log`，约 128 KB 后轮转到 `activity.log.previous`，最多两份。只记录方案操作、已配置快捷键的诊断与错误，便于定位间歇性问题；不记录普通键盘输入。
- 保留原有 JSON 顶层数组、字段与数字枚举。新增 `RecordedDelayMs`：缺省／null 保留旧回放节奏；录制步骤保存动作起始之间的间隔。
- 保存使用临时文件与原子替换，首次改写已有配置前生成 `macros.json.backup`。升级前仍建议保留原 exe 与配置副本。
- .NET 8 Windows Forms。应用继续使用原有管理员清单；没有安装服务、驱动或改变系统输入配置。

```powershell
./build-release.ps1
```

统一用此脚本发布。它读取开机自启项的现有 exe 路径（本机为 `C:\Users\Wes\Desktop\TinyMouseMacro.exe`），先在临时目录编译和自测，成功后停止该路径下的运行实例，再原位更新主程序、配套启动器和 `TinyMouseMacro.README.md`。发布后校验哈希，将临时目录移入回收站，保留自启项和用户数据；不会自动重启应用。

若旧程序以管理员权限运行，请从管理员 PowerShell 执行脚本；权限不足会在覆盖前停止，不自动弹 UAC。自启项不存在且桌面也没有已有安装时，用 `-TargetExe '原安装目录\TinyMouseMacro.exe'` 明确原路径。协作约束见 [AGENTS.md](AGENTS.md)。

`--self-test` 不显示窗口、不注册全局热键、不安装钩子、不注入键鼠输入；使用合成数据生成离屏 UI 预览并验证旧格式、时间、取消、组合键状态与编辑行为。它不能替代 VMware / UU 的物理键盘测试。

## 设计与兼容性资料

- [PowerToys Mouse Jump：显示器地图与键盘选择](https://learn.microsoft.com/en-us/windows/powertoys/mouse-utilities)
- [PowerToys Mouse Without Borders：跨机器切换与全屏行为](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders)
- [Windows RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [低级键盘监听：线程、超时与 Raw Input 建议](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
- [Raw Input INPUTSINK 与标志](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)
- [VMware 独占模式的输入限制](https://techdocs.broadcom.com/us/en/vmware-cis/desktop-hypervisors/workstation-pro/17-0/using-vmware-workstation-pro/using-virtual-machines-in-workstation-pro-user-guide/changing-the-virtual-machine-display/use-exclusive-mode.html)
- [UU 远程官方帮助](https://uuyc.163.com/help/)
- [iCUE 官方 Launch App 说明](https://help.corsair.com/hc/en-us/articles/16551343782797-iCUE-How-to-Open-Programs-with-Keybinds-Through-iCUE)
