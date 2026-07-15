using System.Reflection;

namespace SharpClaw.Core.Tests;

public sealed class CorePersistenceNeutralityTests
{
    [Fact]
    public void Core_assembly_does_not_reference_entity_framework()
    {
        var references = typeof(SharpClawCoreAssembly).Assembly
            .GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            reference => reference.Name?.Contains(
                "EntityFrameworkCore",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Core_type_graph_does_not_contain_persistence_entities()
    {
        var assembly = typeof(SharpClawCoreAssembly).Assembly;
        var violations = assembly.GetTypes()
            .SelectMany(GetReferencedTypes)
            .SelectMany(Unwrap)
            .Where(IsPersistenceType)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(violations);
    }

    private static IEnumerable<Type> GetReferencedTypes(Type type)
    {
        yield return type;

        if (type.BaseType is not null)
            yield return type.BaseType;

        foreach (var implemented in type.GetInterfaces())
            yield return implemented;

        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(flags))
            yield return field.FieldType;

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
            foreach (var parameter in property.GetIndexParameters())
                yield return parameter.ParameterType;
        }

        foreach (var @event in type.GetEvents(flags))
        {
            if (@event.EventHandlerType is not null)
                yield return @event.EventHandlerType;
        }

        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
                yield return parameter.ParameterType;
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Unwrap(element))
                yield return nested;
        }

        if (!type.IsGenericType)
            yield break;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Unwrap(argument))
                yield return nested;
        }
    }

    private static bool IsPersistenceType(Type type)
    {
        var fullName = type.FullName ?? string.Empty;
        return fullName.StartsWith(
                "SharpClaw.Contracts.Entities.",
                StringComparison.Ordinal)
            || type.Name.EndsWith("DB", StringComparison.Ordinal);
    }
}
