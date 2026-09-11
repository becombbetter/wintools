# WinCapture

WinCapture 是一个面向 Windows 11 x64 的本地截图、长截图和录屏工具。

## 功能

- 区域、窗口和全屏截图
- 画笔、箭头、图形、文字、编号、马赛克、模糊、遮挡和裁剪
- 自动滚动长截图与内容重叠拼接
- 主屏、区域和窗口 MP4 录屏
- 系统声音、麦克风、鼠标指针和点击提示
- Windows OCR、置顶贴图、截图历史、托盘和全局快捷键

## 构建

在 Windows 11 x64 上安装 .NET 8 SDK，然后运行：

```powershell
.\build-release.ps1
```

发布文件位于 `publish\win-x64`。启动 `WinCapture.exe`，同目录的 `使用手册.html` 可直接用浏览器打开。

## 依赖

- .NET 8 WPF
- ScreenRecorderLib 7.0.1（MIT）
- Windows Media Foundation
- Microsoft Visual C++ 2015-2022 Redistributable x64

Windows 11 N 版需要额外安装系统的 Media Feature Pack 才能录屏。
