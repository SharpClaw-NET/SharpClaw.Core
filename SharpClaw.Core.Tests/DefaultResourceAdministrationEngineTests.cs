using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Core.Resources;
using SharpClaw.Core.State;

namespace SharpClaw.Core.Tests;

public sealed class DefaultResourceAdministrationEngineTests
{
    [Fact]
    public async Task SetForChannel_WhenSetIsMissing_PreservesNewSetForeignKeySignal()
    {
        var channel = new ChannelState { Title = "channel" };
        var host = new TestHost { Channel = channel };

        await new DefaultResourceAdministrationEngine().SetForChannelAsync(
            channel.Id,
            Request(),
            host);

        Assert.NotNull(channel.DefaultResourceSet);
        Assert.Equal(Guid.Empty, channel.DefaultResourceSetId);
        Assert.Same(channel.DefaultResourceSet, host.TrackedSet);
    }

    [Fact]
    public async Task SetForContext_WhenSetIsMissing_PreservesNewSetForeignKeySignal()
    {
        var context = new ChannelContextState
        {
            Name = "context",
            Agent = null!
        };
        var host = new TestHost { Context = context };

        await new DefaultResourceAdministrationEngine().SetForContextAsync(
            context.Id,
            Request(),
            host);

        Assert.NotNull(context.DefaultResourceSet);
        Assert.Equal(Guid.Empty, context.DefaultResourceSetId);
        Assert.Same(context.DefaultResourceSet, host.TrackedSet);
    }

    private static SetDefaultResourcesRequest Request() =>
        new(new Dictionary<string, Guid?>
        {
            ["task"] = Guid.NewGuid()
        });

    private sealed class TestHost : IDefaultResourceAdministrationHost
    {
        public ChannelState? Channel { get; init; }
        public ChannelContextState? Context { get; init; }
        public DefaultResourceSetState? TrackedSet { get; private set; }

        public Task<ChannelState?> LoadChannelWithDefaultResourcesAsync(
            Guid channelId,
            CancellationToken ct) =>
            Task.FromResult(Channel);

        public Task<ChannelContextState?> LoadContextWithDefaultResourcesAsync(
            Guid contextId,
            CancellationToken ct) =>
            Task.FromResult(Context);

        public Task<IReadOnlyList<Guid>> ListChannelIdsForContextAsync(
            Guid contextId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public void TrackDefaultResourceSet(
            DefaultResourceSetState defaultResourceSet) =>
            TrackedSet = defaultResourceSet;

        public void RemoveDefaultResourceEntry(DefaultResourceEntryState entry)
        {
        }

        public Task SaveAsync(
            Func<SharpClaw.Core.Chat.ChatRuntimeInvalidationPlan?>?
                buildInvalidationPlan,
            CancellationToken ct) =>
            Task.CompletedTask;
    }
}
