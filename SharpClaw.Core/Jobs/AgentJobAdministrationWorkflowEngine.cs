namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral orchestration for job administration state transitions.
/// Query projection and diagnostic retrieval belong to the host.
/// </summary>
public sealed class AgentJobAdministrationWorkflowEngine(
    AgentJobAdministrationEngine jobs,
    AgentJobLifecycleEngine lifecycle)
{
    public AgentJobAdministrationWorkflowEngine()
        : this(new AgentJobAdministrationEngine(), new AgentJobLifecycleEngine())
    {
    }

    public async Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId,
        string actionKeyPrefix,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        return jobs.JobMatchesActionPrefix(job, actionKeyPrefix);
    }

    public Task<AgentJobState?> CancelAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
        => ApplyAsync(
            jobId,
            static (job, engine) => engine.Cancel(
                job.Status,
                DateTimeOffset.UtcNow),
            host,
            ct);

    public Task<AgentJobState?> StopAsync(
        Guid jobId,
        string? requiredActionPrefix,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
        => ApplyAsync(
            jobId,
            (job, engine) => engine.Stop(
                job.Status,
                job.ActionKey,
                requiredActionPrefix,
                DateTimeOffset.UtcNow),
            host,
            ct);

    public Task<AgentJobState?> PauseAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
        => ApplyAsync(
            jobId,
            static (job, engine) => engine.Pause(job.Status),
            host,
            ct);

    public Task<AgentJobState?> ResumeAsync(
        Guid jobId,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
        => ApplyAsync(
            jobId,
            static (job, engine) => engine.Resume(job.Status),
            host,
            ct);

    public async Task RecordTokensAsync(
        IReadOnlyList<Guid> jobIds,
        int promptTokens,
        int completionTokens,
        IAgentJobAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        ArgumentNullException.ThrowIfNull(host);

        if (jobIds.Count == 0)
            return;

        var loaded = await host.LoadJobsByIdsAsync(jobIds, ct);
        var byId = loaded.ToDictionary(job => job.Id);
        var ordered = jobIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(job => job is not null)
            .Select(job => job!)
            .ToList();

        if (ordered.Count == 0)
            return;

        jobs.ApplyTokenUsage(ordered, promptTokens, completionTokens);
        await host.PersistStatesAsync(ordered, ct);
    }

    private async Task<AgentJobState?> ApplyAsync(
        Guid jobId,
        Func<AgentJobState, AgentJobLifecycleEngine, AgentJobLifecycleDecision>
            decide,
        IAgentJobAdministrationHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decide);
        ArgumentNullException.ThrowIfNull(host);

        var job = await host.LoadJobAsync(jobId, ct);
        if (job is null)
            return null;

        var decision = decide(job, lifecycle);
        jobs.ApplyLifecycleState(job, decision);
        await host.PersistDecisionAsync(job, decision, ct);
        return job;
    }
}

/// <summary>
/// Host persistence port for compact job state and ordered lifecycle events.
/// </summary>
public interface IAgentJobAdministrationHost
{
    Task<AgentJobState?> LoadJobAsync(Guid jobId, CancellationToken ct);

    Task<IReadOnlyList<AgentJobState>> LoadJobsByIdsAsync(
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct);

    Task PersistDecisionAsync(
        AgentJobState job,
        AgentJobLifecycleDecision decision,
        CancellationToken ct);

    Task PersistStatesAsync(
        IReadOnlyList<AgentJobState> jobs,
        CancellationToken ct);
}
