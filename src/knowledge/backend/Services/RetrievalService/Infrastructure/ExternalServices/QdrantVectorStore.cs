using System.Globalization;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Indexing;
using Platform.Shared.Contracts.Dtos;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Infrastructure.ExternalServices;

// ADR-0009: Qdrant 実装（ポート = IVectorStore）
public class QdrantVectorStore(
    QdrantClient client, IConfiguration config, ILogger<QdrantVectorStore> logger,
    KeywordSearchMetrics metrics)
    : IVectorStore
{
    // FR-02 整合: 取り込み（IngestionService）と同一のコレクション名解決にする。
    // CollectionName を正とし、後方互換で Collection、既定 knowledge_chunks の順。
    private readonly string _collection = ResolveCollectionName(config);

    // FR-02, FR-03, #1116: コレクション名の解決を 1 か所に畳む。
    // **検索と、全文インデックスの有無を見る readiness（`QdrantFullTextIndexHealthCheck`）が
    // 必ず同じコレクションを指すため**である。別々に書くと「検索が見ているのとは別のコレクションの
    // 索引を健全と報告する」という、本 issue と同型の静かな食い違いが生まれる。
    internal static string ResolveCollectionName(IConfiguration config) =>
        config["Qdrant:CollectionName"] ?? config["Qdrant:Collection"] ?? "knowledge_chunks";

    // FR-03, #1116: 全文検索が引くペイロードキー。取り込み側
    // （`IngestionService.QdrantIngestionVectorStore.FullTextKey`）と同じ 1 つの値であること。
    internal const string FullTextKey = "text";

    // FR-03, FR-04, FR-17, #969: 文書 ID のペイロードキー。書き込み（BuildPayload）・復元（MapPayload）・
    // 削除（DeleteByDocumentAsync）・**文書 ID 絞り込み（BuildDocumentScopedFilter）**が同じ 1 つの値を使う。
    // 取り込み側 IngestionService の QdrantIngestionVectorStore と同じキーであること
    // （#539 が絞り込みキーで踏んだ「片方だけ直すと静かに割れる」型を持ち込まない）。
    internal const string DocumentIdKey = "document_id";

    public async Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK,
        ScopeFilter? filters,
        CancellationToken ct = default)
    {
        var filter = BuildAttributeFilter(filters);

        var results = await client.SearchAsync(_collection, queryVector, limit: (ulong)topK,
            filter: filter, cancellationToken: ct);

        return results.Select(r => MapPayload(r.Id.Uuid, r.Payload, r.Score)).ToList();
    }

    // FR-04, FR-17, ADR-0035, #969: 文書 ID 集合に絞った意味検索（二段検索の後段）。
    // **絞り込みの機構は新しくない** —— `DeleteByDocumentAsync` が既に `document_id` の Match で
    // 同じことをしている（単一 ID は `Match.Keyword`、集合は `Match.Keywords`）。同じ書き方に揃える。
    //
    // 🔴 **空集合は「該当なし」であり「全件」ではない**（`IVectorStore` の契約）。
    // **クライアントを呼ぶ前に返す** —— 空の Keywords を投げて Qdrant 側の解釈に委ねると、
    // 実装差で「全件」に化けうる（グラフが 0 件を返した瞬間に全文書へ広がる）。
    public async Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
        float[] queryVector, int topK,
        IReadOnlyCollection<Guid> documentIds,
        ScopeFilter? filters,
        CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return [];

        var results = await client.SearchAsync(_collection, queryVector, limit: (ulong)topK,
            filter: BuildDocumentScopedFilter(documentIds, filters), cancellationToken: ct);

        return results.Select(r => MapPayload(r.Id.Uuid, r.Payload, r.Score)).ToList();
    }

    // FR-04, FR-05, FR-17, #969: 「文書 ID ∈ 集合」と ABAC 条件を **Must（AND）** で並べる。
    // **ABAC を置き換えない**——文書 ID の制約は追加の条件である。
    // **`internal` にしてあるのは `BuildAttributeConditions` と同じ理由**で、
    // 実機 Qdrant なしで固定できる唯一の面だからである（テストが直接呼ぶ）。
    internal static Filter BuildDocumentScopedFilter(
        IReadOnlyCollection<Guid> documentIds, ScopeFilter? filters)
    {
        var conditions = new List<Condition>
        {
            new()
            {
                Field = new FieldCondition
                {
                    Key = DocumentIdKey,
                    Match = new Match
                    {
                        Keywords = new RepeatedStrings
                        {
                            Strings = { documentIds.Select(id => id.ToString()) }
                        }
                    }
                }
            }
        };
        conditions.AddRange(BuildAttributeConditions(filters));

        return new Filter { Must = { conditions } };
    }

    // FR-03: 全文検索（Qdrant のペイロード `text` への full-text Match）。
    //
    // 🔴 #1116: **この呼び出しは、全文ペイロードインデックスが在って初めて全文検索になる。**
    // 索引が無いときの挙動は Qdrant の版で変わる ——
    //   v1.9.2  : `RpcException`（下の catch が空リストへ縮退させる）
    //   v1.18.1 : **例外を投げず、部分文字列の全走査へ黙って落ちる**（実機で実測。[[IADR-0318]]）
    // 後者は「語でない断片が当たる」「語順に依存する」「全点走査になる」という別種の壊れ方であり、
    // **例外を捕まえるだけでは検出できない**。索引の存在そのものは readiness
    // （`QdrantFullTextIndexHealthCheck`）が見る。索引は取り込み側が起動時に張る
    // （`QdrantIngestionVectorStore.EnsureCollectionsAsync`）。
    public async Task<List<SearchResultDto>> KeywordSearchAsync(
        string query, int topK,
        ScopeFilter? filters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var conditions = BuildFullTextConditions(query);
        if (conditions.Count == 0)
            return [];

        // FR-05: ABAC 属性フィルタを全文検索にも適用（権限外文書を候補から除外）
        conditions.AddRange(BuildAttributeConditions(filters));

        try
        {
            var points = await client.ScrollAsync(_collection,
                filter: new Filter { Must = { conditions } },
                limit: (uint)topK, cancellationToken: ct);

            // Scroll は順序のみ（スコアなし）。順位を擬似スコア化し、融合は RRF が順位で行う。
            var rank = 0;
            return points.Result
                .Select(p => MapPayload(p.Id.Uuid, p.Payload, 1f / ++rank))
                .ToList();
        }
        catch (RpcException ex)
        {
            // 全文インデックス未作成等の場合はベクトルのみへ degrade（検索全体は失敗させない）。
            //
            // 🔴 #1116: **ログ 1 行で終わらせない。** 縮退は応答からは見えない（`SearchResponse` は
            // 縮退の有無を持たず、持たせない —— 存在秘匿・[[IADR-0313]] 決定 1）ので、
            // **応答の外側に数えられる痕跡を必ず残す**。#972 / #992 が「200 ＋ 空を緑にしない」を
            // 検証側で塞いだのと同じ向きの手当てである。
            metrics.RecordDegraded(KeywordSearchMetrics.BackendErrorReason);
            logger.LogWarning(ex,
                "Keyword search unavailable on collection {Collection}; falling back to vector-only. "
                + "全文ペイロードインデックスの有無は /health/ready（qdrant-fulltext-index）で確かめること",
                _collection);
            return [];
        }
    }

    // FR-03, #1118, [[IADR-0339]] 決定 1: クエリを 2 系統に割る。
    //
    //   - CJK 以外（識別子・型番・略語・英単語）→ `text`（`multilingual`）。**#1117 のままで、落とさない。**
    //   - CJK（日本語）→ `text_ngram`（アプリ側 2-gram。取り込み側と同じ `CjkBigramPayload.Encode`）。
    //
    // 🔴 `multilingual` は公式イメージ v1.18.1 では日本語の分かち書きを持たない（実測: 実配備チャンクの
    // 日本語 25 語のうち当たるのは 1 語）。日本語のクエリを `text` へそのまま投げてもほぼ当たらない。
    //
    // 両方が非空なら **両方 `must`**（「識別子も日本語も含む」チャンク）。Qdrant の全文 Match は
    // トークン集合の包含なので、2-gram を全部含む＝その並びを含む、に近い意味論になる。
    internal static List<Condition> BuildFullTextConditions(string query)
    {
        var (nonCjk, ngram) = CjkBigramPayload.SplitQuery(query);
        var conditions = new List<Condition>();
        if (nonCjk.Length > 0)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition { Key = FullTextKey, Match = new Match { Text = nonCjk } }
            });
        }

        if (ngram.Length > 0)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = CjkBigramPayload.PayloadKey,
                    Match = new Match { Text = ngram }
                }
            });
        }

        return conditions;
    }

    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043 / [[IADR-0151]] 決定 1・2）。
    //
    // **検索と同じ ABAC フィルタを渡して facet を呼ぶ。** 別経路（辞書・DocumentService）で数えると
    // 「検索には出るが候補に無い値」「候補にあるのに検索に出ない値」が生まれる。
    //
    // **facet は値と件数の対で返るが、件数はここで捨てる**（ADR-0043 決定 2。件数は値集合そのものより
    // 漏洩力が強い——「12 件だが自分の検索では 8 件」＝見えない文書が 4 件ある、が分かる）。
    // **件数がサービスの外へ出ないことがこの実装の要点である。**
    public async Task<List<string>> ListAttributeValuesAsync(
        string payloadKey,
        ScopeFilter? filters,
        CancellationToken ct = default)
    {
        var facets = await client.FacetAsync(_collection, payloadKey,
            filter: BuildAttributeFilter(filters), cancellationToken: ct);

        // **値だけを取り出す。`Count` は読まない**（ADR-0043 決定 2）。
        // 明示的なループにしてあるのは、**件数へ触れていないことが読んで分かる**ようにするためである。
        // 文字列でない facet 値（整数・真偽）は候補にならないため落とす。
        var values = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var hit in facets.Hits)
        {
            if (!hit.Value.HasStringValue) continue;
            var value = hit.Value.StringValue;
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }

        return [.. values];
    }

    private static Filter? BuildAttributeFilter(ScopeFilter? filters)
    {
        // FR-05: ABAC 多値 allow-list を Qdrant のペイロードフィルタに変換
        var conditions = BuildAttributeConditions(filters);
        return conditions.Count > 0 ? new Filter { Must = { conditions } } : null;
    }

    // FR-05: 各属性キーを「key ∈ AllowedValues」（Match.Keywords=いずれか一致）へ変換。
    // キー間は呼び出し側の Must（AND）で結合される。
    // IADR-0014: フィルタキー "attributes.{k}" は書き込み側のネスト構造体
    // `attributes -> { k: v }` に対する JSON パスとして実機 Qdrant で正しく解決されることを確認済み
    // （フラットキー書き込みとの組み合わせでは過剰除外が生じるため、書き込み側をネストへ統一した）。
    //
    // **［#539］キーの写像は `AttributeValueKeys.ToPayloadKey` に寄せた。**
    // 従前はここが `attributes.{key}` をハードコードしており、**`tags` を絞れなかった**——
    // 一方 #540 が入れた**値集合の照会側は `tags` を知っていた**ので、
    // **「候補には出るのに、その候補で絞れない」**という食い違いが生じていた（実測）。
    // **同じ知識を 2 か所に持たせない**。片方だけ直すと同じ食い違いが再発する。
    //
    // **`tags` はリスト項目なので、`Match.Keywords` はリストの要素いずれかに一致すれば真になる**
    // （Qdrant の配列ペイロードに対する既定の意味論。属性の単一値と同じ書き方で通る）。
    // **`internal` にしてある**——キーの写像は「候補に出る値」と「絞れる値」を一致させる要であり、
    // 実機 Qdrant なしで固定できる唯一の面である（#539 のテストが直接呼ぶ）。
    //
    // FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: スコープが**名前つき分岐**を運ぶときは、
    // 分岐ごとの連言を `Must` にまとめ、**それらを `Should`（OR）で束ねた入れ子フィルタ**を
    // 1 つの条件として返す。呼び出し側は従来どおり全条件を `Must` へ並べるだけでよい
    // （＝利用者指定の絞り込みと自動的に AND になる）。
    //
    // 🔴 **キー単位 union へ畳まない**（IADR-0253 決定 2 の反例）。
    internal static List<Condition> BuildAttributeConditions(ScopeFilter? filters)
    {
        if (filters is null)
            return [];

        var conditions = BuildKeyConditions(filters.Conjunction);

        var disjunction = BuildBranchDisjunction(filters.Branches);
        if (disjunction is not null)
            conditions.Add(disjunction);

        return conditions;
    }

    // FR-05: 「key ∈ AllowedValues」の連言（キー間は呼び出し側の Must で AND になる）。
    private static List<Condition> BuildKeyConditions(IReadOnlyList<AttributeFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return [];

        return filters
            .Where(f => f.AllowedValues.Count > 0)
            .Select(f => new Condition
            {
                Field = new FieldCondition
                {
                    Key = AttributeValueKeys.ToPayloadKey(f.Key),
                    Match = new Match { Keywords = new RepeatedStrings { Strings = { f.AllowedValues } } }
                }
            })
            .ToList();
    }

    // FR-19, IADR-0253 決定 1: 分岐の選言を 1 つの入れ子条件へ写す（分岐内 AND・分岐間 OR）。
    //
    // 🔴 **条件が 1 つも立たない分岐があれば選言そのものを省く。** 文書条件を持たない分岐は
    // 「**そのポリシーの範囲で全件許可**」を意味し、選言の中に「常に真」の枝があるのと同じである。
    // Qdrant は空の `Must` を持つ入れ子を表現できないため、**選言を省いて ABAC 由来の制約なしにする**
    // ——利用者指定の絞り込みだけが残る。緩む向きだが、それが空分岐の意味そのものである。
    private static Condition? BuildBranchDisjunction(
        IReadOnlyList<IReadOnlyList<AttributeFilter>>? branches)
    {
        if (branches is not { Count: > 0 })
            return null;

        var branchFilters = new List<Filter>();
        foreach (var branch in branches)
        {
            var conditions = BuildKeyConditions(branch);
            if (conditions.Count == 0)
                return null;   // 全件許可の分岐がある = 選言は制約にならない
            branchFilters.Add(new Filter { Must = { conditions } });
        }

        return new Condition
        {
            Filter = new Filter
            {
                Should = { branchFilters.Select(f => new Condition { Filter = f }) }
            }
        };
    }

    private static SearchResultDto MapPayload(
        string idUuid, IReadOnlyDictionary<string, Value> payload, float score) =>
        new(
            ChunkId: Guid.Parse(idUuid),
            DocumentId: Guid.TryParse(payload.GetValueOrDefault(DocumentIdKey)?.StringValue, out var docId) ? docId : Guid.Empty,
            DocumentTitle: payload.GetValueOrDefault("document_title")?.StringValue ?? "",
            // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3:
            // 🔴 **`text` は「索引テキスト」であって「本文」ではない。** 本文なしの文書は
            // 題名由来のメタデータをここへ載せて検索に当てているので、**射影を通して空にする** ——
            // 通さないと、メタデータが本文の抜粋として SC-02 と LLM の文脈へ出る。
            Text: DocumentBodyPresence.Excerpt(
                payload.GetValueOrDefault("text")?.StringValue, ExtractHasBody(payload)),
            Score: score,
            MarkdownUri: payload.GetValueOrDefault("markdown_uri")?.StringValue,
            // FR-05, FR-11: ABAC 属性（confidentiality 等）を DTO へ復元する。
            Attributes: ExtractAttributes(payload),
            // FR-03, SC-02（#642）: タグを DTO へ復元する。
            // **これが `[]` 固定だったため、本番でだけタグ列が空になっていた**
            // （`InMemoryVectorStore` は運ぶのでテストは緑のまま。[[IADR-0014]] と同型）。
            Tags: ExtractTags(payload),
            // FR-03, SC-02, #536: 更新日時を復元する（IADR-0149 決定 1・3）。
            UpdatedAt: ExtractUpdatedAt(payload),
            // FR-02, SC-02, #1193: 本文の有無を復元する（既定は「本文あり」）。
            HasBody: ExtractHasBody(payload));

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3:
    // 索引ペイロードの `has_body`（真偽）を復元する。
    // **キーが無ければ「本文あり」**である —— 本項目より前に索引された点はすべて本文チャンクであり、
    // 欠落はそれを正しく表す（backfill は要らない）。真偽以外の型で入っていたら既定へ倒す。
    internal static bool ExtractHasBody(IReadOnlyDictionary<string, Value> payload) =>
        payload.TryGetValue(DocumentBodyPresence.PayloadKey, out var value)
        && value.KindCase == Value.KindOneofCase.BoolValue
            ? value.BoolValue
            : DocumentBodyPresence.DefaultWhenAbsent;

    // FR-03, SC-02, #536: 索引ペイロードの `updated_at`（Unix epoch ミリ秒の整数）を復元する。
    // **キーが無ければ null を返す**（IADR-0149 決定 3）——本項目より前に索引されたチャンクは
    // 日時を持たない。DateTimeOffset.MinValue で埋めると「知らない」が「とても古い」に化け、
    // 並び順（#532）が嘘をつく。再索引が済むまでの縮退であって障害ではない。
    internal static DateTimeOffset? ExtractUpdatedAt(IReadOnlyDictionary<string, Value> payload) =>
        payload.TryGetValue("updated_at", out var value)
        && value.KindCase == Value.KindOneofCase.IntegerValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.IntegerValue)
            : null;

    // FR-05, FR-11: ペイロードに保持した ABAC 属性を `Attributes` 辞書へ復元する。
    // 復元しないと AiAnalysisService の機密区分判定（HighestConfidentiality）が常に
    // 「属性欠落 → restricted」へ縮退し、FR-11 の機密区分別ルーティングが無効化される。
    // IADR-0014（実機検証・Issue #71）: 実機 Qdrant はペイロードキーのドットをリテラルではなく
    // ネストパスとして解釈し、フラットキー書き込み + フラットキーフィルタでは過剰除外が発生することを
    // 確認した。書き込み（UpsertAsync）・フィルタ（BuildAttributeConditions）・復元をネスト構造体
    // `attributes -> { k: v }` へ統一する（選択肢C）。フラットキー復元パスは不要となったため削除した。
    internal static Dictionary<string, string> ExtractAttributes(
        IReadOnlyDictionary<string, Value> payload)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (payload.TryGetValue("attributes", out var nested)
            && nested.KindCase == Value.KindOneofCase.StructValue)
        {
            foreach (var (name, value) in nested.StructValue.Fields)
            {
                if (AsString(value) is { } text)
                    attributes[name] = text;
            }
        }

        return attributes;
    }

    // FR-03, SC-02（Issue #642）: ペイロードに保持したタグを `Tags` リストへ復元する。
    // 復元しないと SC-02 の結果一覧（`SearchResultsPage`）のタグ列が本番で常に空欄になる。
    // InMemoryVectorStore は `ChunkPayload.Tags` をそのまま運ぶためテストは緑のままであり、
    // IADR-0014 が記録した「テストは緑・本番は空」と同型の欠陥であった。
    // 表現は取り込み側（IngestionService の QdrantIngestionVectorStore.BuildChunkPayload）と同じ
    // `tags -> ListValue[StringValue]` である。書き込み（BuildPayload）もこれに揃えてある。
    //
    // **キーは `AttributeValueKeys.Tags` を使う**——#539 が絞り込み側（`BuildAttributeConditions`）を
    // `ToPayloadKey` へ寄せて「候補に出る値と絞れる値を 1 つの関数に持たせた」ため、
    // ここでリテラルを書くと**書き込み・復元・絞り込みでキーの真実が 3 つに割れる**。
    internal static List<string> ExtractTags(IReadOnlyDictionary<string, Value> payload)
    {
        var tags = new List<string>();

        if (payload.TryGetValue(AttributeValueKeys.Tags, out var value)
            && value.KindCase == Value.KindOneofCase.ListValue)
        {
            // 非スカラー（構造体・入れ子リスト）は AsString が null を返すので読み飛ばす。
            // 手で投入されたデータでも検索全体を失敗させない。
            foreach (var item in value.ListValue.Values)
            {
                if (AsString(item) is { } text)
                    tags.Add(text);
            }
        }

        return tags;
    }

    // 属性値をスカラー文字列へ写像する（属性は原則文字列だが、数値/真偽値も安全に文字列化する）。
    private static string? AsString(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.StringValue => value.StringValue,
        Value.KindOneofCase.IntegerValue => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
        Value.KindOneofCase.DoubleValue => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
        Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        _ => null
    };

    public async Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default)
    {
        var payload = BuildPayload(chunk);

        var point = new PointStruct
        {
            Id = new PointId { Uuid = chunk.ChunkId.ToString() },
            Vectors = chunk.Vector,
            Payload = { payload }
        };

        await client.UpsertAsync(_collection, [point], cancellationToken: ct);
    }

    // FR-03, FR-05, SC-02: チャンクの Qdrant ペイロードを構築する。
    // 純関数として切り出すのは、実機 Qdrant なしで書き込み表現を固定するためである
    // （復元側の ExtractAttributes / ExtractTags と同じ位置づけ。Issue #642）。
    // 取り込み側 IngestionService の QdrantIngestionVectorStore.BuildChunkPayload と同じ表現に揃える。
    internal static Dictionary<string, Value> BuildPayload(ChunkPayload chunk)
    {
        var payload = new Dictionary<string, Value>
        {
            [DocumentIdKey] = new Value { StringValue = chunk.DocumentId.ToString() },
            ["document_title"] = new Value { StringValue = chunk.DocumentTitle },
            ["text"] = new Value { StringValue = chunk.Text },
            ["markdown_uri"] = new Value { StringValue = chunk.MarkdownUri ?? "" },
        };
        // FR-03, SC-02, #536: 更新日時は **Unix epoch ミリ秒の整数**で持つ（IADR-0149 決定 1）。
        // 取り込み側（QdrantIngestionVectorStore.BuildChunkPayload）と**同じキー・同じ表現**であること。
        if (chunk.UpdatedAt is { } updatedAt)
            payload["updated_at"] = new Value { IntegerValue = updatedAt.ToUnixTimeMilliseconds() };

        // FR-02, SC-02, #1193: 本文なしの点だけが印を持つ（欠落＝本文あり。[[IADR-0358]] 決定 3）。
        // 取り込み側（`QdrantIngestionVectorStore.BuildChunkPayload`）と**同じキー・同じ書き方**であること。
        if (!chunk.HasBody)
            payload[DocumentBodyPresence.PayloadKey] = new Value { BoolValue = false };

        // FR-03, SC-02（#642）: タグをペイロードに保持する（結果一覧の表示用）。
        // **これは予防であって、本番の欠陥の是正ではない** —— `UpsertAsync` を呼ぶ本番コードは
        // 現在 1 つも無く（実測。書いているのは IngestionService の `QdrantIngestionVectorStore`）、
        // **本番でタグ列が空欄だったのは復元側（`MapPayload`）が原因である。**
        // それでも書くのは、`IVectorStore` が**書き込みを含むポート**で `InMemoryVectorStore` は
        // `Tags` を運んでおり、**Qdrant 実装だけが運ばない状態**を残すとこの口を使い始めた瞬間に
        // 同じ欠陥が再発するためである（[[IADR-0014]] は実装単位に掛かる）。作業仕様書 §追補。
        // 0 件のときはキー自体を書かない（`attributes` と同じ扱い・取り込み側とも一致する）。
        if (chunk.Tags.Count > 0)
        {
            var tagList = new ListValue();
            foreach (var t in chunk.Tags)
                tagList.Values.Add(new Value { StringValue = t });
            payload[AttributeValueKeys.Tags] = new Value { ListValue = tagList };
        }

        // FR-05: ABAC 属性をペイロードに保持（検索時フィルタ用）。
        // IADR-0014（選択肢C・実機検証済み）: ネスト構造体 `attributes -> { k: v }` へ統一する。
        if (chunk.Attributes.Count > 0)
        {
            var attrs = new Struct();
            foreach (var (k, v) in chunk.Attributes)
                attrs.Fields[k] = new Value { StringValue = v };
            payload["attributes"] = new Value { StructValue = attrs };
        }

        return payload;
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        await client.DeleteAsync(_collection,
            new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = DocumentIdKey,
                            Match = new Match { Keyword = documentId.ToString() }
                        }
                    }
                }
            }, cancellationToken: ct);
    }
}
