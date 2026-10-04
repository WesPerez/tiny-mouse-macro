# TinyMouseMacro · 多屏切换与操作录制

Windows 轻量鼠标宏。核心流程是保存“屏幕上的目标位置 + 快捷键”，以及录制操作之间的真实时间间隔。

## 日常使用

1. 左侧选择或新建方案。现有 `上 / 右 / 中` 等方案直接兼容。
2. 在显示器地图上选择位置，或点“3 秒后取点”后把鼠标放到目标上。地图和取点只编辑坐标，不会自动移动或点击。
3. 设置快捷键。推荐组合键，或用 iCUE 将专用键映射到 F13–F24，再在这里绑定。避免将常用字母设置为单键触发。
4. “运行方案”立即回放；停止按钮或 `Ctrl+Alt+F10` 停止；`Ctrl+Alt+P` 暂停／恢复快捷键与定时触发。

更改会在短暂停顿后自动保存，也可按 `Ctrl+S`。输入快捷键期间暂不触发方案，离开输入框后启用。关闭窗口会留在托盘，托盘菜单可退出。

## 录制与编辑

- “录制操作”倒计时 3 秒后开始，追加到当前方案。再次点击或 `Ctrl+Alt+F10` 停止。
- 记录鼠标移动、左／右／中键点击、滚轮及动作间隔；勾选“含键盘”才录制键盘，组合键保留修饰键。
- 本程序窗口中的操作、自家回放注入、停止／暂停组合键不加入录制。录制达到 100,000 步会自动停止并提示。
- 当前录制是离散动作记录，不保留按键按住时长或连续拖拽轨迹。需要拖拽时在“更多动作”中添加拖拽步骤。
- 双击动作编辑坐标、时长或参数，支持复制、删除、拖动排序；新录制动作可以编辑“录制间隔（毫秒）”。
- “运行选项”提供重复、速度、定时和目标窗口。定时从首次检查后等待完整间隔，不会一启动就运行。复制和导入方案默认停用，确认快捷键后再启用。
- “更多动作”保留已有 20 种动作。步骤编号在界面从 1 开始；像素跳转填 0 表示不跳转。

## VMware / UU 远程快捷键

默认将系统热键与低级键盘监听一起使用，监听在独立消息线程运行。只在命中方案时拦截对应主键，普通输入放行；鼠标侧键仍会透传。回放前等待修饰键松开，避免 Shift+点击改变目标行为。

“快捷键诊断”可开启“只检测快捷键，不执行方案”。切到 VMware 或 UU 的操作画面按下方案快捷键，再回来查看是否收到以及来源（系统热键、键盘监听、后台输入）。此模式不运行宏或定时任务，但命中的快捷键仍由监听处理。

默认关闭的“后台输入补充”使用 Raw Input / INPUTSINK。它可能在普通热键失效时收到宿主输入，但**不能阻止同一按键继续传到远端**，因此建议配合专用键。若对方后来接管了钩子，可在回到本程序后手动“重新连接监听”。程序不会循环抢占钩子顺序。

不能保证所有系统里无条件生效：USB 键盘直接交给虚拟机、驱动独占、安全桌面、被更早监听截断，都可能使宿主收不到输入。VMware 默认 `Ctrl+Alt` 释放键鼠；UU 的本地快捷键保留策略需要实际检测。诊断没有记录时，先释放输入再试，不要把“注册成功”当作跨系统测试成功。

一步移动／左键点击切屏方案可在“运行选项”勾选“切屏前尝试释放 VMware / UU 鼠标捕获”：识别宿主前台进程，VMware 使用默认 Ctrl+Alt 释放；随后解除鼠标范围限制并请求激活目标位置的宿主窗口。若焦点未切换或鼠标仍被拉回、限制，取消后续点击并报错。新建方案默认勾选；旧配置保持原来的移动／点击行为，需要手动开启此选项。这是收到触发后的补救措施，不能让宿主收到已经被驱动或 USB 直通截走的按键；不保证自定义 VMware 释放键或 UU 独占输入可用。

