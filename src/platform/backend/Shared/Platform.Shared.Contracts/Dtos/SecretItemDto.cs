namespace Platform.Shared.Contracts.Dtos;

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3, IADR-0433, IADR-0453 (#1411), IADR-0456 (#1477):
// 秘密情報・接続設定の管理（BFF ↔ SPA 契約）。
//
// 🔴 **値を運ぶ応答型を置かない。** 値は要求（`UpdateSecretItemRequest.Value`）にだけ現れ、
// どの応答にも載らない —— 「書き込み専用。保存後は画面から読み出せない」（SC-22 入力規則）を
// 契約の形で閉じる。**値の長さ・ハッシュ・先頭数文字も載せない**（IADR-0433 決定 6）。
// BFF が作る値（MD5・生成した鍵。IADR-0456 決定 2・3）も同じく載せない。

// SC-22 主要素 1・3: 項目の一覧の 1 行（KV 単位。IADR-0453 決定 2）。
//
// `Status` は `set`（KV に版がある）/ `notSet`（KV が無い・現在版が削除済み）/
// `unavailable`（metadata が取れない）の 3 値（IADR-0453 決定 4）。
// 🔴 **`set` はプロパティに空でない値が入っていることを意味しない**（data を読む権限を持たないため）。
//
// `LastUpdatedBy` は **BFF が書いた版の記録と Vault の現在版が一致するときだけ**埋まる
// （IADR-0453 決定 3）。コンソール・bootstrap が書いた版では null（画面は「記録なし」）。
// 一致は版の番号と作成時刻の両方で見る（metadata を作り直すと番号は 1 から振り直される。IADR-0454 決定 3）。
//
// `PropertyDetails` は `Properties` と同じ並びの種別と秘密かどうか（IADR-0456 決定 1）。
// 既定値を持つのは契約の後方互換のためであり、BFF は常に埋める。
public record SecretItemStatusDto(
    string Item,
    string VaultPath,
    List<string> Properties,
    string Status,
    int? CurrentVersion,
    DateTimeOffset? LastUpdatedAt,
    string? LastUpdatedBy,
    List<SecretItemPropertyDto>? PropertyDetails = null);

// SC-22, IADR-0456 決定 1 (#1477): 書けるプロパティ 1 つの入力の形。
// `Kind` は `value`（値をそのまま書く）/ `md5-from-password`（平文のパスワードを送り、BFF が MD5 で書く）/
// `generate-rsa-pkcs1`（値を送らず、BFF が鍵を生成して書く）。
// `Sensitive` が false のプロパティは秘密ではない（画面は平文で入力させる）が、**書き込み専用なのは同じ**である。
public record SecretItemPropertyDto(string Name, string Kind, bool Sensitive);

// SC-22 主要素 2・入力/バリデーション: 1 回に 1 プロパティだけを書く（IADR-0453 決定 2）。
// `Reason` は任意で、監査ログへ残る（値は残らない）。
// IADR-0456 決定 3: 種別 `generate-rsa-pkcs1` は `Value` を**空文字**で送る（値を持たない。空でなければ 400）。
public record UpdateSecretItemRequest(string Property, string Value, string? Reason = null);

// SC-22: 書き込みの結果。**値を返さない。** 版と日時だけで「更新した事実」を示す（ADR-0095 §理由）。
// IADR-0456 決定 4: `SyncRequested` は同期先の ExternalSecret へ即時同期を依頼できたか。
// **false でも書き込みは成立している**（同期は既定の間隔で行われる）。
public record SecretItemWriteResultDto(
    string Item,
    string Property,
    int Version,
    DateTimeOffset UpdatedAt,
    bool SyncRequested = false);
