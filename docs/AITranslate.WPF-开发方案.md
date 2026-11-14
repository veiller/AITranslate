# AITranslate.WPF 开发方案

## 1. 现状分析

### 1.1 现有 KimiTranslate 控制台版（负责解析与翻译逻辑）

| 文件 | 职责 | 关键点 |
| --- | --- | --- |
| `Program.cs` | 入口 + 交互 | 从控制台读取路径、交互式选择文件；硬编码 apiKey / ApiUrl / model；调用 `XCOMTranslater` |
| `Settings.cs` | 配置模型 | `ApiKey / ApiUrl / Model / BatchSize / ApiDelayMs / TranslatableNodeXPath` |
| `TranslationApiService.cs` | OpenAI 兼容流式 API | SSE `data:` 行解析、`[DONE]` 终止、`ApiStreamResponse.delta.content`；3 次退避重试；`ParseTranslatedXml` 提取 `<translations>` 内 `<entry id=..>` |
| `ApiModels.cs` | 请求/响应模型 | `ApiRequest / Message / ApiStreamResponse / MessageDelta`（Newtonsoft.Json 特性） |
| `XCOMTranslater.cs` | **XCOM2 `.int` 文本** | 逐行累加，`lineCount>=70 或 >=50 且空行` 时切批；`TranslateTextStreamAsync` 流式翻译，UTF16-LE 落盘 `.chn`；system prompt 要求保留注释/占位符 |
| `BG3Translater.cs` | **BG3 XML 入口** | 交互式选文件；按 `.ref.xml` 参考文件复用已有翻译；调用 `XmlTranslationService` |
| `XmlTranslationService.cs` | **BG3 XML 翻译** | `XPath = //content`；`contentuid` 字典复用参考翻译；剩余节点按 `BatchSize` 分批调用 `TranslateBatchAsync`；UTF-8 无 BOM 保存 `.chs.xml` |

### 1.2 现有 AITranslate.WPF 骨架

- `AITranslate.WPF.csproj`：`WinExe` / `net10.0-windows7.0` / `UseWPF`。
- `App.xaml` / `MainWindow.xaml`：空窗口占位，无任何资源引用、无 HandyControl。
- 无 `docs`、无依赖包、无视图模型层。

### 1.3 现有结构的痛点（需要改造的原因）

1. **逻辑与 UI / Console 强耦合**：`XCOMTranslater`、`XmlTranslationService`、`TranslationApiService` 内部大量写死 `Console.WriteLine` / `Console.Write`，无法直接复用到 WPF。
2. **配置硬编码**：`Program.cs`、`BG3Translater` 中直接写死 API 密钥 / 地址 / 模型，无法在 UI 中设置。
3. **单文件、阻塞式**：一次只能处理一个文件，`Console.ReadLine` 交互；无并行、无取消。
4. **无流式预览会话对象**：流式输出直接写到控制台，未把「增量文本」「进度」「状态」暴露成可被 UI 订阅的事件。
5. **两份翻译管线重复**：XCOM 走「纯文本切块」，BG3 走「XML 批处理」，二者已各自封装但无可复用的统一抽象层。

## 2. 目标与范围（首版 = 核心功能）

> 用户选择：抽独立类库 + 事件回调；核心功能先落地。

**首版必须交付：**

1. WPF 桌面界面（控件用 HandyControl）。
2. 设置页：可配置 AI `BaseUrl`、`Model`、`ApiKey`（以及批量大小、API 间隔、XPath、超时、重试、并发数），并**持久化**。
3. 拖拽多个文件 → 每个文件一个页签（多页签）。
4. 每页签**流式输出预览**翻译结果。
5. 翻译结果保存（`.int -> .chn`，`.xml -> .chs.xml`），可设输出路径。

**首版不做（后置）：** 历史记录、全局任务队列面板、主题切换、系统托盘、断点续翻、多语言设置页。

## 3. 总体架构设计

