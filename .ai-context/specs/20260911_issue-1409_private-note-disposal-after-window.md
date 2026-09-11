---
title: 退職後 30 日の閲覧窓が閉じた個人資料を定期処理で完全削除する
type: spec
status: done
related_ids: [FR-19, FR-22, UC-11, SC-19, SC-17, SC-10, ADR-0036, ADR-0037, ADR-0057, ADR-0082, ADR-0096, IADR-0428, IADR-0431]
author: Claude (worker)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
---

# 仕様書: 退職後 30 日の閲覧窓が閉じた個人資料を定期処理で完全削除する（#1409）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（個人資料）・FR-22（通知。**本経路では送らない**）・NFR-27（保存容量）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-19（固定文言「退職日から 30 日間」）・SC-17（無効化＝起点の発生源）・SC-10（削除の事実の観測）
- 関連 ADR: ADR-0096（決定 1〜3。本件の直接の起点）・ADR-0036 D-09／§未確定事項 2・
  ADR-0057 決定 1（削除の射程）／決定 2（残余を置かない）・ADR-0037 決定 5（90 日の器）・ADR-0082 決定 5
- 関連 IADR: IADR-0428（起点と 3 値判定。決定 5 の「配線しない」を本作業で解消する）・
  IADR-0296（完全削除がオブジェクトストレージの本文へ及ぶ実装）・IADR-0401（利用者名簿の狭い読み口）・
  IADR-0385（属性の線上表現）・IADR-0379 決定 4（east-west に利用者トークンを載せない）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md>

## 目的・背景

ADR-0036 D-09 は管理者閲覧の窓を「退職日から 30 日間」と定めたが、窓が閉じた後の扱いが未決だった。
IADR-0428（#1392 / PR #1401）は**起点と 3 値判定だけ**を置き、決定 5 で「呼び出し元は配線しない」と
明記して止まっていた。ADR-0096（2026-09-11 裁定・選択肢 A）がこれを確定したので、後段を配線する。

🔴 **現状は退職者の個人資料が無期限に残る**（ADR-0096 統制表）。起点は溜まるが誰も削除しない。

## 対象範囲

- **対象**: `PrivateNoteMaintenanceService` の周期へ述語を 1 つ足す。
  「所有者が無効化済み ∧ 保持起点の評価が `Elapsed`」の個人資料を `DocumentObjectPurger` で完全削除し、
  `DocumentDeleted` を発行する。監査ログに「いつ・誰の・何件」を残す。
  無効化の事実と判定は認可サービスの既存の狭い読み口（`platform.authz.v1.UserDirectory/GetUserAttributes`）から採る。
- **対象外**:
  - 管理者の明示操作による削除経路（ADR-0096 決定 2「設けない」）。
  - FR-22 ① の通知（ADR-0096 決定 1「本経路では送らない」。宛先が無効化済みである）。
  - 持ち出し・移管（ADR-0096 決定 3。**経路を増やさない**）。
  - 共有元が退職した場合の共有先への通知（ADR-0096 §残るもの。計画側の裁定待ち）。
  - 同期トークンの失効（ADR-0037 フォローアップ 3。ADR-0096 フォローアップ 2 で別途環流する）。
  - 30 日の構成化（ADR-0096 決定 2「構成に出さない」）。

## 走査した母集合

**規則 1〜6・9・10**（`.claude/rules/traceability.md` ／ `traceability.repo.md`）に従い、
**誤りの側の文字列**から**軸を 5 本**引いた。走査は `git grep -ln`（追跡下の全ファイル。拡張子で絞らない）。

