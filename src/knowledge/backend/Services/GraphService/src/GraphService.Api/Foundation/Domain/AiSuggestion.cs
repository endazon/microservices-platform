namespace GraphService.Api.Foundation.Domain;

// FR-18, ADR-0033 決定 7・10: AI 提案（リンク候補・タグ候補）の 3 状態。
//
// **リンク提案とタグ提案を 1 つの表に同居させる。** SC-21 が「同じ一覧にタブまたはフィルタで
// 同居させる。**画面を分けると片方が忘れられる**」と定めており、表を分けると一覧の実装が
// 2 経路に割れて同じ事故を招く。種別は `Kind` で判別する。
public static class SuggestionKind
{
    // 辺（両端の文書 ＋ 辺の型）の提案。
    public const string Link = "link";
    // タグ（1 文書 ＋ タグ値）の提案。
    public const string Tag = "tag";

    public static bool IsValid(string value) => value is Link or Tag;
}

// FR-18, ADR-0033 決定 7: 3 状態。
//
// | 値 | 意味 | 探索結果 |
// | pending  | 承認待ち | **含めない**（承認 UI にのみ現れる） |
// | approved | 承認済み | 含める（確定リンクとして） |
// | rejected | 却下     | **含めない**。再提示の抑止に用いる |
//
// 🔴 **探索側にフィルタは要らない。** 未承認の提案は `Edge` を持たないため（承認で初めて
// `EdgeProvenance.AiApproved` の辺が生まれる）、**絞る対象が存在しない**のが正しい形である。
public static class SuggestionState
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static bool IsValid(string value) => value is Pending or Approved or Rejected;
}

// FR-18, ADR-0033 決定 7・10。
public class AiSuggestion
{
    public Guid Id { get; private set; }
    public string Kind { get; private set; } = SuggestionKind.Link;

    // リンク提案では起点、タグ提案では対象文書。**どちらの種別でも必ず入る。**
    public Guid SourceDocumentId { get; private set; }
    // リンク提案のみ。
    public Guid? TargetDocumentId { get; private set; }
    public Guid? EdgeTypeId { get; private set; }
    // タグ提案のみ。
    public string? TagValue { get; private set; }

    // FR-18: なぜ関連と判断したか（SC-21 主要素 6「提案の根拠」）。
    public string Rationale { get; private set; } = string.Empty;

    public string State { get; private set; } = SuggestionState.Pending;

    // ADR-0033 §結果 フォローアップ: **却下回数を保持する。**
    // 「同一の組み合わせが 3 回以上却下された件数」を SC-10 で観測し、再提案が煩わしいという
    // 声が出た時点で変更量のしきい値を入れられるようにするためである。**後から足せない値なので
    // 最初から数える。**
    public int RejectedCount { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    // ADR-0033 決定 10: 却下時点の両端の**本文指紋**。本文そのものは持たない
    // （§結果「1 レコードは辺の両端 ID・却下日時・解除状態のみであり、本文を持たない」）。
    //
    // 🔴 **指紋の作り方はここでは決めない。** 呼び出し側が与える不透明な文字列として扱う。
    // 現状 `DocumentUpdated` は本文ハッシュを持たず、`UpdatedAt` はタグ・属性だけの更新でも
    // 動くため、決定 10 の「本文が変更されたこと**のみ**」を判定できない（#911 / 仕様書 §未決事項）。
    public string? SourceFingerprint { get; private set; }
    public string? TargetFingerprint { get; private set; }

    // ADR-0033 決定 10 の具体化: **解除は却下レコードの削除ではなく「無効化」として記録する。**
    // 「いつ却下し、いつ何によって解除されたか」を追えるようにするためである。
    //
    // ⚠️ 保持するのは**直近の 1 周期**である（却下 → 解除 → 再却下で上書きされる）。
    // ADR が「1 レコードは…却下日時・解除状態のみ」と単数で書いているためこの形を採った。
    // 全周期の履歴が要るなら別表になる（仕様書 §未決事項）。**回数だけは累積する。**
    public DateTimeOffset? ReinstatedAt { get; private set; }
    public string? ReinstatedReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private AiSuggestion() { }

    // FR-18: リンク提案を起こす。
    public static AiSuggestion CreateLink(
        Guid sourceDocumentId, Guid targetDocumentId, Guid edgeTypeId,
        string rationale, DateTimeOffset createdAt)
    {
        if (sourceDocumentId == Guid.Empty || targetDocumentId == Guid.Empty)
            throw new ArgumentException("両端の文書 ID が要る", nameof(sourceDocumentId));
        if (sourceDocumentId == targetDocumentId)
            throw new ArgumentException("自己ループは提案しない", nameof(targetDocumentId));

        return new AiSuggestion
        {
            Id = Guid.NewGuid(),
            Kind = SuggestionKind.Link,
            SourceDocumentId = sourceDocumentId,
            TargetDocumentId = targetDocumentId,
            EdgeTypeId = edgeTypeId,
            Rationale = rationale ?? string.Empty,
            State = SuggestionState.Pending,
            CreatedAt = createdAt,
        };
    }

    // FR-18, SC-21: タグ提案を起こす。
    // **値域の検査はここでは行わない** —— SC-09 のタグ辞書は DocumentService 側にあり、
    // ドメインからは引けない。呼び出し側（サービス層）が辞書と突き合わせる。
    public static AiSuggestion CreateTag(
        Guid documentId, string tagValue, string rationale, DateTimeOffset createdAt)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("文書 ID が要る", nameof(documentId));
        if (string.IsNullOrWhiteSpace(tagValue))
            throw new ArgumentException("タグ値が要る", nameof(tagValue));

        return new AiSuggestion
        {
            Id = Guid.NewGuid(),
            Kind = SuggestionKind.Tag,
            SourceDocumentId = documentId,
            TagValue = tagValue.Trim(),
            Rationale = rationale ?? string.Empty,
            State = SuggestionState.Pending,
            CreatedAt = createdAt,
        };
    }