```
+───────────────────────────────────────────────────────────+
│  AITranslate.WPF  (net10.0-windows7.0, WinExe)            │
│  HandyControl UI + MVVM                                    │
│  - MainWindow / TabControl (多页签) / 拖拽区 / 状态栏        │
│  - SettingsWindow (hc:Window)                              │
│  - ViewModels (Main / TranslateTab / Settings)             │
│  - SettingsService + DialogService                         │
└───────────────┬───────────────────────────────────────────┘
                │ 引用 + 事件回调(IProgress/EventHandler)
┌───────────────▼───────────────────────────────────────────┐
│  AITranslate.Core  (net10.0, 类库)                         │
│  与 UI 无关，纯逻辑，可测试                                 │
│  - Configuration / AiSettings + 设置持久化抽象              │
│  - Translation / ChatCompletionClient (流式 SSE)          │
│  - Translation / ITranslationEngine + 进度/流式事件模型     │
│  - Translation / XcomTranslator  (.int)                  │
│  - Translation / Bg3Translator + XmlTranslationService(xml)│
│  - Translation / TranslationEngineFactory (按扩展名选引擎)  │
│  - Concurrency / GlobalRateLimiter (并发+速率限制)          │
└───────────────────────────────────────────────────────────┘
```

**核心思想**：把现有控制台翻译逻辑迁入 `AITranslate.Core`，把「打印到 Console」统一替换成「通过事件 / `IProgress<T>` 向上层汇报」；WPF 层订阅这些事件驱动 UI 更新（流式预览、进度、状态）。KimiTranslate 控制台版保持原样，不在本次范围内处理。

## 4. 核心引擎层设计（AITranslate.Core）

### 4.1 配置模型

配置被拆成两部分：**用户可配置的 AI 接口三项**，与**代码内固定的翻译参数**。

```csharp
// 用户可配置（写入 settings.json，设置界面只暴露这三项）
public sealed class AiSettings
{
    public string BaseUrl { get; set; }   // base_url，填到版本号，如 https://api.deepseek.com/v1
    public string ApiKey { get; set; }    // sk-xxxx
    public string Model { get; set; }     // 如 deepseek-chat

    // /chat/completions 是 OpenAI 兼容接口的固定路径，程序自动补全
    public string ChatCompletionsUrl => BuildChatCompletionsUrl(BaseUrl);
}

// 固定参数（不对外开放配置，见 TranslationDefaults）
public static class TranslationDefaults
{
    public const int BatchSize = 150;                    // BG3 每批节点数
    public const int ApiDelayMs = 2000;                  // 每批后延迟
    public const string TranslatableNodeXPath = "//content";
    public const int TimeoutSeconds = 120;
    public const int MaxRetries = 3;
    public const int MaxConcurrentFiles = 2;             // 同时翻译的文件数
    public const string TargetLanguage = "简体中文";
}
```

> 设计取舍：批量大小、批间隔、XPath 这些参数与游戏格式和接口约定强绑定，交由用户修改只会把译文弄坏
> 或触发限流，因此**不进设置界面、不写入配置文件**，需要调整时改 `TranslationDefaults`。

持久化：由 WPF 端 `SettingsService` 读写 JSON（默认 `%LocalAppData%\AITranslate\settings.json`）；Core 只定义模型，不绑定路径。

持久化：由 WPF 端 `SettingsService` 读写 JSON（默认 `%LocalAppData%\AITranslate\settings.json`）；Core 只定义模型，不绑定路径。

### 4.2 事件 / 进度模型（替代 Console 输出）

```csharp
public enum TranslationStage { FileStarted, ChunkReceived, BatchCompleted, FileCompleted, Error, Status }

public sealed record TranslationProgressItem(
    string FileId,              // 区分多页签
    TranslationStage Stage,
    string Text = "",           // ChunkReceived 为增量文本；其他为消息
    int Percent = 0,            // 0-100 总体进度
    string? Message = null);
```

`ITranslationEngine`：

```csharp
public interface ITranslationEngine : IDisposable
{
    Task TranslateAsync(
        string inputPath,
        string outputPath,
        IProgress<TranslationProgressItem> progress,
        CancellationToken ct);
}
```

各服务构造时注入共享的 `ChatCompletionClient`（复用 `HttpClient`）与 `GlobalRateLimiter`。

