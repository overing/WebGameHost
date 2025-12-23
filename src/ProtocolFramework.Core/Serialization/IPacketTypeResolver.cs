
using System.Diagnostics.CodeAnalysis;

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
