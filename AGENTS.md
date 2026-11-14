# AGENTS.md

面向 AI 编码代理的项目说明。**动手前请先读完本文件**，尤其是「不要做的事」与「扩展指南」。

## 项目概览

`AITranslate.sln` 用于翻译游戏 MOD 的语言文件，走 **OpenAI 兼容的流式 Chat Completions** 接口。
**运行时可切换「翻译目标」（一次只能选一个），它决定接受的文件类型、使用的引擎与输出后缀：**

| 翻译目标 | 输入 | 输出 | 参考文件 |
| --- | --- | --- | --- |
| XCOM 2 | `.int` | `.chn`（UTF-16 LE 带 BOM） | 不需要 |
| 博德之门 3 | `.xml` | `.chs.xml`（UTF-8 无 BOM） | 支持复用已有翻译 |

解决方案包含 3 个项目：

| 项目 | 目标框架 | 说明 |
| --- | --- | --- |
| `AITranslate.Core` | `net10.0` | **翻译引擎层**，与 UI 完全无关，可复用可测试 |
| `AITranslate.WPF` | `net10.0-windows7.0` | **桌面端**，WPF + HandyControl 3.5.1 |
| `KimiTranslate` | `net10.0` | **旧版控制台程序，保留但不再改动** |

> ⚠️ `KimiTranslate` 是历史代码，**新功能一律加到 `AITranslate.Core` + `AITranslate.WPF`**，不要往控制台项目里加东西。
> 注意 `KimiTranslate/CLAUDE.md` 里写的 `net8.0` 已过时，实际是 `net10.0`。

## 常用命令

```powershell
# 构建整个解决方案
dotnet build AITranslate.sln -c Debug

# 运行桌面端（开发）
dotnet run --project AITranslate.WPF

# 运行已构建的桌面端
AITranslate.WPF\bin\Debug\net10.0-windows7.0\AITranslate.WPF.exe

# 旧版控制台（一般不跑）
dotnet run --project KimiTranslate -- "D:\path\to\file.int"
```

依赖：.NET 10 SDK、`HandyControl 3.5.1`（net8.0 兼容）。**没有测试工程**，改动需手工验证（见「验证方式」）。

## 架构与数据流

```
AITranslate.WPF  (HandyControl UI + CommunityToolkit.Mvvm)
   MainWindow / SettingsWindow / MainViewModel / TranslateTabViewModel
        │  引用 + IProgress<TranslationProgressItem> 事件回调
        ▼
AITranslate.Core  (纯逻辑，禁止引用 WPF)
   GameProfile / GameProfiles ── 每个游戏的文件类型、输出后缀、是否需要参考文件
   AppSettings          ── 持久化根对象：Game + AI 三项
   ChatCompletionClient ── 流式 SSE 解析 + 退避重试 + 地址补全
   XcomTranslatorV2     ── .int：结构化解析 + 只传值 + 去重（当前使用）
   Bg3Translator        ── .xml 翻译（包装 XmlTranslationService）
   XmlTranslationService── XPath 定位 / 参考文件复用 / 分批 / XML 解析
   TranslationEngineFactory ── 按 GameKind 选引擎（不按扩展名猜）
   GlobalRateLimiter    ── 全局并发限流（SemaphoreSlim）
```

### XCOM `.int` 引擎（V2）设计要点

`.int` 的行只有四类：`[组]`、`键="值"`、`键=值`（**值可能没有引号**）、`;注释`、以及空行。
V1 把整段原文（含 `键=`）发给模型、再把整段回复原样写盘，正确性完全靠模型自觉。V2 改为：

1. **逐行结构化解析**，非翻译行（组/注释/空行/无法解析的行）原样保留；
2. **只把「值」发出去**，格式是「`编号|值`」单行，键名与结构**不进入请求**；
3. **本地按原行模板重建**（`KeyPart + "=" + 前导空白 + 引号 + 译文 + 引号 + 尾随空白`），
   行数、键序、缩进、空格、引号有无都由代码保证；
4. **按值去重**只译唯一值（实测 `XComGame.int`：135 行 / 72 条 → **41 条唯一**），
   返回后回填所有同值位置，顺带保证同一英文译法一致；
5. **跳过无需翻译的值**：无字母（纯数字/符号）、资源路径（`img://`、`*.upk` 等）；
6. **缺 id 兜底**：某条没拿到有效译文就**回退原文**并告警，绝不写出损坏文件。

