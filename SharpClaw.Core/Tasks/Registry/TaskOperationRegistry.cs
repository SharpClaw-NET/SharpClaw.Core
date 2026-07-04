using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Tasks.Registry;

/// <summary>
/// Single authoritative registry for module task operation descriptors. Core
/// intrinsic C# statements bypass this registry and are handled by the task
/// language runtime before module dispatch.
/// </summary>
public sealed class TaskOperationRegistry
{
    private readonly Dictionary<string, TaskOperationDescriptor> _byMethod =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskOperationDescriptor> _byKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _methodRegistrationCounts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _keyRegistrationCounts =
        new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Shared singleton; populated by modules during startup.</summary>
    public static readonly TaskOperationRegistry Default = new();

    /// <summary>
    /// Clear all registered descriptors. Intended for test fixtures that
    /// need to seed a deterministic descriptor set; not for production use.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _byMethod.Clear();
            _byKey.Clear();
            _methodRegistrationCounts.Clear();
            _keyRegistrationCounts.Clear();
        }
    }

    public void UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        lock (_lock)
        {
            var operationKeysHandledByMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var methodName in _byMethod
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                var descriptor = _byMethod[methodName];
                operationKeysHandledByMethods.Add(descriptor.OperationKey);

                if (DecrementCount(_methodRegistrationCounts, methodName) == 0)
                    _byMethod.Remove(methodName);

                if (DecrementCount(_keyRegistrationCounts, descriptor.OperationKey) == 0)
                    _byKey.Remove(descriptor.OperationKey);
            }

            foreach (var operationKey in _byKey
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (operationKeysHandledByMethods.Contains(operationKey))
                    continue;

                if (DecrementCount(_keyRegistrationCounts, operationKey) == 0)
                    _byKey.Remove(operationKey);
            }
        }
    }

    /// <summary>
    /// Register an operation descriptor. Duplicate method names or operation keys from
    /// different owners are rejected with <see cref="InvalidOperationException"/>.
    /// Re-registering the same descriptor (same owner, same key, same method) is a no-op.
    /// </summary>
    public void Register(TaskOperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_lock)
        {
            if (descriptor.MethodName is not null)
            {
                if (_byMethod.TryGetValue(descriptor.MethodName, out var existing))
                {
                    if (existing.OperationKey == descriptor.OperationKey && existing.OwnerId == descriptor.OwnerId)
                    {
                        IncrementCount(_methodRegistrationCounts, descriptor.MethodName);
                        IncrementCount(_keyRegistrationCounts, descriptor.OperationKey);
                        return; // idempotent re-registration
                    }

                    throw new InvalidOperationException(
                        $"Task operation method '{descriptor.MethodName}' is already registered " +
                        $"by owner '{existing.OwnerId}' with key '{existing.OperationKey}'. " +
                        $"Attempted to re-register by '{descriptor.OwnerId}' with key '{descriptor.OperationKey}'.");
                }
                _byMethod[descriptor.MethodName] = descriptor;
                _methodRegistrationCounts[descriptor.MethodName] = 1;
            }

            if (_byKey.TryGetValue(descriptor.OperationKey, out var existingKey))
            {
                if (existingKey.OwnerId != descriptor.OwnerId)
                    throw new InvalidOperationException(
                        $"Task operation key '{descriptor.OperationKey}' is already registered " +
                        $"by owner '{existingKey.OwnerId}'. " +
                        $"Attempted to re-register by '{descriptor.OwnerId}'.");
                // Same owner, different method sharing the same key (e.g. HTTP verbs) — allowed.
                // _byKey keeps the first registration; all methods are accessible via _byMethod.
                IncrementCount(_keyRegistrationCounts, descriptor.OperationKey);
            }
            else
            {
                _byKey[descriptor.OperationKey] = descriptor;
                _keyRegistrationCounts[descriptor.OperationKey] = 1;
            }
        }
    }

    private static void IncrementCount(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static int DecrementCount(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out var count))
            return 0;

        count--;
        if (count <= 0)
        {
            counts.Remove(key);
            return 0;
        }

        counts[key] = count;
        return count;
    }

    /// <summary>
    /// Look up a descriptor by script method name. Returns <see langword="null"/>
    /// if the method name is not registered.
    /// </summary>
    public TaskOperationDescriptor? FindByMethod(string methodName)
    {
        lock (_lock)
            return _byMethod.GetValueOrDefault(methodName);
    }

    /// <summary>
    /// Look up a descriptor by operation key. Returns <see langword="null"/>
    /// if the key is not registered.
    /// </summary>
    public TaskOperationDescriptor? FindByKey(string operationKey)
    {
        lock (_lock)
            return _byKey.GetValueOrDefault(operationKey);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="methodName"/> is
    /// registered as a descriptor-backed module method.
    /// </summary>
    public bool IsRegisteredMethod(string methodName)
    {
        lock (_lock)
            return _byMethod.ContainsKey(methodName);
    }
}
