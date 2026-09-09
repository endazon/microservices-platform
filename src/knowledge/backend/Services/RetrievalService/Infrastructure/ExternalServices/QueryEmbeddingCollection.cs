namespace RetrievalService.Infrastructure.ExternalServices;

// FR-02, FR-03, ADR-0016, IADR-0422 決定 3 (#336): **クエリの埋め込みモデルと検索対象コレクションの照合。**
//
// ADR-0016 はモデル別にコレクションを分けた —— 異なるモデルのベクトルは同一空間で比較できないため、
// **クエリの埋め込みは検索するコレクションと同じモデルで作られていなければ意味が無い。**
//
// 🔴 **その整合を保証する仕組みが、これまでプロセスの中に 1 つも無かった。**
//
// | 側 | コレクションを誰が決めるか |
// | --- | --- |
// | 取り込み（Ingestion） | **ゲートウェイの応答**（`EmbedApiResponse.Collection`）へ upsert する |
// | 検索（本サービス） | **自分の構成**（`Qdrant:CollectionName`）。応答の `Collection` を読んでいなかった |
//
// この非対称が #1215 の事故そのものである（点は `knowledge_chunks_deterministic_v1` に在るのに
// 検索側は `knowledge_chunks_voyage_3_5` を読み、**両サービスとも Ready のまま全件 0 件**になった）。
//
// 🔴 **A/B 測定の切替口（`Embedding:Routing:QueryProfile`）を開けると、この乖離は「0 件」では済まない** ——
// 次元の等しい 2 つのコレクション（voyage 1024 / deterministic 1024）で食い違うと、
// **別モデルの空間へ問い合わせて順位が返る**。nDCG はその順位を採点するので、
// **壊れた測定が数字として成立してしまう。**
//
// 🔴 **判定は 1 つである。** REST（`LlmGatewayEmbeddingService`）と gRPC（`LlmGatewayGrpcEmbeddingService`）の
// **両方がこの関数を呼ぶ** —— 輸送ごとに照合が分かれると、片方だけが守る最悪の食い違いが起こる
// （`EmbedUseCase` が越境判定について採ったのと同じ姿勢）。
public static class QueryEmbeddingCollection
{
    /// <summary>
    /// ゲートウェイが答えたコレクション（<paramref name="answered"/>）が、検索が読むコレクション
    /// （<paramref name="target"/>）と同じかを判定する。異なれば呼び出し側は空ベクトルへ降りる
    /// （＝`Embedded=false` と同じ既存の縮退。キーワード系統は生きる）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>答えが空のときは照合しない</b>（<c>true</c>）。空は「情報が無い」であって「不一致」ではない ——
    /// ゲートウェイは送信拒否のとき空を返すが、そのときは既に <c>Embedded=false</c> で降りている。
    /// ここで空を不一致に倒すと、**縮退の理由が 1 つ増えたように見えて原因追跡が濁る**。
    /// <para>
    /// <paramref name="target"/> が null のときも照合しない（合成点が読み先を渡していない構成）。
    /// 本番の合成（<c>Program.cs</c>）は常に渡す。
    /// </para>
    /// </remarks>
    public static bool Matches(string? answered, QueryEmbeddingTarget? target)
        => target is null
           || string.IsNullOrWhiteSpace(answered)
           || string.Equals(answered, target.Collection, StringComparison.Ordinal);
}

// FR-03, ADR-0009, ADR-0016, IADR-0422 決定 3 (#336): 検索が実際に読む Qdrant コレクション名。
//
// **値は合成点（`Program.cs`）が `QdrantVectorStore.ResolveCollectionName` から引いて渡す。**
// 埋め込みの客体が構成を別の規則で読み直すと、**ベクトルストアと別の答えを出し得る** ——
// それでは照合そのものが嘘になる（読み方の正は 1 つの関数に閉じる）。
public sealed record QueryEmbeddingTarget(string Collection);