#### 为什么是「编号行」而不是带 id 的 XML

**标记要付两次**：提示词要求模型「原样回显结构」，所以请求里有多少标记，回复里就得吐多少——
而输出 token 比输入贵。实测同一份文件（41 条唯一值）：

| 协议 | 请求字符 | 每条标记 |
| --- | --- | --- |
| V1 整段原文（含键名/组名） | 8470 | — |
| XML `id`+`group` | 6690 | 82 |
| XML 仅 `id` | 4430 | 26 |
| **`编号\|值`** | **3498** | **4** |
| 仅每行一条、无编号 | 3385 | 1 |

从「无编号」（3385）到「编号行」（3498）只多 ~3%，但编号带来**少条/多条/乱序的可检测性**，
而纯按行对应一旦模型合并两行就会静默错位——这 3% 值得花。

另外 `.int` 的值天然单行，所以行协议下**完全不需要转义**（XML 要处理 `&`、`<`、`>`）。

**语体上下文不要逐条附带**：原先每条带 `group="[组名 模板类]"` 是 82 字符/条、还要回显一遍。
现在改成提示词里**一句静态说明**（模型不需要回显，只花输入侧一次）。

> ⚠️ 改这个引擎时注意：
> - **不要把键名或整行原文放进请求**，否则又回到 V1 的老问题；
> - **不要给每一条附加会被回显的额外字段**（标记是双向计费的）；
> - 新增行类型（如新的注释符号）时，同步更新 `ParseLines` / `TryParseEntry`，
>   并跑「恒等翻译 ⇒ 输出与输入逐行相同」这个测试。

#### 预览：逐行长出目标文档（`OutputLine`）

界面预览**不显示模型的原始响应**，而是显示**最终要写进文件的整行**，让用户直接看到 `.chn` 文档长成。
实现要点：

- 引擎新增进度阶段 `TranslationStage.OutputLine`，`Text` 是**已定稿的输出行 + 换行符**；
  `DocumentEmitter` 按**文件顺序**只推送「连续就绪」的部分——某行没定稿时，它后面的行即使已译好也要等，
  这样预览顺序与最终文件完全一致。
- **空行也要推送**：预览必须能 1:1 还原最终文件（含空行造成的分段），否则用户看到的段落结构
  与实际落盘的文件不一致（这条是用户明确要求的，别再"为了好看"过滤空行）。
- 翻译开始时会**先推一次骨架**（分组行、注释行、无需翻译的条目立刻可见），随后译文逐行补进来。
- 流式解析用 `IncrementalBatchParser`：**留一条缓冲**——收到下一条编号行才定稿上一条
  （模型偶尔会把一条译文拆成多行）。最后一条在整批响应结束后由**权威解析**（`ParseTranslations`）裁决，
  因为只有它能正确区分「最后一条的续行」与「编号行之后的废话」。
- UI 侧 `TranslateTabViewModel.OnProgress` 把 `OutputLine` 与 `ChunkReceived` 一样**只入队**，
  由 60ms 的 `DispatcherTimer` 批量刷到 `PreviewText`（避免大文件逐行刷 UI）。
- ⚠️ BG3 没有「文档行」的概念（XML 是最后整体写出的），仍走 `ChunkReceived` 显示原始流。

回归断言（写测试时照抄）：**把所有 `OutputLine` 拼起来，必须等于最终文件的全部内容（逐行、含空行）**——
断言「预览 == 文件」是最省事也最有效的检查；并验证中途（mock 只发一半时）已经收到若干行，
以证明是真流式而不是最后一次性推送。

#### 回复解析的容错（`ParseTranslations`）

模型不一定守格式，解析器按以下顺序兜底，**能救就救、救不了才回退原文**：

1. 剥离 Markdown 代码围栏；
2. 只取「首个编号行 → 最后一个编号行」之间的内容，自动忽略前后多余的话；
3. 分隔符容忍 `|` `｜` `:` `：` `.` `、` `-` 及两侧空格（正则 `NumberedLineRegex`）；
4. 不带编号的非空行 → 视为**上一条的续行**并入（中日韩字符间**紧拼**、其余**加空格**，见 `JoinWrapped`）；
5. 完全不带编号、但**行数与请求一致** → 退化为按顺序对应；
6. 仍取不到的 id → 该条回退原文并在进度里告警。

### 关键文件

