namespace GraphService.Domain;

// FR-17, UC-10, ADR-0033 決定 2: 探索のノード。
//
// **属性は複製である。** 正本は DocumentService であり、本サービスは ABAC 判定に要する文書属性を
// 辺と一緒に非正規化保持する（ADR-0033 決定 2）。複製するのは探索が**ホップごとに**認可述語を
// 評価するためで、属性がグラフ側に無いとホップごとに別サービスへ同期照会することになり探索が
// 実用にならない。追随は DocumentUpdated 購読で行う（#911）。
//
// **属性レコードが無いノードは不可視である**（IADR-0242 決定 12-3）。新規文書はイベントの初回同期
// まで、イベント欠損時は恒久的に、グラフ上に現れない。AbacNodeFilter の「属性キー欠落は不一致」と
// 同じ向き（欠落は安全側に倒す）。
public class GraphDocument
{
    public Guid DocumentId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    // FR-05, ADR-0033 決定 2: ABAC 判定に用いる文書属性の複製。
    // jsonb で持つ理由は認可の属性軸が増減するためである（現在の実効軸は confidentiality のみ。#516）。
    // 属性ごとに列を切ると軸が 1 つ増えるたびにマイグレーションが要る。
    public Dictionary<string, string> Attributes { get; private set; } = [];

    // FR-18, ADR-0033 決定 10: 本文のハッシュ。**却下した提案の解除判定にのみ用いる**
    // （「本文が変更されたこと」だけが条件で、変更量のしきい値は設けない）。利用は #914。
    public string? BodyHash { get; private set; }

    // ADR-0033 決定 2, IADR-0242 決定 12-4: 複製元イベントの更新時刻。
    // **順序ガードに使う** —— 保持中より古いイベントは適用しない。再配信・追い越しで
    // 「厳格化したのに緩和が復活する」事故を塞ぐ。
    //
    // 🔴 **陳腐化の判定には使えない**（planning#494 決定 2 / [[IADR-0357]]）。本欄は
    // `Document.UpdateMetadata` / `Document.Update`（本文を変えない更新）でも前進するため、
    // これで数えると**タグ・属性の整理そのものが指標を改善させる**。判定は BodyUpdatedAt を使う。
    public DateTimeOffset UpdatedAt { get; private set; }

    // FR-10, UC-05, SC-10, ADR-0006, ADR-0050 決定 2, planning#494 決定 2, [[IADR-0357]] (#1186):
    // **本文が変わったときにだけ前進する時刻**。陳腐化文書数（stale-documents）の起点である。
    //
    // 前進の条件は **BodyHash の変化のみ**であり、UpdatedAt とは独立に動く
    // （GraphDocumentSyncConsumer が却下解除・リンク抽出の契機に採っているのと同じ規律）。
    // **判定をドメインに置く**のは、消費側が増えたときに規律が割れないようにするためである。
    //
    // ⚠️ 既存行にはマイグレーションが UpdatedAt を写す（backfill）。UpdatedAt は実際の本文更新
    // 時刻以降であるため、既存文書は**実際より新しく見える** —— **偽陽性は出ず**、真に陳腐な
    // 文書も遅くとも移行から 1 しきい値以内には数えられる。「不明として数えない」を採らない
    // 理由は [[IADR-0357]] 決定 2（既存文書が母集合から恒久的に外れ、指標が 0 を返し続ける）。
    public DateTimeOffset BodyUpdatedAt { get; private set; }

    private GraphDocument() { }

    public static GraphDocument Create(
        Guid documentId,
        string title,
        Dictionary<string, string> attributes,
        string? bodyHash,
        DateTimeOffset updatedAt)
        => new()
        {
            DocumentId = documentId,
            Title = title,
            Attributes = attributes,
            BodyHash = bodyHash,
            UpdatedAt = updatedAt,
            // 新規行の初期値。**本文の時刻を別に知る手立てが無い**（イベントは指紋しか運ばない）。
            BodyUpdatedAt = updatedAt,
        };

    // ADR-0033 決定 2, IADR-0242 決定 12-4: 複製を更新する。
    // **古いイベントは適用しない**（冪等・追い越し耐性）。適用したかを返す。
    public bool TryApply(
        string title,
        Dictionary<string, string> attributes,
        string? bodyHash,
        DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
            return false;

        // 🔴 [[IADR-0357]] 決定 1 / ADR-0050 決定 2: **本文が変わったときだけ** BodyUpdatedAt を進める。
        // - `bodyHash` が null は「指紋化できなかった＝**不明**」であり、変更と見なさない
        //   （GraphDocumentSyncConsumer が却下解除・リンク抽出で採っているのと同じ向き）。
        // - **タグ・属性だけの更新はここを通らない**（指紋が同値のため）。planning#494 決定 2 の
        //   「棚卸し作業そのものが指標を改善させる」を塞ぐのはこの 1 行である。
        if (bodyHash is not null && !string.Equals(BodyHash, bodyHash, StringComparison.Ordinal))
            BodyUpdatedAt = updatedAt;

        Title = title;
        Attributes = attributes;
        BodyHash = bodyHash;
        UpdatedAt = updatedAt;
        return true;
    }
}