### 4.3 ChatCompletionClient（统一流式 API）

- 用 `System.Net.Http` 发送 OpenAI 兼容 `chat/completions`，`stream=true`。
- 方法：`IAsyncEnumerable<string> StreamChatAsync(AiSettings s, ChatMessage[] messages, CancellationToken ct)`。
- 解析 SSE `data: ` 行，`[DONE]` 终止，`yield` 每个 `delta.content`。
- 内建超时、退避重试、HTTP 错误包装；对外暴露 `event` 或 `IAsyncEnumerable` 两种消费方式。
- 依赖收敛到 `System.Text.Json`（Core 内部不使用 Newtonsoft，减少耦合）；如需重试可保留 Newtonsoft。

### 4.4 XcomTranslator（改造 XCOMTranslater）

- 保留逐行切块规则（`>=70 行` 或 `>=50 行且空行`）。
- 将 `TranslateTextStreamAsync` 中的 `Console.Write(chunk)` 改为 `progress.Report(ChunkReceived, chunk)`。
- 每批完成 → `progress.Report(BatchCompleted, percent=...)`；文件结束 → `FileCompleted`。
- 末尾 `result.Replace("&lt;","<").Replace("&gt;",">")` 保留。
- 落盘仍为 UTF16-LE `.chn`（BOM），结果只在全部完成后写文件（或用临时文件流写入，边译边写+预览，二选一，首版建议「预览 + 完成后保存」以保证正确性）。

### 4.5 Bg3Translator + XmlTranslationService（改造）

- 保留 `XPath = //content`、`contentuid` 参考文件复用、`BatchSize` 分批。
- `TranslationApiService.TranslateOnceAsync` 的 `Console.Write(deltaContent)` 改为 `progress.Report(ChunkReceived)`（流式预览 AI 原始响应）。
- 解析出的 `<entry id=..>` 应用回节点后 `progress.Report(BatchCompleted)`。
- 保存仍为 UTF-8 无 BOM `.chs.xml`。

### 4.6 引擎工厂与并发

```csharp
public static ITranslationEngine Create(string inputPath, AiSettings s, ChatCompletionClient client, GlobalRateLimiter limiter)
{
    return Path.GetExtension(inputPath).ToLowerInvariant() switch
    {
        ".int" => new XcomTranslator(...),
        ".xml" => new Bg3Translator(...),
        _ => throw new NotSupportedException("暂不支持该文件类型")
    };
}
```

`GlobalRateLimiter`：用 `SemaphoreSlim` 限制全局并发的 API 调用数（默认 `MaxConcurrentFiles`），避免多页签同时打满接口触发限流；每个页签持有独立 `CancellationTokenSource` 支持「停止」。

## 5. WPF 界面设计（HandyControl）

### 5.1 App.xaml 资源合并

```xml
<Application ...>
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml"/>
        <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/Theme.xaml"/>
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

### 5.2 MainWindow 布局（自上而下）

1. **工具栏**（`hc:ToolBar` / `hc:Button`）：`添加文件`、`添加文件夹`、`开始翻译`、`停止`、`设置`。
2. **拖拽提示 / 空态**：整窗 `AllowDrop="True"`；空态显示 `hc:Empty`「拖拽文件到此处，或将文件加入列表」。
3. **中部多页签区**：`hc:TabControl`，`Diag`（关页签）。每页签一个文件。
4. **状态栏**（`hc:Growl` 提示 + 底部 `StatusBar`）：总进度、进行中文件数、当前 API 状态。

### 5.3 页签模板（TabItem 内容）

- **Header**：文件名 + 状态图标（`hc:LoadingCircle` 转圈 / `hc:Success` / 报错）/ 关闭按钮。
- **Body**：
  - 进度：`hc:ProgressBar`（当前文件总体百分比）+ `hc:LoadingCircle`。
  - 流式输出预览：只读 `TextBox`，自动滚到底；通过事件把 `ChunkReceived` 增量文本 `Dispatcher.BeginInvoke` 追加（**限流 50–100ms 批量刷新**，避免大文件卡 UI）。
  - 「日志」面板（`hc:ListBox` 或 `hc:Expander`）：记录批处理、错误、重试等状态。
  - 「选项」折叠区：输出路径、BG3 参考文件选择、引擎覆盖（auto/int/xml）、批量大小、批间隔。

### 5.4 设置页（SettingsWindow，`hc:Window`）

分「翻译目标」与「AI 接口」两部分：

| 字段 | 控件 | 说明 |
| --- | --- | --- |
| `Game` | `ComboBox` | **翻译目标**：XCOM 2 / 博德之门 3，只能选一个；下方按选择显示影响说明 |
| `BaseUrl` | `TextBox` | 「接口地址」+ 必填；提示"填到版本号为止即可，`/chat/completions` 由程序自动补全"，例 `https://api.deepseek.com/v1`；下方实时回显「实际请求：…」 |
| `ApiKey` | `PasswordBox` | 「API 密钥」+ 必填；提示以 `sk-` 开头 |
| `Model` | `TextBox` | 「模型名称」+ 必填；例 `deepseek-chat` / `gpt-4o-mini` / `moonshot-v1-8k` |