| 文件 | 职责 |
| --- | --- |
| `AITranslate.Core/Configuration/GameProfile.cs` | `GameKind` 枚举 + `GameProfile` 记录 + `GameProfiles`（各游戏的文件类型/输出后缀/参考文件开关） |
| `AITranslate.Core/Configuration/AppSettings.cs` | **持久化根对象**：`Game` + AI 三项；`ToAiSettings()` 给引擎 |
| `AITranslate.Core/Configuration/AiSettings.cs` | 引擎需要的 AI 配置；`ChatCompletionsUrl` / `BuildChatCompletionsUrl` 做地址补全 |
| `AITranslate.Core/Configuration/TranslationDefaults.cs` | **固定参数**（批量、批间隔、XPath、超时、重试、并发数、目标语言），不对外开放 |
| `AITranslate.Core/Translation/TranslationProgress.cs` | `TranslationStage` 枚举 + `TranslationProgressItem` 进度模型 |
| `AITranslate.Core/Translation/ITranslationEngine.cs` | 引擎接口 + `TranslateRequest`（含可选 `ReferencePath`） |
| `AITranslate.Core/Translation/ChatCompletionClient.cs` | 流式请求、`data:` 解析、`[DONE]` 终止、重试、`TestAsync` 连通性校验 |
| `AITranslate.Core/Translation/XcomTranslatorV2.cs` | **XCOM2 `.int` 引擎（V2，当前使用）**：结构化解析 + 只传值 + 去重 + 回退兜底 |
| `AITranslate.Core/Translation/XcomTranslator.cs` | XCOM2 旧引擎（V1，已被 V2 取代，未在工厂注册，仅作回退参考） |
| `AITranslate.Core/Translation/XmlTranslationService.cs` | BG3 XML 的核心逻辑 |
| `AITranslate.Core/Translation/TranslationEngineFactory.cs` | 按 `GameKind` 建引擎 + `IsSupported(game, path)` 扩展名过滤 |
| `AITranslate.WPF/ViewModels/TranslateTabViewModel.cs` | **一个页签 = 一个文件**：状态、进度、流式预览、日志、取消；持有本页签的 `GameProfile` |
| `AITranslate.WPF/ViewModels/MainViewModel.cs` | 页签集合、拖拽接入、并发启停、全局状态、切换游戏时清空页签 |
| `AITranslate.WPF/Services/SettingsService.cs` | 配置 JSON 持久化（`AppSettings`） |
| `AITranslate.WPF/Services/DialogService.cs` | 文件/文件夹选择（按游戏过滤）、设置窗口、确认框 |
| `AITranslate.WPF/App.xaml` | HandyControl 主题 + **自定义 `AppTabItemStyle`**（见下方「TabItem 样式」） |

## 扩展指南

### 新增一种游戏格式

1. 在 `AITranslate.Core/Configuration/GameProfile.cs` 的 `GameKind` 加枚举值，
   并在 `GameProfiles` 加对应 `GameProfile`（文件扩展名、文件对话框过滤器、输出后缀、是否需要参考文件）。
2. 在 `AITranslate.Core/Translation/` 实现 `ITranslationEngine`（`FileId` + `TranslateAsync`）。
3. 在 `TranslationEngineFactory.Create` 的 `switch` 里把新的 `GameKind` 映射到该引擎。
   `IsSupported` / 文件过滤 / 输出后缀 / 参考文件行显隐都会自动跟随 `GameProfile`，无需另改。
4. **不要**再回到「按扩展名猜引擎」的写法——引擎一律由用户选定的 `GameKind` 决定。

### UI 线程规则（重要）

引擎的进度回调发生在**后台线程**：

- `OutputLine`（已定稿的输出文件行）与 `ChunkReceived`（模型原始增量）：**只允许入队** `ConcurrentQueue`，
  不要碰任何绑定属性。
- 其它阶段（`Status` / `BatchCompleted` / `FileCompleted` / `Error`）：用 `Dispatcher.BeginInvoke`。
- 队列由 60ms 的 `DispatcherTimer` 在 UI 线程批量刷新到 `PreviewText`。

**流式预览必须限流**——每个 token 都刷 UI 会让大文件直接卡死。

### 新增可配置项（谨慎）

设置窗口目前只有两类内容：**「翻译目标」（`Game`）** 与 **「AI 接口」三项**。
其余翻译参数一律加到 `TranslationDefaults`，**不要**暴露成用户可配置项——
它们与游戏格式、接口约定绑定，用户改坏只会导致译文错乱或触发限流。

