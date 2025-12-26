
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ProtocolFramework.Core.Serialization;

public sealed class RegisteredPacketTypeResolver : IPacketTypeResolver
{
    private readonly ConcurrentDictionary<string, Type> _nameToType = new();
    private readonly ConcurrentDictionary<Type, string> _typeToName = new();
    private readonly bool _allowDynamicResolution;

    public RegisteredPacketTypeResolver() : this(new PacketTypeResolverOptions()) { }

    public RegisteredPacketTypeResolver(PacketTypeResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _allowDynamicResolution = options.AllowDynamicResolution;

        // 註冊明確指定的類型
        foreach (var type in options.RegisteredTypes)
        {
            RegisterType(type);
        }

        // 從 Assembly 掃描並註冊類型
        foreach (var assembly in options.RegisteredAssemblies)
        {
            RegisterFromAssembly(assembly, options.TypeFilters);
        }
    }

    public bool TryGetTypeName(Type type, [NotNullWhen(returnValue: true)] out string? name)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (_typeToName.TryGetValue(type, out name))
            return true;

        if (_allowDynamicResolution)
        {
            // 動態註冊並回傳
            name = type.FullName;
            if (name != null)
            {
                RegisterType(type);
                return true;
            }
        }

        return false;
    }

    public Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentNullException(nameof(typeName));

        if (_nameToType.TryGetValue(typeName, out var type))
            return type;

        if (_allowDynamicResolution)
        {
            type = ResolveTypeDynamic(typeName);
            if (type != null)
            {
                RegisterType(type);
                return type;
            }
        }

        throw new TypeLoadException(
            $"Type '{typeName}' is not registered. Register it in PacketTypeResolverOptions.");
    }

    private void RegisterType(Type type)
    {
        var name = type.FullName;
        if (string.IsNullOrEmpty(name))
            return;

        _nameToType.TryAdd(name, type);
        _typeToName.TryAdd(type, name);
    }

    private void RegisterFromAssembly(Assembly assembly, IReadOnlyList<Func<Type, bool>> filters)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 處理部分載入失敗的情況
            types = ex.Types.OfType<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (!IsValidPacketType(type))
                continue;

            if (filters.Count > 0 && !filters.Any(f => f(type)))
                continue;

            RegisterType(type);
        }
    }

    private static bool IsValidPacketType(Type type)
    {
        return type != null
            && type.IsClass
            && !type.IsAbstract
            && !type.IsGenericTypeDefinition
            && type.FullName != null;
    }

    private static Type? ResolveTypeDynamic(string typeName)
    {
        // 優先使用 Type.GetType
        var type = Type.GetType(typeName);
        if (type != null)
            return type;

        // 搜尋已載入的 Assembly
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName);
            if (type != null)
                return type;
        }

        return null;
    }
}
