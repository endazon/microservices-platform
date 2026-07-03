using System.Security.Cryptography;
using System.Text;

namespace ConversionService.Worker.Services;

// FR-12, ADR-0012: 「同一原本の再変換は版で管理し、重複登録を避ける」ための決定的 GUID 生成。
// SourceId＋原本パスから RFC 4122 v5（名前ベース, SHA-1）相当の GUID を導出する。
// 再変換時も同じ DocumentId となり、文書管理サービスで重複登録（冪等性）を判定できる。
public static class DeterministicGuid
{
    // 変換サービス固有の名前空間 GUID（固定値）。
    private static readonly Guid Namespace = new("6f2b1c0e-9d3a-4e57-8b2f-4c1e2a7d5f90");

    public static Guid ForDocument(Guid sourceId, string originalPath)
        => Create(Namespace, $"{sourceId:N}:{originalPath}");

    // RFC 4122 §4.3 name-based UUID (version 5, SHA-1)。
    private static Guid Create(Guid namespaceId, string name)
    {
        var nsBytes = namespaceId.ToByteArray();
        SwapByteOrder(nsBytes); // .NET の GUID バイト順 → RFC のネットワーク順

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var toHash = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, toHash, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, toHash, nsBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(toHash);

        var result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);

        // version 5
        result[6] = (byte)((result[6] & 0x0F) | (5 << 4));
        // variant RFC 4122
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        SwapByteOrder(result); // RFC のネットワーク順 → .NET の GUID バイト順
        return new Guid(result);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