确实需要新增用户可配置项时：

1. `AppSettings`（持久化根对象）加属性；只有引擎需要的 AI 项才加在 `AiSettings` 上。
2. `SettingsViewModel` 加字段，并在 `ToSettings()` / `CanSave` 中处理。
3. `SettingsWindow.xaml` 加输入控件 + **显式 `<TextBlock>` 标签与示例提示**（见下方「UI 文案与标签」）。
4. 若该配置影响主界面（如游戏决定文件类型），记得同步 `MainViewModel` 的过滤与提示文案。

### UI 文案与标签（HandyControl 坑）

- ⚠️ **不要用 `hc:InfoElement.Title` / `hc:InfoElement.Placeholder` 当字段标签**：在本项目的主题配置下
  它们**不渲染**，界面会退化成一排没有说明的输入框（这个坑已经踩过）。
  一律用**显式 `<TextBlock>`** 做标签，并在输入框下方用灰色小字给出**示例值**：

  ```xml
  <TextBlock Text="接口地址" FontWeight="SemiBold" Margin="0,0,0,4" />
  <TextBox Text="{Binding BaseUrl, UpdateSourceTrigger=PropertyChanged}" />
  <TextBlock Foreground="#FF8C8C8C" FontSize="12" TextWrapping="Wrap"
             Text="填到版本号为止即可，/chat/completions 由程序自动补全。例：https://api.deepseek.com/v1" />
  ```

- 用户填的地址若会被程序加工，**把加工结果显示出来**（如设置页的「实际请求：`{Binding FullUrl}`」），
  避免用户猜最终请求到哪个地址。

- 必填项在标签后加红色「必填」，并用 `IsEnabled="{Binding CanSave}"` 之类控制提交按钮可用性。
- 灰色文字用固定色值（如 `#FF8C8C8C`），不要依赖可能不存在的 `DynamicResource` 画刷键。

### TabItem 样式（HandyControl 坑）

- 主窗口的 `TabControl` **必须**设 `ItemContainerStyle="{StaticResource AppTabItemStyle}"`。
  该样式定义在 `App.xaml`，用来**替代 HandyControl 的 `TabItemStyle`**。
- 原因：HandyControl `TabItemStyle` 的 ControlTemplate 里有一个 `MultiDataTrigger` 条件绑定了
  `{Binding TabStripPlacement, RelativeSource={RelativeSource FindAncestor, AncestorType=TabControl}}`。
  模板应用时 TabItem 往往还没挂到 TabControl 上，`FindAncestor` 找不到源，于是刷出
  `System.Windows.Data Error: 4 : Cannot find source for binding with reference 'RelativeSource FindAncestor...'`。
  它在 **Condition** 里（不是 Setter），所以只能换模板才能根除；我们的模板只用 `IsSelected` / `IsMouseOver`。
- ⚠️ `AppTabItemStyle` 里的 `VerticalContentAlignment` **必须是 `Stretch`**：
  HandyControl 的 TabControl 模板把内容宿主 `PART_SelectedContentHost` 的对齐绑到了选中页签的该属性上。
  一旦设成 `Center`，**整个页签内容区会被垂直居中且只占内容高度**（预览框撑不开、上下留大片空白）。
- 该样式只支持 `TabStripPlacement=Top`。

### MVVM 约定（CommunityToolkit.Mvvm 8.4.0）

视图模型都是 `partial class : ObservableObject`，不要手写 `INotifyPropertyChanged` 或 `ICommand`：

- **属性**：`[ObservableProperty] private string _baseUrl;` → 生成属性 `BaseUrl`（下划线字段名去掉前缀并首字母大写）。
- **命令**：方法上加 `[RelayCommand]`，方法名 `StartAllAsync` → 生成命令 `StartAllCommand`
  （**自动去掉 `Async` 后缀**，再拼 `Command`）。所以方法名必须起得能推出你想要的命令名：
  想要 `AddFilesCommand` 就把方法命名为 `AddFiles`，**不要**叫 `AddFilesViaDialog`。
- **联动**：`[NotifyPropertyChangedFor(nameof(IsBusy))]`、`[NotifyCanExecuteChangedFor(nameof(CancelCommand))]`；
  无法用特性的场景用 `XxxCommand.NotifyCanExecuteChanged()`。
