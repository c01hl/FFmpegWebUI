namespace FFmpegWebUI.Services;

/// <summary>轻提示级别</summary>
public enum ToastLevel
{
    /// <summary>普通信息</summary>
    Info = 0,
    /// <summary>成功</summary>
    Success = 1,
    /// <summary>警告</summary>
    Warning = 2,
    /// <summary>错误</summary>
    Danger = 3
}

/// <summary>一条轻提示</summary>
public sealed record ToastMessage(Guid Id, string Text, ToastLevel Level, DateTime CreatedAt, int DurationMs)
{
    /// <summary>图标</summary>
    public string Icon => Level switch
    {
        ToastLevel.Success => "✅",
        ToastLevel.Warning => "⚠️",
        ToastLevel.Danger => "❌",
        _ => "ℹ️"
    };

    /// <summary>CSS 修饰类</summary>
    public string CssClass => Level switch
    {
        ToastLevel.Success => "ffui-toast-success",
        ToastLevel.Warning => "ffui-toast-warning",
        ToastLevel.Danger => "ffui-toast-danger",
        _ => string.Empty
    };
}

/// <summary>
/// 全局轻提示服务。用单例广播而不是每个页面各自维护一份状态，
/// 这样后台任务完成时的提示在任何页面上都能看到。
/// </summary>
public interface IToastService
{
    /// <summary>提示列表发生变化</summary>
    event EventHandler? Changed;

    /// <summary>当前显示中的提示</summary>
    IReadOnlyList<ToastMessage> Messages { get; }

    /// <summary>显示一条提示</summary>
    void Show(string text, ToastLevel level = ToastLevel.Info, int durationMs = 4000);

    /// <summary>显示成功提示</summary>
    void Success(string text, int durationMs = 4000);

    /// <summary>显示警告提示</summary>
    void Warning(string text, int durationMs = 5000);

    /// <summary>显示错误提示（默认停留更久）</summary>
    void Error(string text, int durationMs = 8000);

    /// <summary>移除指定提示</summary>
    void Dismiss(Guid id);

    /// <summary>清空全部提示</summary>
    void Clear();
}

/// <summary>轻提示服务实现</summary>
public sealed class ToastService : IToastService
{
    private const int MaxVisible = 5;
    private readonly List<ToastMessage> _messages = [];
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public IReadOnlyList<ToastMessage> Messages
    {
        get
        {
            lock (_gate) return _messages.ToList();
        }
    }

    public void Show(string text, ToastLevel level = ToastLevel.Info, int durationMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var message = new ToastMessage(Guid.NewGuid(), text.Trim(), level, DateTime.UtcNow, durationMs);

        lock (_gate)
        {
            _messages.Add(message);

            // 只保留最近若干条，避免刷屏
            while (_messages.Count > MaxVisible) _messages.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        // 到点自动消失。
        // 之前 DurationMs 只是记录在对象上、没人使用，提示会一直堆在屏幕右下角
        // （虽然上限是 5 条，但用户既关不掉也等不到它自己走）。
        if (durationMs > 0) ScheduleDismissal(message.Id, durationMs);
    }

    private void ScheduleDismissal(Guid id, int durationMs)
    {
        _ = Task.Delay(durationMs).ContinueWith(_ =>
        {
            try
            {
                Dismiss(id);
            }
            catch
            {
                // 定时清理失败不应影响任何调用方
            }
        }, TaskScheduler.Default);
    }

    public void Success(string text, int durationMs = 4000) => Show(text, ToastLevel.Success, durationMs);

    public void Warning(string text, int durationMs = 5000) => Show(text, ToastLevel.Warning, durationMs);

    public void Error(string text, int durationMs = 8000) => Show(text, ToastLevel.Danger, durationMs);

    public void Dismiss(Guid id)
    {
        var removed = false;
        lock (_gate)
        {
            removed = _messages.RemoveAll(m => m.Id == id) > 0;
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_messages.Count == 0) return;
            _messages.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
