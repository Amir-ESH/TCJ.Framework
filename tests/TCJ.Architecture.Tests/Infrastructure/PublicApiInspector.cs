using System.Reflection;

namespace TCJ.Architecture.Tests.Infrastructure;

internal static class PublicApiInspector
{
    public static IReadOnlyCollection<Type> GetReferencedTypes(Type publicType)
    {
        var references = new HashSet<Type>();

        AddType(publicType.BaseType, references);
        foreach (var interfaceType in publicType.GetInterfaces())
        {
            AddType(interfaceType, references);
        }

        AddGenericParameterConstraints(publicType.GetGenericArguments(), references);

        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (var constructor in publicType.GetConstructors(flags))
        {
            AddParameters(constructor.GetParameters(), references);
            AddGenericParameterConstraints(constructor.GetGenericArguments(), references);
        }

        foreach (var method in publicType.GetMethods(flags))
        {
            AddType(method.ReturnType, references);
            AddParameters(method.GetParameters(), references);
            AddGenericParameterConstraints(method.GetGenericArguments(), references);
        }

        foreach (var property in publicType.GetProperties(flags))
        {
            AddType(property.PropertyType, references);
            AddParameters(property.GetIndexParameters(), references);
        }

        foreach (var field in publicType.GetFields(flags))
        {
            AddType(field.FieldType, references);
        }

        foreach (var eventInfo in publicType.GetEvents(flags))
        {
            AddType(eventInfo.EventHandlerType, references);
        }

        return references;
    }

    private static void AddParameters(IEnumerable<ParameterInfo> parameters, ISet<Type> references)
    {
        foreach (var parameter in parameters)
        {
            AddType(parameter.ParameterType, references);
        }
    }

    private static void AddGenericParameterConstraints(IEnumerable<Type> types, ISet<Type> references)
    {
        foreach (var type in types.Where(type => type.IsGenericParameter))
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
            {
                AddType(constraint, references);
            }
        }
    }

    private static void AddType(Type? type, ISet<Type> references)
    {
        if (type is null || type.IsGenericParameter)
        {
            return;
        }

        if (type.HasElementType)
        {
            AddType(type.GetElementType(), references);
            return;
        }

        if (type.IsGenericType)
        {
            if (type.IsGenericTypeDefinition)
            {
                references.Add(type);
            }
            else
            {
                AddType(type.GetGenericTypeDefinition(), references);
            }

            foreach (var argument in type.GetGenericArguments())
            {
                AddType(argument, references);
            }

            return;
        }

        references.Add(type);
    }
}
