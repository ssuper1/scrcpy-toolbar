# scrcpy 侧边工具栏

Windows 上的 scrcpy 按钮工具栏：返回、桌面、任务、关屏、亮屏、音量、画质、全屏、旋转、通知、电脑键盘、粘贴和退出投屏。

## 先看这里：下载什么，放在哪里

**普通用户请下载 [Releases 最新版本](https://github.com/ssuper1/scrcpy-toolbar/releases/latest) 中的 `scrcpy-toolbar-v0.5.2-windows.zip`，解压后使用。无需编译，也无需安装。** GitHub 自动提供的 `Source code (zip)` / `Source code (tar.gz)` 是源码，不包含编译好的程序。

**scrcpy 本身可以放在任意当前用户可写的目录；本工具的 `toolbar` 文件夹必须放在 scrcpy 的目录里面，与 `scrcpy.exe` 相邻。** 不必使用作者电脑上的路径，不建议放在需要管理员权限才能写入的目录。不能单独把工具栏 EXE 移到桌面，也不能在压缩包内直接运行。

例如，使用 `D:\Apps\scrcpy` 时，目录结构应为：

```text
D:\Apps\scrcpy\
├─ scrcpy.exe
├─ adb.exe
├─ scrcpy-server
├─ ...scrcpy 原有的 DLL 和其他文件（全部保留）
└─ toolbar\
   ├─ ScrcpyToolbar.exe        ← 双击这里启动
   ├─ create-desktop-shortcut.vbs
   ├─ README.md
   └─ LICENSE
```

本项目的运行 ZIP **只包含工具栏，不包含 scrcpy / ADB**。如果还没有 scrcpy，请先下载并解压官方 [scrcpy 3.3.4 Windows 64 位包](https://github.com/Genymobile/scrcpy/releases/download/v3.3.4/scrcpy-win64-v3.3.4.zip)。当前工具栏基于 scrcpy 3.3.4 开发；其他版本尚未验证。

## 第一次使用

1. 把上面的目录放好，确认 `toolbar` 的上一级同时有 `scrcpy.exe` 和 `adb.exe`。
2. 在 Android 手机或平板上打开开发者选项和 USB 调试，用支持数据传输的 USB 线连接电脑。在设备上允许这台电脑进行 USB 调试。
3. 双击 `toolbar\ScrcpyToolbar.exe`。它会同时启动 scrcpy 和工具栏，不显示终端窗口。
4. 想从桌面启动，双击 `toolbar\create-desktop-shortcut.vbs`。它会创建 `scrcpy Toolbar` 快捷方式。之后双击这个快捷方式即可。

如果系统禁用了 VBS，也可以右键 `ScrcpyToolbar.exe`，通过「显示更多选项 → 发送到 → 桌面快捷方式」创建。已有的、指向 `scrcpy.exe` 的快捷方式不会自动显示工具栏。

移动整个 scrcpy 文件夹后，请重新创建桌面快捷方式。本工具按相对位置寻找 scrcpy，文件夹移动后仍可直接运行。

## 使用方法

| 按钮 / 操作 | 效果 |
| --- | --- |
| 返回 / 桌面 / 任务 | 操作设备的返回、主屏幕、最近任务 |
| 关屏 / 亮屏 | 关闭或点亮设备的实体显示屏，电脑继续投屏 |
| 音量 + / − | 调整 Android 设备的音量 |
| 画质 | 切换视频码率和帧率，自动重启投屏应用 |
| 电脑键盘 | 直接输入与 Shift 直通设置、实体 / 兼容模式；另有备用电脑输入窗 |
| 全屏 / 退出全屏 | 切换电脑投屏窗口的全屏状态 |
| 旋转 / 通知 / 粘贴 | 旋转设备画面、展开通知栏、粘贴电脑文字 |
| 顶部「工具」 | 按住拖动工具栏，避开画面内容 |
| 换一侧 | 重新停靠到投屏窗口的另一侧 |
| 收起工具栏 / 展开 | 将工具栏收成小条或重新展开 |
| 退出投屏 / 右上角 × | 同时关闭 scrcpy 投屏和工具栏 |

全屏时默认收起为小条。点击「展开」操作，鼠标离开工具栏 3 秒后会自动收起。窗口模式下工具栏会跟随投屏窗口；拖动位置只保留在当前运行期间。

全屏下需要用键盘退出时，先点击投屏画面，按 `Alt+F` 退出全屏，或按 `Alt+F4` 关闭投屏。

## 用电脑键盘输入手机文字

推荐直接在投屏中输入：

1. 选择「电脑键盘 → 直接输入（实体键盘，推荐）」，保持「Shift 直通手机」勾选。新投屏默认使用此方式。
2. 点击投屏画面里的手机输入框，直接打字。单按 Shift 切换手机输入法的中英文。
3. 拼音、候选词和数字选词由手机输入法处理，具体按键需手机输入法支持实体键盘。若 Shift 或数字不符合预期，请检查手机输入法的实体键盘设置。

**电脑显示英文、手机仍输入中文是正常的。** 电脑端的英文布局负责传递拼音字母；最终输入中文还是英文由手机决定。直通功能仅在投屏窗口获得焦点时请求英文按键布局，避免 Windows 中文输入法再次截走拼音、Shift 和选词数字。切回电脑输入框、最小化投屏或取消勾选时停止直通并恢复原布局；若电脑输入框继承了投屏的英文布局，会在失焦时按记忆恢复一次，之后保留你在电脑中手动切换的输入法。不尝试让两端的中英文图标保持一致；运行期间系统的布局列表中可能出现临时 English (US) 键盘布局，不修改默认输入法设置。

实体键盘模式仍由手机完成中文转换，不需要另外打开电脑输入窗。若希望先在电脑上编辑整段文字，可使用「备用：电脑输入窗（编辑后粘贴）」：

1. 先在投屏画面里点击手机的目标输入框。
2. 点击工具栏「电脑键盘 → 备用：电脑输入窗（编辑后粘贴）」。无需重连投屏。
3. 在新开的电脑输入窗里打字。中英文切换、数字选词按 Windows 输入法自己的设置操作，与手机输入法的当前模式无关。
4. 选词完成后点击「填入手机」。普通 Enter 用于在电脑输入窗中换行，填入动作不会模拟回车或点击发送。

填入使用电脑剪贴板，会覆盖当前剪贴板文字。草稿保留在输入窗中，关闭小窗后重新打开仍可继续编辑，退出工具栏后清除；再次点击填入会重复插入。请检查手机中的实际结果，部分应用或密码框可能限制粘贴。电脑输入窗是单独的编辑窗口，不是在投屏画面内直接显示 Windows 候选框。

可通过「打开手机的实体键盘设置」配置设备布局，通常选 English (US) / QWERTY。设备不支持实体键盘时可选择「兼容模式」输入英文和数字，或使用备用电脑输入窗。Shift 直通仅在已确认的 UHID 实体键盘模式生效。

新投屏默认启用实体键盘（`--keyboard=uhid`）。成功连接后会在 `%LOCALAPPDATA%\ScrcpyToolbar\keyboard-mode.txt` 保存选择，下次启动沿用。切换键盘会暂时重连，保留当前画质；切换画质也会保留键盘模式。附加已有投屏时会尽量读取明确的启动参数，否则将键盘模式标记为未知，选择一次后再应用。

## 工作方式和限制

- Windows Forms 独立程序，通过窗口跟随和左 Alt 快捷键控制 scrcpy，没有修改 scrcpy 本体。
- 启动时，如果同目录的 scrcpy 已有投屏窗口，会附加工具栏；否则默认启动 `scrcpy.exe --shortcut-mod=lalt --stay-awake --video-bit-rate=20M --max-fps=60 --keyboard=uhid`。
- 工具栏启动的默认画质为 20 Mbps、60 FPS，适合高分辨率平板。
- 「画质」按钮可切换流畅 8M/30 FPS、标准 12M/60 FPS、高清 20M/60 FPS、超清 40M/60 FPS；切换时会自动重启当前投屏。
- 使用同目录自带的 `adb.exe`，只为子进程设置 ADB 路径，不改系统环境变量。
- 当前一次管理一个投屏窗口；多个设备时请先让 scrcpy 选定设备并打开投屏，再运行工具栏。
- 自动重连使用工具栏的参数。若附加的 scrcpy 有自定义设备选择或其他启动参数，请在单设备连接下使用模式切换。
- 附加已有投屏时，要求它使用左 Alt 快捷键。直接双击 scrcpy 本身不会自动启动工具栏。
- 需要支持 Windows Forms 的 .NET Framework 4.x；面向 Windows 10 / 11。无需 Python、Node.js 或编译工具。
- 编译和程序内部检查已通过。实际手机音量、关屏和不同 DPI / 多屏 / 全屏组合仍需要用户手动验证；尚未覆盖所有设备。

## 常见问题

**提示找不到 scrcpy / ADB：** 检查文件夹层级，必须是 `scrcpy目录\toolbar\ScrcpyToolbar.exe`，上一级要有 `scrcpy.exe` 和 `adb.exe`。保留官方 scrcpy 包内的其他文件。

**手机插着但没有投屏：** 检查 USB 调试授权、数据线和设备驱动。通过本工具启动时，故障日志会写到 `toolbar\scrcpy.log`。日志可能包含设备信息，公开分享前请先检查。

**按钮没有反应：** 先点击一下投屏画面再操作。工具栏和 scrcpy 应以相同权限运行；已有 scrcpy 窗口也应使用左 Alt 快捷键。若自行更改过 scrcpy 参数，关闭原投屏后通过本工具重新启动。

**分辨率已经很高但画面仍然发糊：** `toolbar\scrcpy.log` 中的 `Texture: 宽x高` 是实际传输分辨率。若它等于平板分辨率，模糊通常来自视频压缩或电脑窗口缩放；先选择「超清」，再把投屏窗口调整到接近原始宽高比例。电脑显示器分辨率低于平板时，放大查看会受到显示器像素限制。

**没有写入权限：** 将整个 scrcpy 文件夹放到当前用户可写的位置，例如自己的下载目录或自建应用目录。

## 从源码构建

源码放在 `src`，脚本放在 `scripts`。Windows 上安装 .NET Framework 的 C# 编译器后，在项目目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

输出为 `dist\toolbar`。如需制作发布 ZIP：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

输出为 `dist\scrcpy-toolbar-v0.5.2-windows.zip` 和对应 SHA-256 校验文件。发布包通过明确的文件清单构建，不包含本地日志、备份、快捷方式或设备信息。

运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1` 可用不可见的模拟窗口验证后台不启用直通、聚焦启用和失焦恢复、电脑窗口继承英文后的恢复、保留电脑手动切换、快速取消尚未处理的布局请求、进程身份检查，以及键盘模式、参数保留和重连。另覆盖备用输入窗的文本和草稿保护。测试不会连接设备、读写剪贴板或向手机输入内容；实际手机 Shift 切换、数字选词和粘贴效果需要手动验证。

键盘行为以 [scrcpy 3.3.4 官方键盘文档](https://github.com/Genymobile/scrcpy/blob/v3.3.4/doc/keyboard.md) 为准。

## 开源许可

本工具以 MIT 许可证发布，见 [LICENSE](LICENSE)。scrcpy 是 [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy) 项目，按其自己的许可证发布；本项目与其无隶属关系。
