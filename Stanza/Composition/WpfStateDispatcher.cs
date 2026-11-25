using System.Windows.Threading;

namespace Stanza.Composition;

/// <summary>
/// 用 WPF 上下文串行执行状态操作。Unwrap 等待委托内的异步任务，而不只等待委托被投递执行。
/// </summary>
internal sealed class WpfStateDispatcher(Dispatcher dispatcher) : IStateDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
    public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default)
        => dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        => dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
}