`/chat/completions` 是 OpenAI 兼容接口的固定路径，不让用户填写——由 `AiSettings.BuildChatCompletionsUrl`
统一补全（同时容忍结尾斜杠、以及用户直接粘贴完整地址的情况），保证地址只有一个拼装入口。

含「测试连接」按钮（调用一次最小请求校验，结果就地显示）。必填项为空时「保存」按钮禁用。
保存 → `SettingsService.Save`。

> ⚠️ 不要用 `hc:InfoElement.Title` / `hc:InfoElement.Placeholder` 当标签：在当前主题配置下**不渲染**。


### 5.5 MVVM

- 使用 **CommunityToolkit.Mvvm 8.4.0** 的源生成器：`ObservableObject` + `[ObservableProperty]` + `[RelayCommand]`。
- 属性由 `[ObservableProperty]` 自动生成；联动用 `[NotifyPropertyChangedFor]`（如 `State` → `IsBusy/HasError/StateText`）
  与 `[NotifyCanExecuteChangedFor]`（如 `IsTesting` → `TestCommand`、`State` → `CancelCommand`）。
- 命令由方法上的 `[RelayCommand]` 生成：方法 `StartAllAsync` → 命令 `StartAllCommand`（自动去掉 `Async` 后缀），
  `CanExecute` 通过 `CanExecute = nameof(CanStartAll)` 指定，手动刷新用 `XxxCommand.NotifyCanExecuteChanged()`。
- `TranslateTabViewModel`：容纳一个文件的状态、预览字符串、进度、命令（取消/打开输出目录）。
- `MainViewModel`：页签集合、全局开始/停止、设置命令、拖拽文件处理。

## 6. 关键技术点与实现要点

- **拖拽**：窗口 `AllowDrop`，`Drop` 中支持 `.int`/`.xml` 单选或多选；若为文件夹则递归枚举匹配扩展名。每个文件 `AddTab` 创建独立 `TranslateTabViewModel`。
- **流式预览限流**：`StringBuilder` 积累增量，`DispatcherTimer`（≈80ms）或事件批量推送到 UI；避免每 token 一次 UI 刷新。
- **并行 & 取消**：全局 `SemaphoreSlim(MaxConcurrentFiles)` 限制同时运行的文件数；每个页签 `CancellationTokenSource`，`开始/停止` 分别控制。
- **正确性优先**：流式预览为「实时展示」，最终文件在整份完成后一次性写入，避免半截内容落盘。
- **设置页面加载时机**：`App.OnStartup` 读取 settings 注入 `MainViewModel`。
- **测试连接**：发送 1 条最小 `chat/completions`（`stream=false`），验证 baseurl/模型/apikey 可用。

## 7. 依赖包

| 项目 | 包 | 版本/说明 |
| --- | --- | --- |
| AITranslate.Core | 无新增(用 BCL) | `System.Text.Json`（BCL）/ `System.Net.Http` |
| AITranslate.WPF | HandyControl | 3.5.1（net8.0 兼容，向下兼容 net10.0） |
| AITranslate.WPF | CommunityToolkit.Mvvm | 8.4.0（`ObservableObject` / `[ObservableProperty]` / `[RelayCommand]`） |

