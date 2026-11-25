using Microsoft.Extensions.DependencyInjection;
using Stanza.Core.Workflows;

namespace Stanza.Composition;

/// <summary>组合根创建的类型与执行委托映射，使内核契约无需引用 DI 容器。</summary>
internal sealed record CommandRegistration(Type CommandType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Execute);

/// <summary>进程内命令分发器；只串行调度状态操作，不把网络等待排成全局队列。</summary>
internal sealed class CommandBus(
    IServiceProvider provider, IEnumerable<CommandRegistration> registrations,
    IStateDispatcher dispatcher, NowPlayingProjection projection, WorkflowTasks tasks) : ICommandBus
{
    private readonly IReadOnlyDictionary<Type, CommandRegistration> _handlers = registrations.ToDictionary(r => r.CommandType);

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        => tasks.Track(DispatchAsync(command, cancellationToken));

    private async Task<TResult> DispatchAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken)
    {
        // 窗口关闭与应用退出都能结束等待；已接受的状态变更由用例决定，取消不代表回滚。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tasks.Token);
        linked.Token.ThrowIfCancellationRequested();
        var handler = _handlers[command.GetType()];
        return await dispatcher.InvokeAsync(async () =>
        {
            linked.Token.ThrowIfCancellationRequested();
            try { return (TResult)(await handler.Execute(provider, command, linked.Token))!; }
            // 失败或取消前也可能已完成清理，让界面读到用例实际留下的状态。
            finally { projection.Publish(); }
        }, linked.Token);
    }
}

internal static class CommandRegistrationExtensions
{
    public static void AddCommand<TCommand, TResult, THandler>(this IServiceCollection services,
        Func<IServiceProvider, THandler> factory)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        // 映射常驻，处理器按次解析；业务对象仍通过构造参数获得依赖。
        services.AddTransient<ICommandHandler<TCommand, TResult>>(sp => factory(sp));
        services.AddSingleton(new CommandRegistration(typeof(TCommand), async (sp, command, token) =>
            await sp.GetRequiredService<ICommandHandler<TCommand, TResult>>().HandleAsync((TCommand)command, token)));
    }
}
