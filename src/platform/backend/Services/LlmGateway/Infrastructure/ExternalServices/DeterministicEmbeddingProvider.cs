using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using System.Text;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-02, FR-03, ADR-0016, ADR-0017, #992 案 2, [[IADR-0313]]:
// **決定的なローカル埋め込み**（ティアA＝社外送信なし。使い捨て統合スタック専用）。
//
// ■ なぜ在るのか
//
// 統合スタック（`.github/workflows/integration-stack.yml`）には埋め込みの鍵が無い。
// 取り込み（`DocumentUpdatedConsumer`）は `Embedded=false` のチャンクを索引しないので、
// **索引に 1 点も入らず、`POST /bff/search` は「検索が壊れている」ときと同じ `200 ＋ 空` を返す**
// （[[IADR-0255]] / #992）。#992 の「やること 2」が挙げた候補のうち
// **「決定的なローカル埋め込み」**がこれである。
//
// ■ 🔴 越境判定を緩めるものではない
//
// 本プロバイダは **HTTP を一切行わない**（プロセス内で計算するだけ）。したがって
// **ティアA（セルフホスト＝社外送信なし）の定義をそのまま満たす**。
// `EmbeddingEgress.AllowedTiers`（機密区分 × ティア）も `EmbeddingRouter.Route` も触っていない。
// confidential / restricted がティアB / C へ出ないという fail-closed は無傷である。
//
// ■ 🔴 これは検索**品質**を担保しない
//
// 表層の文字 3-gram の重なりしか見ていない。意味的な近さは無い。
// **nDCG などの品質評価に使ってはならない**（それは実モデル＝ADR-0017 の仕事である）。
// 既定は `Enabled: false`（`appsettings.json`）で、`Program.cs` が有効時に起動警告を出す。
//
// ■ 設計
//
//   1. 小文字化した本文から**文字 3-gram** を取る（日本語には語境界が無いので単語分割はしない）
//   2. 各 3-gram を **FNV-1a 64bit** で hash し、次元へ写す（`string.GetHashCode` は
//      プロセスごとにランダム化されるので**使えない**——同じ本文が実行ごとに別ベクトルになる）
//   3. hash の 1 bit を符号に使う（hashing trick。衝突の偏りを打ち消す）
//   4. L2 正規化する（Qdrant の距離は Cosine）
//
// **`purpose`（Query / Index）は使わない。** Ruri v3 の 1+3 プレフィクス（#809）は
// モデルが非対称に符号化することを前提にした作法であり、ハッシングにその概念は無い。
// 付けると**クエリ側だけ 3-gram が増えて文書から系統的に遠ざかる**（＝当たらなくなる）。
public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    // 3-gram。1 では文字の出現頻度だけ、5 では短いクエリが 3-gram を持てない。
    internal const int GramSize = 3;

    public Task<float[]> EmbedAsync(
        string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        => Task.FromResult(Embed(text, dimensions));

    // テストと呼び出し側の両方から使う純粋関数（同じ入力 → 同じ出力）。
    internal static float[] Embed(string text, int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions,
                "埋め込み次元は正の値が必要です（ルーターの決定と一致させること）。");

        var vector = new float[dimensions];
        var normalized = (text ?? string.Empty).ToLowerInvariant();

        for (var i = 0; i + GramSize <= normalized.Length; i++)
        {
            var hash = Fnv1a64(normalized.AsSpan(i, GramSize));
            var index = (int)(hash % (ulong)dimensions);
            // 最上位 bit を符号に使う（次元の選択に使う下位 bit とは独立）。
            var sign = (hash & 0x8000_0000_0000_0000UL) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        var norm = 0.0;
        foreach (var v in vector) norm += (double)v * v;
        norm = Math.Sqrt(norm);

        if (norm <= 0)
        {
            // 3-gram が 1 つも取れない（空・2 文字以下）か、符号が完全に打ち消し合った場合。
            // **零ベクトルを返さない** —— Cosine 距離の空間では零ベクトルは比較できず、
            // Qdrant が点を拒む。決定的な単位ベクトルへ倒す（どの短文も同じ点になるが、
            // 「索引に入らない」より「意味の無い点として入る」ほうが観測可能である）。
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);

        return vector;
    }

    // FNV-1a（64bit）。**プロセス跨ぎ・プラットフォーム跨ぎで同じ値**を返すことが要件である。
    private static ulong Fnv1a64(ReadOnlySpan<char> chars)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        Span<byte> bytes = stackalloc byte[GramSize * 4]; // UTF-8 は 1 文字最大 4 バイト
        var written = Encoding.UTF8.GetBytes(chars, bytes);

        var hash = offsetBasis;
        for (var i = 0; i < written; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }
        return hash;
    }
}
