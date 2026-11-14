# AITranslate

面向游戏 MOD 的本地语言文件翻译工具，走 **OpenAI 兼容的流式 Chat Completions** 接口。

把 XCOM 2 的 `.int` 或博德之门 3 的 `.xml` 丢进窗口，选好模型，程序会按结构拆行/拆节点、只把需要翻译的文本发给 AI，再把译文按原模板写回目标文件——键名、缩进、引号、XML 标签都不会被改动。

## 支持的游戏

| 翻译目标 | 输入 | 输出 | 参考文件 |
| --- | --- | --- | --- |
| XCOM 2 | `.int` | `.chn`（UTF-16 LE，带 BOM） | 不需要 |
| 博德之门 3 | `.xml` | `.chs.xml`（UTF-8，无 BOM） | 支持复用已有翻译 |

## 功能特性

- **一个页签 = 一个文件**，支持拖拽批量加入、多文件并发翻译（并发数 2）。
- **流式预览**：显示的不是模型的原始回复，而是**最终要写进文件的整行**，译文逐行长出来。
- **结构安全**：只发送“值”，由代码按原行模板重建；缺译就回退原文并告警，不会写出损坏文件。
- **按值去重**：同一英文只翻一次，回填所有出现位置，保证同一字符串译法一致。
- **容错解析**：模型多嘴、包 Markdown 围栏、换分隔符、把一条拆成两行都能救回来。
- 全局并发限流、请求退避重试、可随时取消。

## 项目结构

| 项目 | 目标框架 | 说明 |
| --- | --- | --- |
| `AITranslate.Core` | `net10.0` | 翻译引擎层，与 UI 完全无关；解析、请求、落盘都在这里 |
| `AITranslate.WPF` | `net10.0-windows7.0` | 桌面端，WPF + HandyControl + CommunityToolkit.Mvvm |
| `KimiTranslate` | `net10.0` | 旧版控制台程序，保留作参考，不再加新功能 |

## 快速开始

需要 .NET 10 SDK。

```powershell
# 构建
dotnet build AITranslate.sln -c Debug

# 运行桌面端
dotnet run --project AITranslate.WPF
```

首次使用先在「设置」里填三项：

- **接口地址**：填到版本号为止，例如 `https://api.deepseek.com/v1`，`/chat/completions` 由程序自动补全；
- **API 密钥**；
- **模型名**，例如 `deepseek-chat`。

配置保存在 `%LocalAppData%\AITranslate\settings.json`，**API 密钥是明文存储**，不要把这个文件分享出去。

## 文档

- [`AGENTS.md`](AGENTS.md)：架构、引擎设计取舍、UI 约定与验证方式（也是给 AI 编码代理看的项目说明）。
- [`docs/AITranslate.WPF-开发方案.md`](docs/AITranslate.WPF-开发方案.md)：开发方案与实施结果。

## 说明

当前没有自动化测试工程，改动以手工验证为主，验证清单见 `AGENTS.md` 的「验证方式」一节。