## 8. 实施步骤（Todo 顺序）

**A. 引擎层（Core）**
1. 新建 `AITranslate.Core` 类库并加入解决方案。
2. 配置模型 + 进度/流式事件模型 + 引擎接口 + 工厂 + 限流器。
3. 移植 `ChatCompletionClient`（流式 SSE + 重试）。
4. 移植改造 `XcomTranslator`（事件化、流式）。
5. 移植改造 `Bg3Translator` + `XmlTranslationService`（事件化、流式）。
6. `dotnet build` 确保 Core 编译通过。

**B. WPF 界面层**
7. `AITranslate.WPF` 引入 HandyControl；App.xaml 合并资源字典。
8. `SettingsService`（JSON 持久化）+ `SettingsWindow`。
9. `MainWindow` 骨架：工具栏 + 拖拽区 + `TabControl` + 状态栏。
10. `TranslateTabViewModel`：状态/进度绑定 + 流式预览限流 + 取消命令。
11. 事件接线：拖拽 → 建页签；引擎 progress 事件 → 更新页签。
12. 并发/取消/开始停止控制。
13. `dotnet build` 并 `dotnet run` 验证；拖入 `.int`/`.xml` 走通全流程。

**C. 打磨（后置）**
14. 历史记录 / 主题切换 / 全局队列面板 / 系统托盘（视需要）。

## 9. 风险与注意点

- **HandyControl 资源合并 & 主题**：`SkinDefault.xaml` / `Theme.xaml` 需在启动时加载成功，否则控件样式异常。先做最小窗口验证。
- **流式解析健壮性**：不同 API 返回格式略有差异，`delta.content` 可能缺失/为空；保留容错解析。
- **XML 批处理预览语义**：流式预览是 AI 原始响应（`<translations>` 块），落盘前需成功解析回节点；解析失败要抛错并高亮。
- **API 限流与成本**：并发 + 每批延迟需可配置，默认并发别设太大。
- **大文件性能**：流式预览用限流推送，避免单行几万字符导致 UI 卡顿；进度用整行/整批粒度即可。
- **API Key 存明文 JSON**：本地工具可接受，但仍建议在设置页提示「敏感信息，请勿分享该文件」。
- **TFM 兼容**：Core 用 `net10.0`；WPF 用 `net10.0-windows7.0`（已存在，验证可编译）。

## 10. 交付物

- 可运行的 `AITranslate.WPF`（HandyControl 界面）：设置页、拖拽多文件多页签、每页签流式输出预览、结果保存。
- `AITranslate.Core` 类库（可复用、可测试）。
- 本方案文档。

---

## 11. 实施结果（首版已完成）

### 11.1 实际落地的项目结构

```
AITranslate.sln
├── AITranslate.Core/                    # 新增：与 UI 无关的引擎层 (net10.0)
│   ├── Configuration/
│   │   ├── AiSettings.cs                # 用户可配置三项
│   │   └── TranslationDefaults.cs       # 固定的翻译参数
│   └── Translation/
│       ├── TranslationProgress.cs       # TranslationStage + TranslationProgressItem
│       ├── ITranslationEngine.cs        # 引擎接口 + TranslateRequest
│       ├── ChatCompletionClient.cs      # 流式 SSE + 重试 + TestAsync
│       ├── XcomTranslator.cs            # .int 引擎
│       ├── Bg3Translator.cs             # .xml 引擎
│       ├── XmlTranslationService.cs     # XML 批处理 / 参考复用 / 解析
│       ├── TranslationEngineFactory.cs  # 按扩展名选引擎
│       └── Concurrency/GlobalRateLimiter.cs
├── AITranslate.WPF/                     # 桌面端 (net10.0-windows7.0, HandyControl 3.5.1)
│   ├── App.xaml(.cs)                    # 合并 HandyControl 主题；启动建窗
│   ├── MainWindow.xaml(.cs)             # 工具栏 + 拖拽 + 多页签 + 状态栏
│   ├── Views/SettingsWindow.xaml(.cs)   # 设置窗口（AI 接口三项）
│   ├── ViewModels/                      # MainViewModel / TranslateTabViewModel / SettingsViewModel
│   ├── Services/                        # SettingsService / DialogService / IDialogService
│   └── Converters/InverseBooleanConverter.cs
└── KimiTranslate/                       # 控制台版，保持原样未改动
```