- **`CanExecute`**：写成 `[RelayCommand(CanExecute = nameof(CanXxx))]`，方法名必须是 `Can` + 命令名去掉 `Command`。

> ⚠️ **命令名/属性名由方法名和字段名推导，是字符串约定**：改了方法名或字段名，必须同步改 XAML 里的
> `{Binding XxxCommand}`，否则界面上的按钮会**静默失效**（见「验证方式」第 4 条）。

## 约定（务必遵守）

- **注释与用户可见文案用中文**，与现有代码保持一致。
- **`AITranslate.Core` 不得引用 WPF / HandyControl**，JSON 用 `System.Text.Json`（不引入 Newtonsoft）。
- **引擎的致命错误要 `throw`**，不要吞掉后返回 `false`——页签依赖异常来判定「失败」状态。
- **输出编码固定**：`.int` → UTF-16 LE（**带 BOM**）`.chn`；`.xml` → UTF-8（**无 BOM**）`.chs.xml`。
- 翻译提示词要求 AI **保留 XML 标签与 `[1]` 这类占位符**，改动提示词时别破坏这条。
- 日志集合有上限（1000 条），避免大文件翻译时内存无限增长。
- 分支命名前缀：`feature/`、`bugfix/`、`hotfix/`。
- 🚫 **改源码一律用编辑工具，不要用 PowerShell 的 `Get-Content` / `Set-Content` 做批量替换**：
  它们按系统 ANSI 码页读写，会把文件里的中文变成**不可逆的乱码**（本项目已经因此损坏过两个文件、
  只能整文件重写）。确需脚本处理时，必须显式指定编码两端的
  （`Get-Content -Encoding UTF8` + `[System.IO.File]::WriteAllText(..., UTF8Encoding($false))`），
  并在改完后用 `Select-String` 搜 `锛|浣|鐨勬` 确认没有乱码。

## 配置与运行时

- 配置文件：`%LocalAppData%\AITranslate\settings.json`，保存 **`Game`（翻译目标）+ AI 三项**
  （BaseUrl / ApiKey / Model）。旧版只有 AI 三项的扁平文件可直接读取（`Game` 默认 Xcom2）。
- **翻译目标决定一切**：接受的文件类型、引擎、输出后缀、参考文件行显隐，全部来自 `GameProfiles.For(game)`。
  切换游戏时会**清空已加入的页签**（会先弹确认框；用户放弃切换则保留原目标但仍应用 AI 参数改动）。
- **`BaseUrl` 是 base_url 语义**：用户只填到版本号（如 `https://api.deepseek.com/v1`），
  `/chat/completions` 是 OpenAI 兼容接口的固定路径，由 `AiSettings.BuildChatCompletionsUrl` 自动补齐。
  请求一律用 `AiSettings.ChatCompletionsUrl`，**不要在别处直接拼路径**；该函数已处理结尾斜杠，
  以及用户直接粘贴完整地址（以 `/chat/completions` 结尾）的情况。
- 引擎的固定参数在 `TranslationDefaults`：`BatchSize=150`（BG3 每批节点）、`ApiDelayMs=2000`、
  `TranslatableNodeXPath=//content`、`TimeoutSeconds=120`、`MaxRetries=3`、`MaxConcurrentFiles=2`、
  `XcomMaxEntriesPerBatch=50`、`XcomMaxCharsPerBatch=6000`（XCOM 每批唯一值条数与字符上限）。
- ⚠️ **ApiKey 以明文存储**，提醒用户不要分享该文件。
- 未配置 `BaseUrl` / `Model` 时，点「开始翻译」会弹提示并中止（`MainViewModel.IsConfigured`）；
  设置窗口的「保存」按钮在必填项为空时禁用（`SettingsViewModel.CanSave`）。

## 验证方式

没有自动化测试工程，推荐按下面的方式验证，**不要消耗真实 API 额度**：

1. `dotnet build AITranslate.sln -c Debug` 必须 **0 错误**。警告范围：
   `AITranslate.Core` 与 `AITranslate.WPF` 必须 **0 警告**；`KimiTranslate`（历史代码，不改动）全量重建时有约 25 条可空性/CA2024 警告。
   注意用 `-t:Rebuild` 验证——增量构建不会重新编译未改动的项目，警告会看不到。
