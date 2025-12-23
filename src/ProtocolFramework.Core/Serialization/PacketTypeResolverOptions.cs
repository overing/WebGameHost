
using System.Reflection;

namespace ProtocolFramework.Core.Serialization;

public sealed class PacketTypeResolverOptions
{
    private readonly List<Type> _registeredTypes = new List<Type>();
    private readonly List<Assembly> _registeredAssemblies = new List<Assembly>();
    private readonly List<Func<Type, bool>> _typeFilters = new List<Func<Type, bool>>();

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
    public bool AllowDynamicResolution { get; set; } = false;

    /// <summary>
    /// 註冊單一類型
    /// </summary>
    public PacketTypeResolverOptions Register<T>() where T : class
    {
        return Register(typeof(T));
    }

    /// <summary>
    /// 註冊單一類型
    /// </summary>
    public PacketTypeResolverOptions Register(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        
        if (!_registeredTypes.Contains(type))
            _registeredTypes.Add(type);
        
        return this;
    }

    /// <summary>
    /// 註冊多個類型
    /// </summary>
    public PacketTypeResolverOptions Register(params Type[] types)
    {
        if (types == null) throw new ArgumentNullException(nameof(types));

        foreach (var type in types)
            Register(type);

        return this;
    }

    /// <summary>
    /// 從指定 Assembly 註冊類型
    /// </summary>
    public PacketTypeResolverOptions RegisterAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));

        if (!_registeredAssemblies.Contains(assembly))
            _registeredAssemblies.Add(assembly);

        return this;
    }

    /// <summary>
    /// 從 TMarker 所在的 Assembly 註冊類型
    /// </summary>
    public PacketTypeResolverOptions RegisterAssemblyOf<TMarker>()
    {
        return RegisterAssembly(typeof(TMarker).Assembly);
    }

    /// <summary>
    /// 新增類型篩選器（用於 Assembly 掃描時篩選類型）
    /// </summary>
    public PacketTypeResolverOptions AddTypeFilter(Func<Type, bool> filter)
    {
        if (filter == null) throw new ArgumentNullException(nameof(filter));

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

        return AddTypeFilter(t => 
            t.Namespace != null && t.Namespace.StartsWith(@namespace, StringComparison.Ordinal));
    }

    /// <summary>
    /// 篩選具有指定 Attribute 的類型
    /// </summary>
    public PacketTypeResolverOptions FilterByAttribute<TAttribute>() where TAttribute : Attribute
    {
        return AddTypeFilter(t => t.GetCustomAttribute<TAttribute>() != null);
    }
}