### 用已有 iCUE 触发切屏（可选）

发布目录额外提供不要求管理员权限的 `TinyMouseMacro.Switch.exe`。在诊断页“生成当前方案启动器”，再把生成的 exe 分配给 iCUE 的 **Launch App / 启动应用** 动作；常驻主程序勾选“允许 iCUE 启动切屏方案”。每个 exe 的文件名包含对应方案 ID，不需要在 iCUE 填命令行参数。主程序须已运行，方案须启用。

启动器会等待主程序确认收到方案，确认收到不等于切回已经成功。未收到确认时不弹窗抢焦点，错误写入 `%LOCALAPPDATA%\TinyMouseMacro\launcher-last-error.txt`；具体切屏结果在主程序“最近活动”中查看。

这个入口默认关闭，只允许同一桌面的本地程序触发已启用的一步移动／左键点击方案，不接受程序路径、脚本或网络请求。开启后同桌面的其他进程也可触发这些切屏动作，因此它只适用于你需要该功能时。没有调用任意宏的提权通道。

此路线把“识别按键”交给 iCUE，减少对本工具键盘监听的依赖。**iCUE 的键盘宏能在客户机中生效，不等于 iCUE 的 Launch App 在捕获状态下也一定能启动宿主程序**；仍须先在“只检测”模式中验证三种前台场景。板载宏模式不能据此视为已支持启动宿主程序。

这里的坐标属于宿主 Windows 的显示器布局；在每个系统分别安装宏工具，也不能让宿主坐标自动变成客户机／远端坐标。需要在远端内部执行宏时，应在该系统内配置自己的方案。iCUE 可用的具体原因可能是板载映射、驱动或其输入方式；仅凭宏动作的配置，不能断言本工具可达到同样覆盖率。

### 本机验收顺序

1. 启动原安装位置的 `TinyMouseMacro.exe`（本机为桌面上的同名程序）。发布脚本会在替换前停止该位置的旧实例，不会自动重新启动；无需寻找另一套发布目录。
2. 在“快捷键诊断”开启“只检测”，分别在本机、VMware 客户机、UU 远端画面内按方案键，返回查看最近活动。没有记录时，可开启后台输入补充再试；该模式仍不能证明实际切回成功。
3. 对切屏方案启用“切屏前尝试释放”并把目标设在另一屏的窗口上。关闭“只检测”，分别从 VMware 和 UU 按键切回，确认焦点和鼠标均落到正确的窗口，再试切往其他屏幕。
4. 普通快捷键收不到时再测试 iCUE Launch App 入口，同样先检测、后实际切屏。若 iCUE 也无法在捕获时启动宿主程序，就需要利用远程软件的释放键或调整其捕获设置；不能仅凭宏在客户机里能播放来判断这个入口可用。

## 数据与构建

- 配置：`%APPDATA%\TinyMouseMacro\macros.json`。外观与后台输入偏好在同目录 `preferences.json`。
- 保留原有 JSON 顶层数组、字段与数字枚举。新增 `RecordedDelayMs`：缺省／null 保留旧回放节奏；录制步骤保存动作起始之间的间隔。
- 保存使用临时文件与原子替换，首次改写已有配置前生成 `macros.json.backup`。升级前仍建议保留原 exe 与配置副本。
- .NET 8 Windows Forms。应用继续使用原有管理员清单；没有安装服务、驱动或改变系统输入配置。

```powershell
./build-release.ps1
```

统一用此脚本发布。它读取开机自启项的现有 exe 路径（本机为 `C:\Users\Wes\Desktop\TinyMouseMacro.exe`），先在临时目录编译和自测，成功后停止该路径下的运行实例，再原位更新主程序、配套启动器和 `TinyMouseMacro.README.md`。发布后校验哈希，清理临时目录，保留自启项和用户数据；不会自动重启应用。

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