    // ADR-0033 決定 7: 承認。**pending からのみ。**
    //
    // 🔴 **approved / rejected からの遷移を許さない。** 承認済みを再承認すると辺が二重に生まれ、
    // 却下済みを承認 UI を経ずに承認できると、却下の記録（再提示の抑止）が黙って壊れる。
    public bool TryApprove()
    {
        if (State != SuggestionState.Pending) return false;
        State = SuggestionState.Approved;
        return true;
    }

    // ADR-0033 決定 7・10: 却下。**pending からのみ。**
    //
    // 却下回数を増やし、両端の本文指紋を控える。指紋は解除の判定に使う。
    public bool TryReject(string? sourceFingerprint, string? targetFingerprint, DateTimeOffset at)
    {
        if (State != SuggestionState.Pending) return false;

        State = SuggestionState.Rejected;
        RejectedCount++;
        RejectedAt = at;
        SourceFingerprint = sourceFingerprint;
        TargetFingerprint = targetFingerprint;
        // 直近の周期を上書きする（前回の解除の記録は残さない。§未決事項）。
        ReinstatedAt = null;
        ReinstatedReason = null;
        return true;
    }

    // ADR-0033 決定 10: **両端いずれかの本文が変更された時点で却下を解除する。**
    // 変更量のしきい値は設けない —— 判定条件は「変わったこと」のみである。
    //
    // 🔴 **本メソッドには本番コードからの呼び出し元が存在しない。**
    // 発火させるのは `DocumentUpdated` の購読（#911）であり、それが未着手だからである。
    // **この「未配線」は `AiSuggestionWiringTests` が機械で固定している** ——
    // 呼び出し元が生まれた瞬間に当該テストが落ち、実装者は受け入れ基準の書き直しを迫られる。
    // 散文の注意書きは読まれないので、テストで留める。
    //
    // 戻り値は「解除したか」。解除しなかった場合、状態は一切変わらない。
    public bool TryReinstate(string? sourceFingerprint, string? targetFingerprint, DateTimeOffset at)
    {
        if (State != SuggestionState.Rejected) return false;

        var sourceChanged = !string.Equals(SourceFingerprint, sourceFingerprint, StringComparison.Ordinal);
        var targetChanged = TargetDocumentId is not null
            && !string.Equals(TargetFingerprint, targetFingerprint, StringComparison.Ordinal);

        if (!sourceChanged && !targetChanged) return false;

        State = SuggestionState.Pending;
        ReinstatedAt = at;
        // SC-21: 再提示には**理由を必ず添える**。どちらの端点が変わったのかまで残す
        // ——「文書が更新された」だけでは、利用者は何を見直せばよいか判らない。
        ReinstatedReason = (sourceChanged, targetChanged) switch
        {
            (true, true) => "both",
            (true, false) => "source",
            _ => "target",
        };
        // 指紋は新しい値へ進める。進めないと、次の更新で「変わっていない」と誤判定する
        // （比較対象が古い指紋のまま固定されるため）。
        SourceFingerprint = sourceFingerprint;
        if (TargetDocumentId is not null) TargetFingerprint = targetFingerprint;
        return true;
    }
}
