# UI 约定（重构后必须遵守）

本文件是重构期间所有 UI 代码的公共契约。修改任何 `.razor` 前先读一遍。

## 1. 颜色与主题

**禁止在组件 `<style>` 中写死颜色。** 组件作用域样式会在 `app.css` 之后注入，
同优先级下永远胜过全局规则，写死颜色会导致深色主题不可读。

必须使用 `wwwroot/app.css :root` 里定义的令牌（深色主题会自动换成另一套值）：

| 用途 | 令牌 |
| --- | --- |
| 页面/主体背景 | `--body-bg` `--page-bg` `--main-bg` |
| 卡片/面板 | `--card-bg` `--surface-2` |
| 次级表面（表头、页脚、hover） | `--surface-3` `--light-bg` |
| 输入框 | `--input-bg` |
| 代码块 | `--code-bg` |
| 侧栏 | `--sidebar-bg` |
| 边框 | `--border-color` `--border-strong` |
| 文字 | `--text-primary` `--text-secondary` `--text-muted` `--text-inverse` |
| 品牌色 | `--primary-color` `--primary-dark` `--primary-light` `--primary-contrast` |
| 语义色（前景/背景/文字三元组） | `--success-color|-bg|-text`，`--danger-…`，`--warning-…`，`--info-…` |
| 阴影/圆角 | `--shadow-sm|-md|-lg`，`--radius-sm|-md|-lg` |
| 等宽字体 | `--font-mono` |

主题属性挂在 `<html data-theme="Light|Dark">` 上（由 `app.js` 的 `ffui.applyTheme` 设置）。

## 2. 可复用的全局 class（不要重复实现）

- 按钮：`.ffui-btn`（默认）、`.ffui-btn-primary`、`.ffui-btn-danger`、`.ffui-btn-ghost`、`.ffui-btn-sm`、`.ffui-btn-lg`
- 图标按钮：`.ffui-icon-btn`（必须带 `aria-label`）
- 面板：`.ffui-panel`、`.ffui-panel-title`
- 徽标：`.ffui-badge` + `-primary|-success|-danger|-warning|-info`
- 提示条：`.ffui-alert` + `-info|-success|-warning|-danger`
- 空状态：`.ffui-empty-state`
- 容器：`.ffui-container`、`.ffui-section-title`
- 进度条：`.ffui-progress-track` / `.ffui-progress-fill`
- 加载：`.spinner`、`.spinner-lg`、`.ffui-loading-row`（**不要**再定义 `@keyframes spin`）

## 3. 可复用的共享组件（`Components/Shared/`）

- `Modal.razor` — 参数：`Title`、`IsOpen`、`IsOpenChanged`、`Size`("sm|md|lg|xl")、
  `CloseOnBackdrop`、`DisableClose`，以及**唯一的**隐式 `ChildContent`。
  已处理 Esc 关闭、遮罩点击、`role="dialog"`、打开后聚焦。**不要再手写遮罩层。**
  底部按钮栏写法：把 `<div class="ffui-modal-footer">…按钮…</div>` 放在内容的**最后一个元素**
  （该 class 定义在 `app.css`，Modal 内部会自动把它贴到底部）。
  注意 `Modal` **没有** `FooterContent` 参数，也没有可 `@ref` 的关闭方法——
  需要主动关闭时在自己的组件里 `IsOpen = false; await IsOpenChanged.InvokeAsync(false);`。
- `ConfirmDialog.razor` — 参数：`IsOpen`、`IsOpenChanged`、`Title`、`Message`、`Detail`、
  `ConfirmText`、`CancelText`、`Danger`、`OnConfirm`、`OnCancel`、`IsBusy`。
  它内部已经包含 `<Modal>`，**不要**再自己套一层。
- `ToastHost.razor` — 已挂在 `MainLayout`，无需再引入。
  通过 `@inject IToastService Toast` 调用：`Toast.Success/Warning/Error/Show("文本")`。
  **不要再在每个页面里自定义 `_message` / `_messageClass` 横幅。**
- `TemplateParameterEditor.razor` — 参数：`Template`、`Values`、`ValuesChanged`、`Disabled`。
  渲染模板声明的全部参数控件。

## 4. Blazor 硬性规则

- 定义 `Dispose()` 的组件**必须**写 `@implements IDisposable`，否则永不调用。
- 订阅单例服务的事件后必须退订（`Dispose`）。
- 事件处理器不要用 `async void`；用 `async Task` 并在 `InvokeAsync` 里调用，或
  在处理器中 `try/catch` 后 `_ = InvokeAsync(StateHasChanged)`。
  **不要**写 `async void OnXxxChanged(...)`。
- `InvokeAsync(StateHasChanged)` 的返回值要 `await` 或显式丢弃（`_ =`）。
- 循环渲染必须加 `@key`。
- 不要在组件里改 `[Parameter]` 的值；只用 `XxxChanged.InvokeAsync(...)` 通知父组件。
- 搜索/文本输入用 `@bind:event="oninput"` 时，对重计算要做缓存或防抖。
- 所有 `catch` 必须有可见反馈（Toast 或提示条），**不要**写空的 `catch { }`。

