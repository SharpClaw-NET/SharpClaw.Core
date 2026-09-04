using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace SharpClaw.Core.Kernel;

internal static class KernelExecutionScope
{
    private static readonly AsyncLocal<ScopeState?> CurrentScope = new();

    public static IServiceProvider Current(IServiceProvider rootProvider)
    {
        var current = CurrentScope.Value;
        if (current is null || !ReferenceEquals(current.RootProvider, rootProvider))
            return rootProvider;
        current.EnsureUsable();
        return current.ServiceProvider;
    }

    public static async ValueTask<TResult> RunAsync<TResult>(
        IServiceProvider rootProvider,
        Func<IServiceProvider, ValueTask<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(operation);

        if (CurrentScope.Value is { } current
            && ReferenceEquals(current.RootProvider, rootProvider))
        {
            current.EnsureUsable();
            return await operation(current.ServiceProvider);
        }

        var scope = rootProvider.CreateAsyncScope();
        var state = new ScopeState(rootProvider, scope);
        var previous = CurrentScope.Value;
        CurrentScope.Value = state;
        try
        {
            return await operation(scope.ServiceProvider);
        }
        finally
        {
            CurrentScope.Value = previous;
            await state.ReleaseAsync();
        }
    }

    public static Task<TResult> RunRetainedAsync<TResult>(
        Func<ValueTask<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var state = CurrentScope.Value
            ?? throw new InvalidOperationException("A retained kernel operation requires an active execution scope.");
        if (!state.TryRetain())
            throw new InvalidOperationException("The kernel execution scope is no longer active.");

        try
        {
            return Task.Run(
                () => RunRetainedCoreAsync(state, operation),
                CancellationToken.None);
        }
        catch
        {
            state.ReleaseAfterSchedulingFailure();
            throw;
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
            current.EnsureUsable();
            await foreach (var item in operation(current.ServiceProvider)
                               .WithCancellation(cancellationToken))
                yield return item;
            yield break;
        }

        var scope = rootProvider.CreateAsyncScope();
        var state = new ScopeState(rootProvider, scope);
        var previous = CurrentScope.Value;
        CurrentScope.Value = state;
        try
        {
            await foreach (var item in operation(scope.ServiceProvider)
                               .WithCancellation(cancellationToken))
                yield return item;
        }
        finally
        {
            CurrentScope.Value = previous;
            await state.ReleaseAsync();
        }
    }

    private static async Task<TResult> RunRetainedCoreAsync<TResult>(
        ScopeState state,
        Func<ValueTask<TResult>> operation)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = state;
        try
        {
            return await operation();
        }
        finally
        {
            CurrentScope.Value = ReferenceEquals(previous, state) ? null : previous;
            await state.ReleaseAsync();
        }
    }

    private sealed class ScopeState(
        IServiceProvider rootProvider,
        AsyncServiceScope scope)
    {
        private readonly object _gate = new();
        private int _leases = 1;
        private bool _disposalStarted;

        public IServiceProvider RootProvider { get; } = rootProvider;
        public IServiceProvider ServiceProvider => scope.ServiceProvider;

        public void EnsureUsable()
        {
            lock (_gate)
            {
                if (_disposalStarted || _leases == 0)
                    throw new InvalidOperationException("The kernel execution scope is no longer active.");
            }
        }

        public bool TryRetain()
        {
            lock (_gate)
            {
                if (_disposalStarted || _leases == 0)
                    return false;
                _leases++;
                return true;
            }
        }

        public async ValueTask ReleaseAsync()
        {
            var dispose = false;
            lock (_gate)
            {
                if (_leases == 0)
                    throw new InvalidOperationException("The kernel execution scope lease was released more than once.");
                _leases--;
                if (_leases == 0)
                {
                    _disposalStarted = true;
                    dispose = true;
                }
            }

            if (dispose)
                await scope.DisposeAsync();
        }

        public void ReleaseAfterSchedulingFailure() =>
            ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }
}
