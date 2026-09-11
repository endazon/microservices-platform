---
title: 個人資料の退職時 30 日窓の起点をアカウント無効化日として明示的に持ち、構成で人事由来の退職日へ持ち替えられるようにする
issue: "#1392"
plan_refs:
  - FR-19
  - SC-19
  - ADR-0036
  - ADR-0082
adr_refs:
  - IADR-0428
  - IADR-0301
  - IADR-0329
  - IADR-0385
status: done
created: 2026-09-11
---

# 作業仕様書: 退職時の保持起点（retention anchor）を持つ（#1392）

## 起点

- issue #1392（親 #438 の非クラスタ残作業の最後の 1 件）。2026-09-11 の #438 監査が
  「ADR-0082 実装側残作業 3 の受け皿が無い」と判定した。
- 計画 `ADR-0082` 決定 5 / フォローアップ 3:

  > D-09 の期限計算は、暫定期間中はアカウント無効化日を起点とする（決定 5）。
  > 配備後の切り替えを見越し、**起点を構成または属性で持つ形にしておくこと**。

- 計画 `ADR-0036` D-09: 個人資料に対する管理者閲覧権の有効期限は「退職日から 30 日間」。
  起点だけを `ADR-0082` 決定 5 が部分改定した（**期間 30 日は変えない**）。
- `ADR-0036` §未解決事項 2（退職後 30 日経過後の資料の扱い＝削除／保持したまま閲覧不可／残置）は
  **未決のまま**である。本作業は起点と判定だけを置き、**窓が閉じた後の動作は実装しない。**

### 現況の実測（基点 `origin/develop` `bfb835d6`）

```console
$ git rev-parse --is-shallow-repository
false
$ git grep -niE "DisabledAt|RetentionAnchor|無効化日|退職日" -- src
（保持起点は 0 件。DataSource の無効化端点と SC-19 / SC-20 の告知文言だけが当たる）
$ git grep -n "RetentionDays" src/knowledge/backend/Services/DocumentService/Domain/PrivateNote.cs
16:    public const int RetentionDays = 90;
```

🔴 **`PrivateNote.RetentionDays = 90` は D-09 の 30 日ではない。** あれは `ADR-0037` 決定 5・16 の
「論理削除の保管期間／版履歴の日数条件」であり、退職とは無関係の別規則である。**混ぜない。**

## 対象範囲

- 対象:
  - 保持起点（retention anchor）を**明示的な概念として持つ**。値は IdP（Keycloak）の利用者属性に置き、
    起点の**出所**を構成 `RetentionAnchor:Source` で選ぶ（`account-disabled-at` 既定 / `hr-leave-date`）。
  - SC-17 の無効化で起点を刻み、再有効化で消す。
  - 起点が未供給／読めないときは**削除の対象にしない**（fail-safe。「0 件」と「未供給」を分ける）。
  - 判定（30 日経過したか）を純関数として置き、陽性対照・陰性対照で固定する。
- 対象外:
  - **人事システム連携そのもの**（`ADR-0082` 決定 1 は方式を委ねたが、選ぶ入力＝人事システムの実体が
    計画に無い）。`hr-leave-date` を選んでも**書き手は居ない**＝起点は未供給のまま＝削除しない。
  - **窓が閉じた後の動作**（削除／閲覧不可化／残置）。`ADR-0036` §未解決事項 2 が未決。
  - **管理者閲覧の実体**（D-08 の緊急アクセスも含め、実装は存在しない。`git grep -n "D-09" -- src` は 0 件）。
  - 稼働クラスタへの反映（本作業は一切触れていない）。

## 設計

### 置き場

**AuthorizationService（platform ユニット）に閉じる。**

- 起点は「SC-17 でアカウントが無効化された日」（`ADR-0082` 決定 5）であり、SC-17 の後段は
  `ADR-0064` 決定 により認可サービスである。**事象が起きる場所に起点を置く。**
- 個人資料を持つ DocumentService（knowledge ユニット）へ判定を置くと、利用者名簿への
  gRPC 依存（`Services:AuthorizationServiceGrpc`）を新設することになり、配備マニフェスト 2 系統
  （compose・helm）へ手が入る。**呼び出し元がまだ決まっていない（未解決事項 2）段階で輸送を増やさない。**
- 起点は IdP の利用者属性なので、**既存の狭い読み口** `platform.authz.v1.UserDirectory/GetUserAttributes`
  でそのまま読める。**契約（proto）は 1 行も変えない。**

