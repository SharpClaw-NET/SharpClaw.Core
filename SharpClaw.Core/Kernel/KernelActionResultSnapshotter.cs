using System.Text.Json;

namespace SharpClaw.Core.Kernel;

internal interface IKernelImmutableResult;

public interface IKernelActionResultSnapshotter
{
    TResult Snapshot<TResult>(TResult result);
}

public sealed class JsonKernelActionResultSnapshotter : IKernelActionResultSnapshotter
{
    public TResult Snapshot<TResult>(TResult result)
    {
        if (result is null)
            return result;
        if (result is IKernelImmutableResult || IsKnownImmutable(result.GetType()))
            return result;
        if (result is JsonElement json)
            return (TResult)(object)json.Clone();
        if (result is KernelProviderRequestEnvelope request)
            return (TResult)(object)SnapshotProviderRequest(request);
        if (result is KernelProviderTransportResult transport)
        {
            return (TResult)(object)new KernelProviderTransportResult(
                transport.Completion is null ? null : SnapshotJson(transport.Completion),
                transport.IsStreaming,
                transport.Stream);
        }
        if (result is KernelProviderCompletionEnvelope completion)
        {
            return (TResult)(object)new KernelProviderCompletionEnvelope(
                SnapshotProviderRequest(completion.Request),
                SnapshotJson(completion.Completion));
        }

        try
        {
            var runtimeType = result.GetType();
            var (serializationType, snapshotType) = GetSnapshotContractTypes<TResult>(runtimeType);
            var snapshotJson = JsonSerializer.SerializeToElement(
                result,
                serializationType,
                KernelJson.Options);
            return (TResult)(snapshotJson.Deserialize(snapshotType, KernelJson.Options)
                ?? throw new KernelActionExecutionException(
                    $"The action result snapshot for '{snapshotType.FullName}' is null."));
        }
        catch (KernelActionExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new KernelActionExecutionException(
                $"The action result type '{result.GetType().FullName}' requires an immutable result or a " +
                $"snapshot codec. The codec reported: {exception.Message}");
        }
    }

    private static bool IsKnownImmutable(Type type) =>
        type.IsPrimitive ||
        type.IsEnum ||
        type == typeof(string) ||
        type == typeof(decimal) ||
        type == typeof(Guid) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) ||
        type == typeof(Uri) ||
        type == typeof(Version);

    private static (Type SerializationType, Type SnapshotType) GetSnapshotContractTypes<TResult>(
        Type runtimeType)
    {
        if (typeof(TResult) != typeof(object) || runtimeType.IsArray)
        {
            var contractType = typeof(TResult) == typeof(object) ? runtimeType : typeof(TResult);
            return (contractType, contractType);
        }

        var contracts = runtimeType.GetInterfaces().Append(runtimeType).ToArray();
        var dictionary = contracts.FirstOrDefault(type =>
            type.IsGenericType &&
            type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)));
        if (dictionary is not null)
        {
            return (
                dictionary,
                typeof(Dictionary<,>).MakeGenericType(dictionary.GetGenericArguments()));
        }

        var set = contracts.FirstOrDefault(type =>
            type.IsGenericType &&
            type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IReadOnlySet<>) || definition == typeof(ISet<>)));
        if (set is not null)
            return (set, typeof(HashSet<>).MakeGenericType(set.GetGenericArguments()));

        var sequence = contracts.FirstOrDefault(type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return sequence is null
            ? (runtimeType, runtimeType)
            : (sequence, typeof(List<>).MakeGenericType(sequence.GetGenericArguments()));
    }

    private static KernelProviderRequestEnvelope SnapshotProviderRequest(
        KernelProviderRequestEnvelope request) =>
        request with { Messages = request.Messages.ToArray() };

    private static T SnapshotJson<T>(T value)
    {
        var runtimeType = value!.GetType();
        var json = JsonSerializer.SerializeToElement(value, runtimeType, KernelJson.Options);
        return (T)(json.Deserialize(runtimeType, KernelJson.Options)
            ?? throw new KernelActionExecutionException(
                $"The action result snapshot for '{runtimeType.FullName}' is null."));
    }
}