| 軸 | 検索語 | 件数 | 判断 |
| --- | --- | --- | --- |
| 1 | `IADR-0428` | 19 | 本体・索引・仕様書 2・`docs/security/security.md`・認可サービスのコード 14 |
| 2 | `RetentionAnchor` | 17 | 軸 1 の部分集合（仕様書 1 件と索引が落ちる） |
| 3 | `退職` | 54 | 画面・文言・BFF セッション等を含む広い網。**本作業で誤りになるのは docs 2 件のみ** |
| 4 | `account_disabled_at\|hr_leave_date` | 6 | 予約キーの直書き。**属性キーは足さない**ので追随不要 |
| 5 | `配線しない\|判定の呼び出し元\|未解決事項 2\|誰も削除しない` | 19 | **凍結記録（`.ai-context/`）が 17 件・`docs/` が 2 件** |

### 本作業で「新たに誤りになる記述」（規則 10 の引き直し）

| ファイル | 誤りになる記述 | 対応 |
| --- | --- | --- |
| `docs/security/security.md` §退職時の個人資料の保持起点 | 「窓が閉じた後に資料をどう扱うかは計画側で**未決**であり、実装は起点と判定だけを持つ（**判定の呼び出し元はまだ無い**）」 | 本文を差し替え、trace ブロックへ ADR-0096 / IADR-0431 を足す |
| `docs/functional/FR-19_private-notes.md` 冒頭の残作業 | 「**入っていないのは** ④**退職時規則**・…」 | ④ を「入った」側へ移し、残るのはトークン失効だけに絞る。trace ブロックを更新 |
| `.ai-context/adr/IADR-0428_....md` 決定 5 | 「窓が閉じた後の動作は**実装しない**」 | **本文を書き換えない。** 日付つき追記ブロック `［2026-09-11 追記 / #1409］` を足して解消を記す（`.ai-context/adr/` は live な権威文書であり、`traceability.repo.md` の Superseded 書式 §適用先に当たる） |

### 黙って除外したものと理由（規則 6）

- 軸 3 の 52 件のうち上表 2 件を除く全件 —— **`退職` を含むが本作業で誤りにならない**。内訳:
  凍結記録（`.ai-context/adr/` 4・`.ai-context/specs/` 9）＝ 本文プロズを書き換えない、
  画面・文言・PO カタログ（frontend 7）＝ SC-19 の固定文言は ADR-0096 で**変わらない**、
  BFF セッション・共有（`DocumentShare` / `document-share.md` / `FR-20` / `SC-17` / `SC-20`）＝ D-11 側の話で射程外、
  認可サービスのコード 14 ＝ 起点の書き手側で本作業は読むだけ。
- 軸 4 の全件 —— **予約キーの綴りも値域も変えない**。読み手が 1 つ増えるだけである。
- 軸 5 の凍結記録 17 件 —— `.ai-context/` は凍結記録であり、**本文プロズを後から書き換えない**
  （`.ai-context/README.md`）。IADR-0428 だけは追記ブロックで解消を記す（上表）。
- `CHANGELOG.md` / `src/ai-stock-trading` —— 生成物・submodule（`check-plan-id-qualification.js` の対象外と同じ線）。

## 設計

### 1. 判定は**認可サービス側**で行い、判定結果だけを east-west に載せる

🔴 **`RetentionAnchorPolicy` は `AuthorizationService.Domain` にあり、knowledge ユニットから参照できない**
（ユニット外参照は `src/platform/backend/Shared/` の 3 プロジェクトのみ。CLAUDE.md §サービス境界）。
30 日・ISO-8601 の固定書式・fail-safe の向きを knowledge 側へ**複写しない**ため、
**解決と判定は認可サービスが行い、3 値の答えだけを線に載せる**。

**新しい面（proto service / rpc / クライアントクラス）は作らない。** 既存の
`platform.authz.v1.UserDirectory/GetUserAttributes` の**応答へ 2 項目を足す**。

```proto
message GetUserAttributesResponse {
  bool found = 1;
  string username = 2;
  map<string, string> attributes = 3;
  bool enabled = 4;                                  // 追加
  RetentionEligibility retention_eligibility = 5;    // 追加（0 = UNSPECIFIED → 呼び出し側は NotEvaluable へ倒す）
}
```