2. 启动桌面端，确认主窗口与设置窗口能正常加载（HandyControl 主题/XAML 资源问题只在运行时暴露）。
3. 翻译管线：用本地最小的 HTTP 服务模拟 OpenAI 兼容的 **SSE** 接口（`data: {...}` + `data: [DONE]`），
   驱动 Core 的引擎，校验流式解析、切块、编码落盘、XML 回填与取消。
   - 模拟端注意：请求体是 JSON，`<` `>` `"` 会被 `System.Text.Json` 转义成 `\u003C` 等形式，
     应先 `JsonDocument.Parse` 取出消息内容再匹配，不要直接对原始 body 用正则。
   - **XCOM `.int` 最有效的回归测试是「恒等翻译」**：让模拟服务按 `编号|原文` 原样返回，
     则输出文件必须与输入**逐行完全相同**。这一条能一次性抓住解析/重建的所有结构性问题
     （引号有无、`=` 前后空格、行尾空格、注释、缩进、未闭合引号、无 `=` 的行……）。
   - 还要补这几组用例：
     - 故意丢掉某个 id → 该条回退原文 + 告警，其余正常；
     - 同一个值出现多次 → 去重只送一条，回填时**两处都要更新**；
     - **脏回复容错**：前后加废话、包 Markdown 围栏、分隔符写成 `｜`/`:`/`、`、把一条拆成两行
       → 都要能正确解析，且废话不得混入译文；
     - 回复完全不带编号但行数一致 → 退化为按顺序对应。
   - ⚠️ 模拟端解析请求行时记得去掉行尾 `\r`（请求用 `\r\n`），否则抓到的文本会带残留、
     造成测试假失败（这个坑踩过）。
4. **改动 XAML 绑定后，务必做一次绑定诊断**（绑定名是运行时字符串解析，写错**不报编译错、只静默失效**）：
   在 `App.OnStartup` 里把 `PresentationTraceSources.DataBindingSource` 的 `Switch.Level` 设为 `SourceLevels.Warning`
   并挂一个 `TraceListener` 收集输出，然后实例化主窗口、**至少加入一个页签**
   （否则 `TabControl.ContentTemplate` 里的绑定不会实例化，检查不到）、以及设置窗口，最后筛选
   `BindingExpression path error`。
   - ⚠️ 必须**同时加一个故意写错属性名的绑定做探针**：如果探针也没被抓到，说明追踪没生效，
     此时「0 错误」的结论不可信（这个坑踩过）。
5. **UI 改动要真的看一眼**：在自检里把窗口用 `RenderTargetBitmap` + `PngBitmapEncoder` 渲染成 PNG
   （渲染 `window.Content`，渲染前 `UpdateLayout()` 并 `Dispatcher.Invoke(..., DispatcherPriority.Loaded)`），
   然后直接看图确认。仅靠"能编译、能启动"无法发现**标签没渲染**这类问题。
   - 判断元素是否被裁切时不要靠肉眼猜，打印实测坐标更可靠：
     `element.TransformToAncestor(root).Transform(new Point(0,0))` 得到 top，加上 `ActualHeight` 与内容高度比较。
   - 布局回归用 **A/B 对比**定位：例如"换了 TabItem 样式后内容被居中"，就在同一次运行里分别设置
     `ItemContainerStyle = null` / 不设，各打印一次关键元素尺寸（如 `PART_SelectedContentHost` 的
     `ActualHeight` 与 `VerticalAlignment`），差异立刻指向根因，比看图猜快得多。

### 自检脚本自身的坑

- **`ShutdownMode` 默认是 `OnLastWindowClose`**：自检里关掉一个窗口后应用就开始退出，
  之后再 `new Window()` 会抛 `InvalidOperationException: 应用程序对象正在关闭`。
  自检开头先设 `ShutdownMode = ShutdownMode.OnExplicitShutdown;`。
- 窗口 `Show()` 后要跑一次调度再量尺寸（`Dispatcher.Invoke(..., DispatcherPriority.Background)`），
  否则 `ActualWidth/Height` 可能仍是 0，截图会变成 0×0。
- 探针（故意写错的绑定）要**先跑**，再做其它场景；否则一旦中途抛异常，"0 错误"就失去意义。

## 已知缺口（尚未实现）

历史记录、主题切换、全局任务队列面板、系统托盘、断点续翻、ApiKey 的 DPAPI 加密。

## 参考文档

- 开发方案与实施结果：[docs/AITranslate.WPF-开发方案.md](docs/AITranslate.WPF-开发方案.md)
