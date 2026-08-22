namespace GraphService.Api.Foundation.Domain;

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
    public DateTimeOffset UpdatedAt { get; private set; }

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

        Title = title;
        Attributes = attributes;
        BodyHash = bodyHash;
        UpdatedAt = updatedAt;
        return true;
    }
}
