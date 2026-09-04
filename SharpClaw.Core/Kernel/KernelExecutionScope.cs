using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace SharpClaw.Core.Kernel;

internal static class KernelExecutionScope
{
    private static readonly AsyncLocal<ScopeState?> CurrentScope = new();

    public static IServiceProvider Current(IServiceProvider rootProvider) =>
        ReferenceEquals(CurrentScope.Value?.RootProvider, rootProvider)
            ? CurrentScope.Value.ServiceProvider
            : rootProvider;

    public static async ValueTask<TResult> RunAsync<TResult>(
        IServiceProvider rootProvider,
        Func<IServiceProvider, ValueTask<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(operation);

        if (CurrentScope.Value is { } current
            && ReferenceEquals(current.RootProvider, rootProvider))
        {
            return await operation(current.ServiceProvider);
        }

        await using var scope = rootProvider.CreateAsyncScope();
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ScopeState(rootProvider, scope.ServiceProvider);
        try
        {
            return await operation(scope.ServiceProvider);
        }
        finally
        {
            CurrentScope.Value = previous;
        }
    }

    public static async IAsyncEnumerable<TResult> StreamAsync<TResult>(
        IServiceProvider rootProvider,
        Func<IServiceProvider, IAsyncEnumerable<TResult>> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(operation);

        if (CurrentScope.Value is { } current
            && ReferenceEquals(current.RootProvider, rootProvider))
        {
            await foreach (var item in operation(current.ServiceProvider)
                               .WithCancellation(cancellationToken))
                yield return item;
            yield break;
        }

        await using var scope = rootProvider.CreateAsyncScope();
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ScopeState(rootProvider, scope.ServiceProvider);
        try
        {
            await foreach (var item in operation(scope.ServiceProvider)
                               .WithCancellation(cancellationToken))
                yield return item;
        }
        finally
        {
            CurrentScope.Value = previous;
        }
    }

    private sealed record ScopeState(
        IServiceProvider RootProvider,
        IServiceProvider ServiceProvider);
}