- 🔴 **`UNSPECIFIED`（proto3 の既定 0）を `NotEvaluable` へ写す。** 古い呼び出し先と喋ったときに
  「経過した」へ倒れないための fail-safe である（既定値が削除を発火させてはならない）。
- 既存の `GetUserAttributesAsync` / `PlatformUserAttributes`（McpServer が使う）は**触らない**。
  `UserDirectoryGrpcClient` へ読み口を 1 つ足す（`GetRetentionStatusAsync`）。

### 2. knowledge 側のポートと縮退

`DocumentService.Domain.Ports.IOwnerRetentionDirectory`

```csharp
Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct);  // null = 引けなかった
public sealed record OwnerRetentionStatus(bool Found, bool Enabled, OwnerRetentionEligibility Eligibility);
public enum OwnerRetentionEligibility { Elapsed, WithinWindow, NotEvaluable }
```

- 🔴 **「居ない」「引けなかった」「有効」「まだ経っていない」「数えていない」は、いずれも削除しない。**
  削除するのは `Found && !Enabled && Eligibility == Elapsed` の**唯一の組み合わせ**である。
- `Services:AuthorizationServiceGrpc` が未構成の配備では `UnavailableOwnerRetentionDirectory`
  （常に `null`）を登録する —— **口が無い配備で削除が起きてはならない**。

### 3. 周期への述語（`PurgeDepartedOwnersAsync`）

`RunAsync` の**先頭**に置く。90 日の器（`PurgeExpiredAsync`）より先に走らせるのは、
退職者の論理削除済み資料が 90 日側で拾われると**無効化済みの宛先へ FR-22 ①-c が飛ぶ**ためである。

1. 個人資料を `OwnerId` で畳み、所有者ごとに 1 回だけ名簿を引く（日次・1 所有者 1 往復）。
2. `Found && !Enabled && Elapsed` の所有者の資料を**論理削除の有無にかかわらず全件**選ぶ
   （窓が閉じた時点で ADR-0096 決定 1 の対象である）。
3. `purger.PurgeIsolatedAsync` で**文書ごとに隔離**して実体を消し、**消せたものだけ**を今周期の対象にする。
   消せなかった行は残り、次周期で再入する（ADR-0096 決定 2・IADR-0296 決定 3 の規律をそのまま採る）。
4. `Documents` / `PrivateNotes` の行を消し、`DocumentDeleted` を発行する（索引の伝播。ADR-0057 決定 1）。
5. 監査 `private-note.purge.departed`：**subject = 所有者 ID・detail = `count=N` のみ**。
   🔴 **タイトル・本文・資料 ID を載せない**（ADR-0096 決定 1「ログ経由で残余を置かない」）。
   時刻は監査ログの行が持つ。
6. **通知しない。** `RecordUsageAndWarnAsync` も呼ばない（容量警告の通知が飛ぶため）。

## 受け入れ基準

| # | 基準 | 種別 |
| --- | --- | --- |
| 1 | 所有者が無効化済みかつ `Elapsed` の個人資料が、DB 行・オブジェクト実体とも消え、`DocumentDeleted` が出る | 陽性 |
| 2 | `WithinWindow` の資料は 1 件も消えない | **陰性対照** |
| 3 | `NotEvaluable`（起点が未供給／読めない）の資料は 1 件も消えない | **陰性対照** |
| 4 | 所有者が**有効**（無効化されていない）なら消えない | **陰性対照** |
| 5 | 名簿を引けなかった（`null`）ときは消えない | **陰性対照** |
| 6 | 実体削除に失敗した資料は**行が残り**、次周期で再入して消える | 陽性 |
| 7 | 監査ログに `count=` だけが載り、**タイトルが載らない** | 陽性 |
| 8 | 本経路では通知が 1 件も発火しない | **陰性対照** |
| 9 | proto の既定値（`UNSPECIFIED`）は `NotEvaluable` へ倒れる | **陰性対照** |