### 構成点（`ADR-0082` フォローアップ 3 の「構成または属性」）

| 構成キー | 値域 | 既定 | 意味 |
| --- | --- | --- | --- |
| `RetentionAnchor:Source` | `account-disabled-at` / `hr-leave-date` | `account-disabled-at` | どの属性を起点として読むか |

- **期間（30 日）は構成にしない。** `ADR-0036` D-09 の値であり、`ADR-0082` 決定 5 が
  「期間は変えない。変えるのは起点だけ」と明記している。構成にすると配備が計画の決定を弱められる。
- 値域外の宣言は**起動時に落とす**（`IdentityAdmin:Provider` と同じ deny-by-default）。

### 属性（予約キー）

| キー | 書き手 | 読み手 |
| --- | --- | --- |
| `account_disabled_at` | SC-17 の無効化端点（本作業） | 保持起点の解決 |
| `hr_leave_date` | **人事連携（未実装）** | 同上（`Source=hr-leave-date` のとき） |

🔴 **予約キーは ABAC 属性ではない。** 3 点で分ける。

1. **SC-17 の応答 DTO へ出さない**（`PlatformUserMapper.ToDto` で落とす）。出すと画面の権限編集が
   下書きへ写して送り返し、`UserAssignmentValidation.ValidateAttributes`（辞書に無いキーを拒否する）で
   **400 になる** —— 無効化済み利用者の属性編集が壊れる。
2. **`ReplaceAttributesAsync`（SC-17 の属性差し替え）では消えない。** 差し替えは ABAC 属性だけを
   置き換え、予約キーは現在値を持ち越す。持ち越さないと、部門を 1 つ直しただけで起点が黙って消える。
3. **書き手は `SetRetentionAnchorAsync` ただ 1 つ**にする（ポートに口を足す）。差し替えの口と
   兼用にすると、上の 2 と正面から衝突する（差し替えは保存し、消去は消す、が同じ口で表せない）。

### 判定

```text
Resolve(attributes, options) → Supplied(at) / NotSupplied / Unreadable
Evaluate(anchor, now)        → Elapsed / WithinWindow / NotEvaluable
```

- `Elapsed` は `now >= at + 30 日` のときだけ。
- **`NotSupplied` と `Unreadable` はどちらも `NotEvaluable`** であり、**削除の対象にしない。**
  「対象 0 件」（評価できて該当が無い）と区別できる 3 値にしてある —— bool で返すと
  「起点が無い」が「まだ経っていない」に化け、後から本当に経過したのかを誰も答えられない。
- 解析は **ISO-8601 の固定書式のみ**（`o` 相当・`yyyy-MM-ddTHH:mm:ssK`・`yyyy-MM-dd`）。
  `"0"` や空文字は `Unreadable` / `NotSupplied` であって**エポックではない。**

### 無効化・再有効化

- 無効化: `SetEnabledAsync(false)` → 全セッション失効（既存）→ **起点が未供給なら now を刻む。**
  - **既にある起点は上書きしない**（冪等）。上書きすると再無効化のたびに窓が後ろへ延びる。
  - `Source=hr-leave-date` のときは**刻まない**（起点の出所は人事であり、無効化操作ではない）。
- 再有効化: 起点を**消す**。退職の取り消し・誤操作の是正であり、起点が残ると復職者の資料が
  期限つきで扱われ続ける。

## 走査した母集合（規則 2・9・10）

対象は追跡下の全ファイル。除外は `CHANGELOG.md`（生成物）・`.git`・`node_modules`。
**軸を 1 本で終わらせない**（規則 5）ため 4 軸で引いた。

