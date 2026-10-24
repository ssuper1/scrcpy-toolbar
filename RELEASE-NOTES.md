# v0.1.0

首个公开版本：为 Windows scrcpy 增加可拖动、可收起的侧边按钮工具栏。

## 下载与安装

下载附件 **scrcpy-toolbar-v0.1.0-windows.zip**。解压后把整个 **toolbar** 文件夹放进已有 scrcpy 目录，再双击 **toolbar\ScrcpyToolbar.exe**。可运行 **create-desktop-shortcut.vbs** 创建桌面快捷方式。

scrcpy 可以放在任意当前用户可写的目录，但 toolbar 必须位于其中。需要单独下载官方 scrcpy，运行包不包含 scrcpy 或 ADB。自动生成的 Source code 压缩包只提供源码。

## 功能

- 返回、桌面、最近任务、关屏、亮屏、音量、全屏、旋转、通知、粘贴。
- 无终端启动，同时启动投屏和工具栏。
- 顶部拖动，左右换侧；全屏默认收起，离开工具栏 3 秒后自动收起。
- 退出投屏按钮以及 × 同时关闭投屏和工具栏。
- 使用 scrcpy 自带 ADB，不修改系统环境变量。

## 验证范围

基于 scrcpy 3.3.4 开发，编译及按键标记、收起展开、拖动边界检查已通过。实际设备音量和不同全屏、多屏、DPI 组合仍待用户手动验证。

源码与工具栏使用 MIT 许可证；scrcpy 保留其自身许可证。
