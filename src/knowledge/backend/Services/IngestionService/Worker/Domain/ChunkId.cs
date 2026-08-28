using System.Security.Cryptography;

namespace IngestionService.Worker.Domain;

// FR-02: 冪等な再取り込みのため、チャンク ID を documentId + chunkIndex から
// 決定的に導出する。同一文書・同一位置のチャンクは常に同じ ID になり、
// 旧チャンク削除に失敗しても upsert が上書きとなって重複を防ぐ。
// 暗号用途ではなく ID 導出のためのハッシュであり MD5 で十分。
public static class ChunkId
{
    public static Guid Derive(Guid documentId, int chunkIndex)
    {
        Span<byte> buffer = stackalloc byte[20];
        documentId.TryWriteBytes(buffer[..16]);
        BitConverter.TryWriteBytes(buffer[16..], chunkIndex);

        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(buffer, hash);
        return new Guid(hash);
    }
}