## 5. 格式化

- 数字/百分比/时间进入 CSS 或文本时必须用 `CultureInfo.InvariantCulture`
  （否则中文区域会输出 `85,5` 这种非法 CSS 宽度）。
- 文件大小统一用 `IFileService.FormatFileSize(bytes)`。
- 时间统一用 `ConversionTask.DurationSeconds` / `TimeSpan` 格式化。

## 6. 服务契约（重构后）

```csharp
IFFmpegService
  Task<FFmpegInfo?> GetFFmpegInfoAsync(bool forceRefresh = false)
  Task<MediaInfo?> GetMediaInfoAsync(string filePath)
  Task<FFmpegRunResult> RunAsync(FFmpegRunRequest request, CancellationToken ct = default)
  Task<bool> SendInputAsync(ObjectId taskId, string input)   // 仅用于 'q' 优雅退出
  Task KillAsync(ObjectId taskId, bool graceful)
  int RunningCount { get; }
  FFmpegPaths ResolvedPaths { get; }
  BuiltCommand BuildCommand(CommandTemplate template, CommandContext context)

IHardwareDetectionService
  Task<HardwareDetectionReport> DetectAsync(bool forceRefresh = false, CancellationToken ct = default)
  HardwareDetectionReport? LastReport { get; }
  Task<bool> IsEncoderAvailableAsync(string encoderName)
  Task<string> GetRecommendedEncoderAsync(string codec, string? requiredEncoder = null)
  EncoderProfile? ResolveProfile(string encoderName)
  DateTime? LastDetectionTime { get; }

ITemplateService
  Task<List<CommandTemplate>> GetAllTemplatesAsync(bool includeSystem = true)
  Task<List<CommandTemplate>> GetTemplatesByCategoryAsync(string category)
  Task<CommandTemplate?> GetTemplateByIdAsync(ObjectId id)
  Task<CommandTemplate> Create/Update/Delete/Copy…Async
  Task<SystemTemplateSyncResult> InitializeSystemTemplatesAsync()
  Task ResetSystemTemplatesAsync()
  Task<CommandTemplate> CreateSystemTemplateAsync(CommandTemplate t)
  Task<bool> UpdateSystemTemplateAsync(CommandTemplate t)
  Task<bool> DeleteSystemTemplateAsync(ObjectId id)
  Task<List<string>> GetCategoriesAsync()
  Dictionary<string,string> CreateDefaultParameterValues(CommandTemplate t)
  IReadOnlyList<TemplateIssue> Validate(CommandTemplate t)
  Task<TemplateTestResult> TestTemplateAsync(CommandTemplate t, IReadOnlyDictionary<string,string>? values = null)
  string ExportToJson(IEnumerable<CommandTemplate> templates)
  Task<int> ImportFromJsonAsync(string json, bool asSystemTemplate = false)
  int PresetVersion { get; }

ITaskService
  Task<ConversionTask> CreateTaskAsync(string input, string output, ObjectId templateId, Dictionary<string,string>? parameters = null)
  Task<BatchTask> CreateBatchTaskAsync(List<string> inputs, string outDir, ObjectId templateId, Dictionary<string,string>? parameters = null)
  Task<bool> StartTaskAsync(ObjectId taskId)                    // 立即返回，后台队列执行
  Task<BatchTask> StartBatchTaskAsync(List<string> inputs, string outDir, ObjectId templateId, Dictionary<string,string>? parameters = null)
  Task<bool> CancelTaskAsync(ObjectId taskId, bool graceful = false)
  Task CancelBatchAsync(ObjectId batchId)
  Task<ConversionTask?> GetTaskByIdAsync(ObjectId taskId)
  Task<List<ConversionTask>> GetTaskHistoryAsync(int limit = 50, TaskStatus? status = null)
  Task<List<ConversionTask>> GetRunningTasksAsync()
  Task<List<ConversionTask>> GetPendingTasksAsync()
  Task<List<ConversionTask>> GetBatchTasksAsync(ObjectId batchId)
  Task<ConversionTask?> RetryTaskAsync(ObjectId taskId)
  Task<int> DeleteTasksAsync(IEnumerable<ObjectId> taskIds)
  Task<int> DeleteTasksByStatusAsync(TaskStatus status)
  Task<int> CleanupHistoryAsync(DateTime olderThan)
  Task<int> ClearHistoryAsync()
  int RecoverOrphanedTasks()
  int QueuedCount { get; }  int ActiveCount { get; }
  event EventHandler<TaskProgressEventArgs>? TaskProgressChanged
  event EventHandler<TaskStatusEventArgs>? TaskStatusChanged
  event EventHandler<TaskLogEventArgs>?    TaskLogAppended    // (TaskId, Line)

IFileService
  bool IsFileAccessible(string path)
  bool IsDirectoryWritable(string path)
  long GetAvailableDiskSpace(string path)
  string GenerateOutputPath(string input, string outDir, string extension, string? suffix = null)
  string GenerateUniqueOutputPath(string input, string outDir, string extension, string? suffix = null)
  string FormatFileName(string template, string input, string extension)
  List<string> ScanMediaFiles(string directory, List<string>? extensions = null, bool recursive = false)
  long GetFileSize(string path)
  void EnsureDirectoryExists(string path)
  string FormatFileSize(long bytes)
  IReadOnlyCollection<string> SupportedMediaExtensions { get; }

IFileDialogService
  bool IsSupported { get; }  string BackendDescription { get; }
  Task<string?> PickFileAsync(string title = "选择文件", string? filter = null, string? initialDirectory = null)
  Task<string?> PickFolderAsync(string title = "选择文件夹", string? initialDirectory = null)
  // 返回 null 表示用户取消或平台不支持；调用方必须给出可见反馈

ISettingsService
  Task<AppSettings> GetSettingsAsync()
  Task SaveSettingsAsync(AppSettings settings)
  Task ResetToDefaultAsync()
  Task<bool> ValidateFFmpegPathAsync(string path)
  Task<string?> GetFFmpegVersionAsync(string path)
  event EventHandler? SettingsChanged

IAppStatusService
  Task<AppStatus> GetStatusAsync(bool forceRefresh = false)
  bool IsDataDirectoryWritable { get; }

IToastService
  void Show(string text, ToastLevel level = ToastLevel.Info, int durationMs = 4000)
  void Success(string text, int durationMs = 4000)
  void Warning(string text, int durationMs = 5000)
  void Error(string text, int durationMs = 8000)
  void Dismiss(Guid id)   void Clear()
  event EventHandler? Changed   IReadOnlyList<ToastMessage> Messages { get; }
```

