
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ProtocolFramework.Core.Serialization;

/// <summary>
/// 封包類型解析器
/// </summary>
public interface IPacketTypeResolver
{
    /// <summary>
    /// 嘗試取得類型的序列化名稱
    /// </summary>
    bool TryGetTypeName(Type type, [NotNullWhen(returnValue: true)] out string? name);

    /// <summary>
    /// 從序列化名稱解析類型
    /// </summary>
    Type ResolveType(string typeName);
}

public sealed class PacketTypeResolverOptions
{
    private readonly List<Type> _registeredTypes = [];
    private readonly List<Assembly> _registeredAssemblies = [];
    private readonly List<Func<Type, bool>> _typeFilters = [];

    /// <summary>
    /// 已註冊的類型
    /// </summary>
    public IReadOnlyList<Type> RegisteredTypes => _registeredTypes;

    /// <summary>
    /// 已註冊的 Assembly
    /// </summary>
    public IReadOnlyList<Assembly> RegisteredAssemblies => _registeredAssemblies;

    /// <summary>
    /// 類型篩選器（用於 Assembly 掃描）
    /// </summary>
    public IReadOnlyList<Func<Type, bool>> TypeFilters => _typeFilters;

    /// <summary>
    /// 是否允許 Fallback 到動態解析（預設：false）
    /// </summary>
    public bool AllowDynamicResolution { get; set; }

    /// <summary>
    /// 從指定 Assembly 註冊類型
    /// </summary>
    public PacketTypeResolverOptions RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!_registeredAssemblies.Contains(assembly))
            _registeredAssemblies.Add(assembly);

        return this;
    }

    /// <summary>
    /// 從 TMarker 所在的 Assembly 註冊類型
    /// </summary>
    public PacketTypeResolverOptions RegisterAssemblyOf<TMarker>() => RegisterAssembly(typeof(TMarker).Assembly);

    /// <summary>
    /// 新增類型篩選器（用於 Assembly 掃描時篩選類型）
    /// </summary>
    public PacketTypeResolverOptions AddTypeFilter(Func<Type, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _typeFilters.Add(filter);
        return this;
    }

    /// <summary>
    /// 篩選指定 namespace 下的類型
    /// </summary>
    public PacketTypeResolverOptions FilterByNamespace(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
            throw new ArgumentNullException(nameof(@namespace));

        return AddTypeFilter(t =>t.Namespace != null && t.Namespace.StartsWith(@namespace, StringComparison.Ordinal));
    }
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class RegisteredPacketTypeResolver : IPacketTypeResolver
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