### 変異による裏取り（宣言でなく実測した）

`OwnerRetentionStatus.IsPurgeable` を 1 か所書き換え、
`dotnet test ... --filter FullyQualifiedName~PrivateNoteDepartedOwnerPurgeTests` を実走した結果である。

| 変異 | 結果 |
| --- | --- |
| 無変異（基準線） | **10 件すべて緑** |
| `!Enabled` → `Enabled`（述語の反転） | **失敗 6 / 合格 4** |
| `== Elapsed` → `!= WithinWindow`（`NotEvaluable` の門を外す） | **失敗 1 / 合格 9**（`起点が未供給または読めない所有者の資料は消えない`） |
| `Elapsed` → `WithinWindow` | **失敗 6 / 合格 4** |

🔴 **`NotEvaluable` の門だけは陰性対照 1 本でしか捕まらない。** 他の 9 件はすべて通り抜ける ——
**この 1 本を消すと、起点が未供給の利用者（人事連携が未配備の間は全員）の資料が消える変更が緑で通る。**

## 影響範囲

- `src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/authz/v1/user_directory.proto`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Authz/UserDirectoryGrpcClient.cs`
- `src/platform/backend/Services/AuthorizationService/Features/Users/Directory/GrpcService.cs`（＋テスト）
- `src/knowledge/backend/Services/DocumentService/Domain/Ports/IOwnerRetentionDirectory.cs`（新規）
- `src/knowledge/backend/Services/DocumentService/Infrastructure/ExternalServices/`（新規 2 ファイル）
- `src/knowledge/backend/Services/DocumentService/Features/PrivateNotes/Maintenance/PrivateNoteMaintenanceService.cs`
- `src/knowledge/backend/Services/DocumentService/Program.cs`
- `docs/security/security.md` / `docs/functional/FR-19_private-notes.md`
- `.ai-context/adr/IADR-0431_*.md`（新規）・`.ai-context/adr/README.md`・`.ai-context/adr/IADR-0428_*.md`（追記）

## 残課題・環流

- **1 所有者 1 往復**である。所有者が数万人規模になれば日次周期の往復数が問題になる。
  **今は増やさない**（ADR-0096 決定 2「新しい定期処理を起こさない」）。IADR-0431 §結果 に受容として残す。
- ADR-0096 フォローアップ 2（無効化時の同期トークン失効の実測と環流）は**本 PR の射程外**である。

### 追加で追随した母集合（着手後に機械が示したもの）

🔴 **`check-trace-blocks` が「計画 ADR レンジ（ADR-0001..0093）外です: ADR-0096」で止めた。**
規則 10（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）の実例である ——
**着手前の走査は「IADR-0428 / RetentionAnchor / 退職」の軸で引いており、レンジ宣言は掛からなかった。**

- `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」のレンジを
  `ADR-0001..0093` → `ADR-0001..0096` へ前進させた。
- 🔴 **公開ファイルの導出結果をそのまま採らなかった。** 隣接クローンの作業ツリー `main` が
  `origin/main` より古く、`gen-plan-ranges.js --check` は `[1, 93]` と答えた（作業ツリーは読み取り専用）。
  `git ls-tree --name-only origin/main projects/microservices-platform/07_adr/` を直接数え、
  **一意な `ADR-XXXX` が 96 件・最大 `ADR-0096`**（したがって欠番なし）を実測して採った。
- 追随先は `docs/how-to/plan-id-range-history-annex.md`（世代の記録）1 件である。
  `git grep -n "0093"` の 20 件は**すべて `IADR-0093`（MinIO の OIDC 連携）と計画 `ADR-0093`
  （レンジ公開の裁定）の参照**であり、レンジ宣言ではない。
- `scripts/proto-contracts-baseline.json` は `check-proto-contracts.js --update` で更新した
  （非破壊の差分＝フィールド 2 個と enum 1 個の追加）。**手で書いていない。**
