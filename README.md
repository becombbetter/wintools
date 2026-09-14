# WinCapture

WinCapture 是一个面向 Windows 11 x64 的本地截图、长截图和录屏工具。

## 功能

- 区域、窗口和全屏截图
- 画笔、箭头、图形、文字、编号、马赛克、模糊、遮挡和裁剪
- 手动滚动实时拼接长截图，可在设置中改为自动滚动
- 吸顶导航与吸底工具栏自动去重，支持撤销上一段
- 长截图预览窗口：可滚动查看整图，带缩放百分比（加减 / 预设档位 / Ctrl+滚轮 / 拖拽平移）
- 主屏、区域和窗口 MP4 录屏
- 系统声音、麦克风、鼠标指针和点击提示
- Windows OCR、置顶贴图、截图历史、托盘和全局快捷键

## 构建

在 Windows 11 x64 上安装 .NET 8 SDK，然后运行：

```powershell
.\build-release.ps1
```

产物是 **单文件** `publish\win-x64-single\WinCapture.exe`（约 70 MB）。整个 .NET 运行时、使用手册和第三方声明
都已打进这一个 exe：拷到任意 Windows 11 x64（或 Windows 10 2004 及以上）机器上，**双击即可运行，无需安装 .NET**，
目录里也只有一个文件。

> 若 .NET 8 SDK 装在自定义位置，可用 `.\build-release.ps1 -DotNet "<dotnet.exe 的完整路径>"` 指定。
> （`publish\win-x64` 是旧的非单文件版本，含运行时 dll，保留未动。）

## 依赖

- 运行环境：Windows 11 x64（或 Windows 10 2004 及以上），**无需预装 .NET**
- 构建环境：.NET 8 SDK（WPF，x64）
- ScreenRecorderLib 7.0.1（MIT）
- Windows Media Foundation
- 个别系统录屏若提示缺少运行库，再安装 Microsoft Visual C++ 2015-2022 Redistributable x64

Windows 11 N 版需要额外安装系统的 Media Feature Pack 才能录屏。