## 7. JS 互操作（`wwwroot/js/app.js`，全局 `window.ffui`）

```js
await JS.InvokeAsync<bool>("ffui.copyText", text)        // 剪贴板，返回是否成功
await JS.InvokeVoidAsync("ffui.applyTheme", theme)        // "Light"|"Dark"|"System"
await JS.InvokeVoidAsync("ffui.initLogScroll", element, true)
await JS.InvokeVoidAsync("ffui.scrollToBottom", element)
await JS.InvokeVoidAsync("ffui.focusElement", element)
await JS.InvokeAsync<bool>("ffui.saveTextFile", name, content, mime)   // 下载文件
await JS.InvokeAsync<bool>("ffui.requestNotificationPermission")
await JS.InvokeAsync<bool>("ffui.notify", title, body)
```

## 8. 数据模型要点

- `CommandTemplate`：`Name` `Description` `CommandArgs` `Type` `Category`
  `SupportedInputFormats` `OutputExtension` `TargetCodec` `HardwareMode`
  `RequiresHardwareAcceleration` `RequiredEncoder` `Parameters` `Tags`
  `IsRecommended` `PresetVersion` `SortOrder` `InputFormatsDisplay`
- `TemplateParameter`：`Name` `Label` `Description` `Kind` `DefaultValue`
  `Options` `Min` `Max` `TrueValue` `FalseValue`
- `HardwareEncoder`：`Name` `DisplayName` `Type` `Family` `Codec` `IsAvailable`
  `SupportsQualityOptions` `IsHardware` `UnavailableReason` `MissingFromBuild`
- `HardwareDetectionReport`：`Encoders` `AvailableHardwareEncoders` `HasHardwareAcceleration`
  `Summary` `FFmpegVersion` `FFmpegPath` `Platform` `Hwaccels` `DetectedAt`
- `ConversionTask`：状态枚举新增 `Skipped`（已跳过）。`TemplateName`、`TemplateParameters`、
  `RetryOfTaskId`、`DurationSeconds` 可用。
- `TemplateIssue(Severity, Message, Field)`；`TemplateTestResult(Success, Message, Command, Output, Duration)`
- `AppStatus`：`FFmpegAvailable` `FFmpegVersion` `FFmpegPath` `FFprobePath` `Platform`
  `DataDirectory` `AvailableHardwareEncoderCount` `TemplateCount` `Notices` `Hwaccels` 等。

## 9. 重要行为提醒

- FFmpeg 命令里**不要**写 `-y` / `-n`，执行层会按「文件已存在时的处理方式」自动添加。
- 覆盖策略由 `OutputPathResolver` 在任务执行时处理（`Ask` 在批处理中等价于自动重命名），
  所以任何分支都不会让 FFmpeg 阻塞等待输入。
- 硬件加速模板应使用 `{encoder}` + `{quality_args}` + `{preset_args}` + `{hwupload_args}`，
  而不是硬编码某个厂商的编码器；这样在任何平台上都能正确工作。
- `ITaskService.StartTaskAsync` 立即返回，任务在后台队列执行，页面跳转不会中断转换。