| 軸 | 検索語 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1（誤りの側＝起点の不在） | `DisabledAt\|RetentionAnchor\|無効化日\|退職日` | 9 ファイル | `src/.../DataSources/Disable/` 2 件は別概念（データソースの無効化）→ **除外**。`docs/screens/SC-20`・SC-19 / SC-20 の画面文言・po / ts カタログ 5 件は**告知文言**であり、`ADR-0082` 決定 4 が「文言は変えない」と明記→ **除外** |
| 2（利用者属性の読み書き点） | `IdentityUser` / `PlatformUserDto` | 8 + 20 ファイル | **変更**: `PlatformUserMapper`（DTO から予約キーを落とす）・両アダプタ・ポート・テスト器 `TestIdentityDirectory`。**確認のみ**: `UserDirectoryGrpcService`（予約キーはここから読ませる＝意図的に残す）・`ScopeUserAttributeSource`（ABAC 判定は名指しのキーしか読まないので不活性）・McpServer の登録者属性（同）。**除外**: 生成物（`src/platform/frontend/src/lib/api/generated/**`）・e2e |
| 3（D-09 の 30 日を語る箇所） | `退職日から 30` | 7 ファイル | いずれも**利用者への告知文言とそのカタログ**。`ADR-0082` 決定 4「利用者向けの文言は変えない」→ **全件除外** |
| 4（保持・削除の評価点） | `PurgeAt\|RetentionDays` | 15 ファイル（非テスト・非生成） | **全件除外**。`PrivateNote`（90 日・ADR-0037 決定 5）・利用イベント保持・通知保持は**別規則**であり、D-09 の 30 日とは起点も対象も違う。混ぜると 2 つの規則が 1 つの定数を共有し、片方を直すともう片方が黙って変わる |

**規則 10 の引き直し**: 予約キーを足したことで新たに誤りになる自分の記述は無いか、
`ValidateAttributes`（辞書外キーを拒否）・`useUserPermissionEditor`（`{...editing.attributes}` を下書きへ写す）・
`EnsureAttributesWereApplied`（要求したキーだけ突合）の 3 か所を実読して確かめた。
**1 つ目と 2 つ目が「DTO へ出さない」という設計判断の理由そのもの**であり、本文へ書いた。

## 受け入れ基準

- [x] 起点あり（無効化から 30 日経過）→ `Elapsed`（陽性対照）
- [x] 起点あり（29 日）→ `WithinWindow`（境界の陽性対照）
- [x] 起点なし → `NotEvaluable`＝削除しない（陰性対照）
- [x] `"0"` / 空文字 → `NotEvaluable`（「未供給」と「0」を分ける）
- [x] `Source=hr-leave-date` に切り替えると読む属性が変わる（構成点が効く）
- [x] `Source=hr-leave-date` では無効化しても起点を刻まない（人事連携を実装しない）
- [x] 無効化で起点が刻まれ、再無効化で**動かない**、再有効化で消える
- [x] SC-17 の応答 DTO に予約キーが出ない（＝属性編集が 400 にならない）
- [x] SC-17 の属性差し替えで起点が消えない
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（platform ユニット）が緑
- [x] 変異試験: 判定を反転させると陰性対照が落ちる／刻印を外すと陽性対照が落ちる

## テスト方針

- 純関数の判定（`RetentionAnchorPolicy`）は単体（`Tests/Domain/RetentionAnchorTests.cs`）。
  **陰性対照だけでは「常に対象外」を返す実装と区別できない**ので、陽性対照を同じ表に並べる。
- 端点（無効化・再有効化・属性差し替え）は既存の結合テスト（`UserAdminEndpointTests`）へ足す。
  観測点は DI の `IIdentityAdminClient`（偽物の生の属性）と応答 DTO の両方 ——
  **DTO だけを見ると「出さない」が「持っていない」と区別できない。**
- Keycloak アダプタは既存のスタブ `HttpMessageHandler` に対する固定（`KeycloakIdentityAdminClientTests`）。
  **緑であることは実 IdP へ反映できることを意味しない**（同ファイルの既存注記）。疎通は稼働クラスタで測る
  —— 本作業では**測っていない**。

## 計画書との差異

- 差異: なし。`ADR-0036` D-09 の期間（30 日）・`ADR-0082` 決定 5 の起点・決定 4 の告知文言の据え置きに従う。
- **環流はしていない**（計画の誤り・不足は見つからなかった）。ただし §未決事項 1 は
  人事システムの実体が判明した時点で `ADR-0082` フォローアップ 1 として環流する対象である。

## 未決事項

1. `hr_leave_date` の書き手（人事連携）は未実装であり、**`Source=hr-leave-date` は現状 fail-safe に倒れるだけ**である。
   方式選定は `ADR-0082` 決定 1 の入力（人事システムの実体）が計画へ入るまで着手できない。
2. 窓が閉じた後に何をするか（`ADR-0036` §未解決事項 2）が未決のため、`Evaluate` の**呼び出し元が無い**。
   裁定後に個人資料側（DocumentService）へ配線する。そのときに利用者名簿への読み口が要る。
3. 実 Keycloak への疎通（予約キーが `unmanagedAttributePolicy: ADMIN_EDIT` で書けること）は
   realm 宣言の実読までで、**稼働クラスタでは測っていない。**
