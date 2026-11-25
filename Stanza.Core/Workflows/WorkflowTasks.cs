namespace Stanza.Core.Workflows;

/// <summary>应用任务的取消与等待边界，退出时不遗留后台工作流。</summary>
internal sealed class WorkflowTasks : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _pending = [];
    // 这里只保护任务登记集合，不作为业务状态锁，更不能持有它等待网络。
    private readonly object _gate = new();
    public CancellationToken Token => _stopping.Token;

    /// <summary>登记调用方会等待的命令/查询，保留原任务的结果、异常和取消语义。</summary>
    public Task<T> Track<T>(Task<T> task)
    {
        lock (_gate)
            _pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate)
                _pending.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>在当前上下文启动无需返回结果的工作流，集中观察异常；不会另开线程。</summary>
    public Task Run(Func<Task> action)
    {
        if (Token.IsCancellationRequested)
            return Task.CompletedTask;
        var task = ObserveAsync(action);
        lock (_gate)
            _pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate)
                _pending.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task ObserveAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("应用工作流失败", ex); }
    }

    public void Cancel() => _stopping.Cancel();
    /// <summary>取消后等待已登记任务结束；调用方必须保持状态调度器可用，供回包完成收尾。</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            // 等待期间登记集合仍可能变化，不能只等待第一次捕获到的任务。
            Task[] pending;
            lock (_gate)
            {
                _pending.RemoveWhere(task => task.IsCompleted);
                pending = [.. _pending];
            }
            if (pending.Length == 0)
                return;
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }
    }
    public void Dispose() => _stopping.Dispose();
}
