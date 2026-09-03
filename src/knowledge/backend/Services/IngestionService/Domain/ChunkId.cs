using System.Security.Cryptography;

namespace IngestionService.Domain;

// FR-02: 冪等な再取り込みのため、チャンク ID を documentId + chunkIndex から
// 決定的に導出する。同一文書・同一位置のチャンクは常に同じ ID になり、
// 旧チャンク削除に失敗しても upsert が上書きとなって重複を防ぐ。
// 暗号用途ではなく ID 導出のためのハッシュであり MD5 で十分。
public static class ChunkId
{
    // FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1: メタデータ点（本文なしの文書を
    // 索引へ載せるための 1 点）の索引位置。**本文チャンクの索引は 0 以上しか取らない**ので衝突しない。
    // ペイロードの `chunk_index` にもこの値が入り、「これは本文チャンクではない」が索引を直接読んでも分かる。
    internal const int MetadataChunkIndex = -1;

    public static Guid Derive(Guid documentId, int chunkIndex)
    {
        Span<byte> buffer = stackalloc byte[20];
        documentId.TryWriteBytes(buffer[..16]);
        BitConverter.TryWriteBytes(buffer[16..], chunkIndex);

        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(buffer, hash);
        return new Guid(hash);
    }

    // FR-02, FR-03, ADR-0070 決定 4, #1193: 本文なしの文書のメタデータ点の ID。
    // 本文チャンクと同じ導出（決定的・冪等）で、位置だけが本文の取らない値である。
    public static Guid DeriveMetadata(Guid documentId) => Derive(documentId, MetadataChunkIndex);
}