### 11.2 与方案的差异

- **MVVM 采用 CommunityToolkit.Mvvm 8.4.0**：视图模型统一使用 `ObservableObject` + `[ObservableProperty]` +
  `[RelayCommand]` 源生成器（对应 5.5 节方案），不再手写 `INotifyPropertyChanged` 与 `RelayCommand`。
- **配置拆分为「可配置三项 + 固定参数」**：原方案的 10 项配置全部暴露在设置界面，实际使用中其余 7 项
  与游戏格式/接口约定强绑定，属于固定值。现拆出 `TranslationDefaults`（`TranslationDefaults.cs`），
  `AiSettings` 只保留 `BaseUrl` / `ApiKey` / `Model`，引擎也不再依赖 `AiSettings`。
- **设置界面重做**：原实现用 `hc:InfoElement.Title` 当字段标签，该附加属性在当前主题下**不渲染**，
  导致界面只剩一排没有说明的输入框。现改为显式 `TextBlock` 标签 + 示例提示 + 必填标记 + 保存按钮可用性校验。
  主窗口的输出路径、参考文件两处同样改掉了 `hc:InfoElement`。
- **接口地址只填 base_url**：`/chat/completions` 是 OpenAI 兼容接口的固定路径，原先要求用户填完整地址
  （易错且各家格式不一）。现在用户只填到版本号，由 `AiSettings.BuildChatCompletionsUrl` 统一补全，
  设置页实时回显最终请求地址。
- **新增「翻译目标」运行时选择**：设置里用下拉框选 XCOM 2 或博德之门 3（只能选一个），
  它决定接受的文件类型、使用的引擎、输出后缀，以及是否显示「参考文件」行。
  引擎由 `GameKind` 决定而非按扩展名猜测；切换目标会清空已加入的页签（弹确认框）。
  为此把持久化根对象改为 `AppSettings`（`Game` + AI 三项），并新增 `GameProfile` / `GameProfiles`。
- **替换 HandyControl 的 TabItem 模板**：其 `TabItemStyle` 的模板里含
  `{Binding TabStripPlacement, RelativeSource={RelativeSource FindAncestor, AncestorType=TabControl}}`
  条件绑定，TabItem 尚未挂到 TabControl 时会刷出
  `System.Windows.Data Error: 4 : Cannot find source for binding...`。改用 `App.xaml` 中的
  `AppTabItemStyle`（只用 `IsSelected`/`IsMouseOver` 触发），从根上消除该警告。
- **XCOM `.int` 引擎重写为 `XcomTranslatorV2`（结构化 + 编号行协议）**：V1 把整段原文（含 `键=` 与空行）
  发给模型、再把整段回复原样写盘，正确性完全依赖模型自觉。V2 改为逐行结构化解析、
  **只把「值」按 `编号|值` 单行发送**、本地按原行模板重建、**按值去重**、跳过无需翻译的值、缺 id 回退原文。
  V1 保留在代码库中但不再由工厂注册。
  - 协议选型依据：**标记要付两次**（模型必须原样回显结构，而输出 token 更贵）。实测同一文件（41 条唯一值）：
    V1 整段原文 8470 字符；XML `id`+`group` 6690（82 字符/条）；XML 仅 `id` 4430（26 字符/条）；
    **`编号|值` 3498（4 字符/条）**。故改用编号行，并把语体上下文从逐条 `group` 属性改为提示词里的一句静态说明。
  - 实测 `XComGame.int`：135 行 → 135 行**逐行零差异**；72 条文本 → 41 条唯一；资源路径 `img:///...` 正确跳过；
    实际请求体积为原文的 **46%**。
