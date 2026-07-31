# 飞书妙记字幕下载器

面向 Windows 11 x64 的轻量原生桌面程序。从飞书妙记分享链接提取带毫秒时间轴的逐句字幕，并生成 SRT 文件。

## 特点

- 原生 C# / WPF，不携带 Python、Playwright、Node.js 或浏览器运行时。
- 自动跟随 Windows 11 应用深浅色模式，切换系统主题后无需重启。
- 使用普通 HTTPS 请求建立匿名分享会话，不读取本机 Chrome Profile。
- 保留妙记标题命名、每行字数/时长调整、取消下载、运行日志和说话人字幕映射。
- 目标框架为 Windows 11 已内置的 .NET Framework 4.8.1。

## 使用

从 [GitHub Releases](https://github.com/KingStar-China/FeiShu-Sub/releases/latest) 下载并双击 `妙记字幕下载器.exe`。

默认将字幕保存到 EXE 同目录下的 `minutes/`。SRT 直接写入所选保存位置，不再额外建立 token 子目录；如果该目录中已有 `transcript.txt`，程序还会生成带说话人前缀的 `_speaker.srt`。

## 构建

要求：

- Windows 11 x64
- Visual Studio 2022 Build Tools（包含 MSBuild 与 WPF 构建任务）
- 可访问 NuGet 官方源以下载仅在编译期使用的 .NET Framework 4.8.1 参考程序集

运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build_release.ps1
```

构建脚本只生成 `release/妙记字幕下载器.exe`，在控制台显示 SHA-256，并在成功后删除临时参考程序集和 `bin/obj`。重复构建不会删除 `release/minutes` 中的字幕。

## 项目结构

```text
src/FeishuMinutes/   WPF 界面与纯 HTTP 字幕核心
minutes/             已下载字幕
release/             单个可分发 EXE
build_release.ps1    可复现发布构建
```

飞书妙记页面使用内部接口，若飞书未来调整接口结构，可能需要同步更新解析规则。
