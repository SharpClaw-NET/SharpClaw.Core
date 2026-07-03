using System.Threading.Channels;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Host-agnostic runtime state for one active task instance.
/// </summary>
public sealed class TaskRuntimeEntry : IDisposable
{
    private readonly CancellationTokenSource _cancellationSource;
    private readonly Channel<TaskOutputEvent> _outputChannel;
    private readonly TaskPauseGate _pauseGate;
    private readonly TimeProvider _timeProvider;
    private long _sequenceCounter;

    /// <summary>
    /// Creates a task runtime entry from host-supplied runtime resources.
    /// </summary>
    public TaskRuntimeEntry(
        CancellationTokenSource cancellationSource,
        Channel<TaskOutputEvent> outputChannel,
        TaskPauseGate pauseGate,
        TimeProvider? timeProvider = null)
    {
        _cancellationSource = cancellationSource;
        _outputChannel = outputChannel;
        _pauseGate = pauseGate;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates an entry with the canonical SharpClaw task-runtime channel and pause semantics.
    /// </summary>
    public static TaskRuntimeEntry Create(CancellationToken linkedToken, TimeProvider? timeProvider = null)
    {
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        var outputChannel = Channel.CreateUnbounded<TaskOutputEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        return new TaskRuntimeEntry(cancellationSource, outputChannel, new TaskPauseGate(), timeProvider);
    }

    /// <summary>
    /// Cancellation token for this runtime entry.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationSource.Token;

    /// <summary>
    /// Reads structured output events emitted by this task instance.
    /// </summary>
    public ChannelReader<TaskOutputEvent> OutputReader => _outputChannel.Reader;

    /// <summary>
    /// Creates the task-execution handle for this entry.
    /// </summary>
    public TaskRuntimeInstance CreateInstance(Guid instanceId) => new(instanceId, this);

    /// <summary>
    /// Marks the entry paused so cooperative execution waits at pause checkpoints.
    /// </summary>
    public void Pause() => _pauseGate.Pause();

    /// <summary>
    /// Resumes cooperative execution and releases pause waiters.
    /// </summary>
    public void Resume() => _pauseGate.Resume();

    /// <summary>
    /// Waits while the entry is paused, returning immediately while it is running.
    /// </summary>
    public Task WaitIfPausedAsync(CancellationToken ct) => _pauseGate.WaitIfPausedAsync(ct);

    /// <summary>
    /// Returns the next monotonic task-output sequence number.
    /// </summary>
    public long IncrementSequence() => Interlocked.Increment(ref _sequenceCounter);

    /// <summary>
    /// Emits a structured task-output event if the output stream is still open.
    /// </summary>
    public async Task WriteEventAsync(TaskOutputEventType type, string? data, CancellationToken ct = default)
    {
        if (!await _outputChannel.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
            return;

        var evt = new TaskOutputEvent(type, IncrementSequence(), _timeProvider.GetUtcNow(), data);
        _outputChannel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Cancels this running task entry.
    /// </summary>
    public Task CancelAsync() => _cancellationSource.CancelAsync();

    /// <summary>
    /// Completes the output stream for readers waiting on the task instance.
    /// </summary>
    public void CompleteOutput(Exception? error = null)
    {
        if (error is null)
            _outputChannel.Writer.TryComplete();
        else
            _outputChannel.Writer.TryComplete(error);
    }

    /// <summary>
    /// Releases the entry's cancellation source.
    /// </summary>
    public void Dispose() => _cancellationSource.Dispose();
}

/// <summary>
/// Lightweight execution handle used by task interpreters and step executors.
/// </summary>
public sealed class TaskRuntimeInstance
{
    private readonly TaskRuntimeEntry _entry;

    internal TaskRuntimeInstance(Guid instanceId, TaskRuntimeEntry entry)
    {
        InstanceId = instanceId;
        _entry = entry;
    }

    /// <summary>
    /// Identifier of the running task instance.
    /// </summary>
    public Guid InstanceId { get; }

    /// <summary>
    /// Cancellation token for this task instance's execution.
    /// </summary>
    public CancellationToken CancellationToken => _entry.CancellationToken;

    /// <summary>
    /// Emits a structured task-output event.
    /// </summary>
    public Task WriteEventAsync(TaskOutputEventType type, string? data, CancellationToken ct = default)
        => _entry.WriteEventAsync(type, data, ct);

    /// <summary>
    /// Waits while this task instance is paused.
    /// </summary>
    public Task WaitIfPausedAsync(CancellationToken ct)
        => _entry.WaitIfPausedAsync(ct);

    /// <summary>
    /// Returns the next monotonic task-output sequence number.
    /// </summary>
    public long IncrementSequence() => _entry.IncrementSequence();
}

/// <summary>
/// Cooperative async pause gate shared by SharpClaw task runtimes.
/// </summary>
public sealed class TaskPauseGate
{
    private volatile TaskCompletionSource _signal = Signaled();

    /// <summary>
    /// Switches the gate into a paused state.
    /// </summary>
    public void Pause()
    {
        if (_signal.Task.IsCompleted)
            Interlocked.Exchange(ref _signal, Paused());
    }

    /// <summary>
    /// Releases all current and future waiters until the next pause.
    /// </summary>
    public void Resume()
    {
        var next = Signaled();
        Interlocked.Exchange(ref _signal, next).TrySetResult();
    }

    /// <summary>
    /// Waits until the gate is resumed.
    /// </summary>
    public Task WaitIfPausedAsync(CancellationToken ct)
        => _signal.Task.WaitAsync(ct);

    private static TaskCompletionSource Paused()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Signaled()
    {
        var source = Paused();
        source.TrySetResult();
        return source;
    }
}
