namespace Platform.Shared.Contracts.Dtos;

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3, IADR-0433, IADR-0453 (#1411):
// 秘密情報・接続設定の管理（BFF ↔ SPA 契約）。
//
// 🔴 **値を運ぶ応答型を置かない。** 値は要求（`UpdateSecretItemRequest.Value`）にだけ現れ、
// どの応答にも載らない —— 「書き込み専用。保存後は画面から読み出せない」（SC-22 入力規則）を
// 契約の形で閉じる。**値の長さ・ハッシュ・先頭数文字も載せない**（IADR-0433 決定 6）。

// SC-22 主要素 1・3: 項目の一覧の 1 行（KV 単位。IADR-0453 決定 2）。
//
// `Status` は `set`（KV に版がある）/ `notSet`（KV が無い・現在版が削除済み）/
// `unavailable`（metadata が取れない）の 3 値（IADR-0453 決定 4）。
// 🔴 **`set` はプロパティに空でない値が入っていることを意味しない**（data を読む権限を持たないため）。
//
// `LastUpdatedBy` は **BFF が書いた版の記録と Vault の現在版が一致するときだけ**埋まる
// （IADR-0453 決定 3）。コンソール・bootstrap が書いた版では null（画面は「記録なし」）。
public record SecretItemStatusDto(
    string Item,
    string VaultPath,
    List<string> Properties,
    string Status,
    int? CurrentVersion,
    DateTimeOffset? LastUpdatedAt,
    string? LastUpdatedBy);

// SC-22 主要素 2・入力/バリデーション: 1 回に 1 プロパティだけを書く（IADR-0453 決定 2）。
// `Reason` は任意で、監査ログへ残る（値は残らない）。
public record UpdateSecretItemRequest(string Property, string Value, string? Reason = null);

// SC-22: 書き込みの結果。**値を返さない。** 版と日時だけで「更新した事実」を示す（ADR-0095 §理由）。
public record SecretItemWriteResultDto(
    string Item,
    string Property,
    int Version,
    DateTimeOffset UpdatedAt);