- **预览改为逐行长出目标文档**：新增进度阶段 `OutputLine`，引擎边翻译边把**已定稿的输出文件整行**
  （如 `DisplayName="保卫抵抗军空军基地"`）按文件顺序推给界面，而不是展示模型原始响应。
  翻译开始时先推骨架（分组行、无需翻译的条目），随后译文逐行补进来；**空行同样推送**，
  使预览与落盘文件 1:1（含段落空行）。
  实测断言：把所有 `OutputLine` 拼起来**必须等于最终文件的全部内容（逐行、含空行）**，
  且 mock 只发一半内容时界面已收到 52 行（确认是真流式，而非最后一次性推送）。
- **设置窗口的 ApiKey 输入**：HandyControl 3.5.1 不存在 `PasswordBoxAttach.Password`，改为在代码后置中同步 `PasswordBox.Password`。
- **XCOM 切块修复**：原控制台版在「计数到 70 行」的边界会丢掉触发该边界的那一行；`XcomTranslator.BuildItems` 已修正为把该行归入下一段。
- **引擎致命错误统一抛出**：`XmlTranslationService` 失败时 `throw`（原先返回 `false`），便于页签可靠地判定「失败」状态。

### 11.3 使用说明

1. 运行 `AITranslate.WPF`（`dotnet run --project AITranslate.WPF` 或直接运行 exe）。
2. 点「设置」：先在**「翻译目标」**下拉框选 XCOM 2 或博德之门 3（只能选一个），
   再填「接口地址」（填到 `/v1` 为止）、「API 密钥」、「模型名称」，可点「测试连接」校验；
   界面会实时回显实际请求地址。保存后写入 `%LocalAppData%\AITranslate\settings.json`。
3. 把**当前目标对应的**语言文件（或文件夹）拖到窗口，每个文件生成一个页签。
   拖入不匹配的类型会被拒绝并在状态栏说明。
4. 点工具栏「开始翻译」并发翻译全部页签（并发上限为固定值 2），
   或点页签内的「翻译本文件」单独执行；页签内实时显示流式输出预览与进度。
5. `.int` 输出为 UTF-16 LE `.chn`，`.xml` 输出为 UTF-8 无 BOM `.chs.xml`；
   选博德之门 3 时，页签内会出现「参考文件」行，可指定已翻译的 XML 复用其中已有的翻译。
6. 切换翻译目标会清空已加入的页签（会先确认）。

### 11.4 验证结论

- `AITranslate.Core` 与 `AITranslate.WPF` 全量重建（`-t:Rebuild`）：**0 警告 0 错误**。
  （`KimiTranslate` 作为历史代码保留，全量重建有约 25 条警告，未改动。）
- 应用启动：主窗口与设置窗口均能在运行时正常加载并完成布局。
- 翻译管线端到端（用本地模拟 OpenAI 兼容 SSE 服务验证，不消耗真实额度）：
  - XCOM `.int`：切块、流式增量、UTF-16 LE + BOM 落盘、完成事件 —— **通过**；
  - BG3 `.xml`：参考文件复用、分批 AI 翻译、`<translations>` 解析回填、UTF-8 落盘 —— **通过**；
  - 取消（「停止」）：能正确中断并抛出取消 —— **通过**；
  - 「测试连接」：正常端点返回成功、401 端点正确报错 —— **通过**。
- XAML 绑定诊断（`PresentationTraceSources.DataBindingSource`，并用故意写错的绑定做探针确认追踪生效）：
  **0 绑定错误**，且 `TabStripPlacement` 警告不再出现（已替换 HandyControl 的 TabItem 模板）。
- 翻译目标：两种模式下分别校验扩展名过滤（XCOM2 收 `.int` 拒 `.xml`，反之亦然）、
  输出后缀（`.chn` / `.chs.xml`）、参考文件行显隐（仅 BG3）—— 全部**通过**。
- UI 外观：用 `RenderTargetBitmap` 把主窗口（两种模式）与设置窗口渲染为 PNG 实际查看，
  确认标签与示例提示可见、参考文件行按目标显隐、页签内容区正常撑满；
  并用 A/B 对比 + 实测尺寸定位并修掉了一处「内容区被垂直居中」的布局回归。

