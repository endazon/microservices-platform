---
title: "SC-22 のフェーズ末監査の非ブロッキング指摘 3 件（ソフト削除後の書き込み・PUT 本文の上限・metadata 再作成時の最終更新者）を片付ける（#1467）"
type: spec
status: in-progress
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, IADR-0433, IADR-0453, IADR-0454]
author: claude
created: 2026-09-15
updated: 2026-09-15
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
---

# 仕様書: SC-22 監査フォローアップ 5〜7（#1467）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-05`（管理・権限）
- 非機能要件（NFR）: `NFR-18`（シークレット管理）
- ユースケース（UC）: なし（運用・保守要求）
- 画面（SC）: `SC-22`（秘密情報・接続設定の管理）
- 関連 ADR: `ADR-0095` 決定 3（値を読み出せない・項目の集合の外へ書けない）・決定 4（一括再投入をしない）
- 関連 IADR: `IADR-0433`（権限の形。**data の `read` なし・`update` なし・ワイルドカードなし・`items[]` だけ**）／`IADR-0453`（決定 3・5・7・10、フォローアップ 5〜7 が本作業）／**`IADR-0454`（本作業で起こす。事前割り当て）**
- 起票: #1467（MSP#1466 のフェーズ末監査の指摘 2・4・5）。SC-22 本体は #1411
- 同じ PR で記録する裁定: planning#631（2026-09-15・選択肢 (a)）。**コードは変えない**（注記は既に在る）。別コミットにする

## 目的・背景

MSP#1466（develop `eed1ff24`）で SC-22 の画面 → BFF → Vault が着地した。別文脈のエージェントによるフェーズ末監査は条件付き合格で、
非ブロッキングの指摘 3 件を IADR-0453 のフォローアップ 5〜7 に残した。いずれも `SecretItemBffEndpoints` / `VaultKvClient` /
書き込み記録に閉じる小修正であり、1 本で片付ける。

## 着手前の実測（`origin/develop` `eed1ff24`）

| 箇所 | 現状 |
| --- | --- |
| 現在版がソフト削除された KV への書き込み | `PATCH` 404 → `POST cas=0`。実 Vault は metadata が在る path への `POST` を `update` として権限判定するため 403（IADR-0453 決定 10 で `update` を外した）→ トークンを取り直して再送 → 403 → `Rejected` → **502 `vault-rejected`**。画面は「書き込みを受け付けませんでした」だけで、原因も次の一手も分からない |
| FakeVault の同じ状況 | 🔴 **実 Vault を写していない**。`PATCH` は `Store` にあれば削除済みでも成功して `Deleted=false` に戻し、`POST cas=0` は既存 KV に 400 を返す。**現在の試験では 502 すら再現しない** |
| `PUT /bff/secrets/{item}` の本文 | 最小 API の暗黙バインド（`UpdateSecretItemRequest? body`）。**ハンドラ（＝ロール判定）より前に解釈される**。上限は Kestrel 既定の 30 MB。不正 JSON・Content-Type 違いはフレームワークが 400 / 415 を返し、**監査に残らない** |
| 最終更新者 | 記録の `Version` と metadata の `current_version` だけを突き合わせる。metadata を削除して作り直すと版は 1 から振り直され、**古い記録（版 1・別人）が一致してしまう** |
| FakeVault の作成時刻 | `2026-09-14T01:00:<版>Z`（**版から決まる**）。作り直した版 1 は元の版 1 と同じ時刻になり、時刻の突き合わせを試験できない |
| 書き込み記録の `UpdatedAt` | `VaultKvClient.InterpretWriteAsync` が書き込み応答の `data.created_time`（無ければ BFF の時計）を入れている |

## 対象範囲

- 対象:
  1. **フォローアップ 5**: `VaultKvClient`（削除済みの判定と書き込み結果）・端点（409・problem type・監査理由）・SPA（文言）・FakeVault（実 Vault の 404 / 403 を写す）・運用 Runbook（復元の手順）
  2. **フォローアップ 6**: 端点の本文を手で上限付きに読む（413 / 415 / 400 を監査つきで返す）
  3. **フォローアップ 7**: 一覧の最終更新者の突き合わせに作成時刻を足す・FakeVault の時刻を書き込みごとに進める
  4. 記録: IADR-0454（新規）・IADR-0453 フォローアップ 5〜7 への追記・索引・`docs/screens` / `docs/tests` / `docs/api`（`openapi.yaml` と `BFF_bff-surface.md`）/ `docs/security/security.md` の監査表 / Runbook、codegen と i18n の再生成
  5. **別コミット**: planning#631 の裁定 (a) を IADR-0453 フォローアップ 1 と画面仕様書へ記録する
- 対象外:
  - 🔴 **Vault の権限を広げること**（data の `read`・`update`・`delete`・`list`・`undelete`・ワイルドカード）。**値を読む口も作らない**
  - 一覧の状態の値域（`set` / `notSet` / `unavailable` の 3 値）を変えること。削除済みは従来どおり `notSet` と出す
  - `cas=0` 作成の競合（400）分岐の見直し。`update` が無い policy では競合は 403 として現れる（IADR-0454 に帰結として記録し、コードは触らない）
  - MinIO・結合試験のワークフロー（別の変更が develop の失敗を直している）
  - 稼働クラスタ・実 Vault への操作
  - 下記「走査で見つけたが直さないもの」

## 設計（決定の理由は IADR-0454）

### フォローアップ 5: 現在版がソフト削除された KV への書き込み

- `VaultMetadataState` に **`Deleted`**（metadata は在り、現在版が削除または破棄されている）を足す。従来の `Absent` は「KV が無い（404・`current_version` が 0）」だけを意味する。一覧は `Deleted` を従来どおり `notSet` と出す（契約は変えない）。
- `VaultWriteOutcome` に **`CurrentVersionDeleted`** を足す。`WritePropertyAsync` は `PATCH` が 404 を返したら **metadata を読んで見分ける**:

  | metadata | 次の手 |
  | --- | --- |
  | `Absent` | 従来どおり `POST` ＋ `cas=0` で作る |
  | `Deleted` | 🔴 **`POST` を送らずに** `CurrentVersionDeleted` を返す |
  | `Present`（`PATCH` と metadata の間に誰かが作った・復元した） | `PATCH` を 1 度だけやり直す |
  | `Unavailable` | `Unavailable` |

- 端点は **409**、problem type `urn:microservices-platform:secret-items:current-version-deleted`、title「この項目の現在の版は保管先（Vault）で削除されているため、画面から書き込めません。」。監査は `failed`・`reason=current-version-deleted`。書き込み記録は作らない。
- SPA は 409 を「現在の版が削除されている」「画面からは書けない」「コンソールの手順で版を復元してから更新し直す」「値は保存されていない」と出す。
- 運用者の次の一手（Runbook の失敗の分岐へ足す）: 削除（ソフト削除）なら `vault kv undelete -versions=<現在版>`、破棄なら `vault kv rollback -version=<破棄されていない版>` で新しい版を作り、**画面から改めて更新する**。どちらもコンソールの権限で行い、BFF の権限は変えない。
- FakeVault: `PATCH` は KV が無い**または現在版が削除・破棄されていれば 404**。`POST` は **metadata が在る KV に対しては `cas` の値に関係なく 403**（実 Vault は既存 path への `POST` を `update` として権限判定し、policy に `update` が無い）。`Kv.Destroyed` を足し、metadata の `destroyed` に写す。

### フォローアップ 6: `PUT` 本文の上限と解釈失敗の監査

- 暗黙バインドをやめ、ハンドラで次の順に処理する。**ロール判定（`DenyUnlessWriterAsync`）と allowlist の判定は本文より前**に置く（非権限者に本文を解釈させない）。
  1. Content-Type が JSON でない → **415**、監査 `denied reason=unsupported-media-type`
  2. `Content-Length` が上限を超える → **413**、監査 `denied reason=body-too-large`（読まない）
  3. 本文を**上限 ＋ 1 バイトまでしか読まない**。超えたら 413（`Content-Length` を持たない送り方でも止まる）
  4. アプリの JSON 設定（`Microsoft.AspNetCore.Http.Json.JsonOptions`。暗黙バインドと同じ）で解釈する。`JsonException`・`null` → **400**（ValidationProblem・`body`）、監査 `denied reason=invalid-body`
- 🔴 本文・例外メッセージ・長さは**ログにも監査にも出さない**。
- 上限は **64 KiB（65,536 バイト）**。最悪の場合の計算:

  | 部分 | 最悪 | 根拠 |
  | --- | --- | --- |
  | 値 | 8,192 × 6 = 49,152 | 上限は UTF-16 の文字数。JSON で 1 文字が最も長くなるのは `\uXXXX`（6 バイト）。System.Text.Json は既定で非 ASCII を、ブラウザの `JSON.stringify` は制御文字と孤立サロゲートをこの形にする（生の UTF-8 は 1 単位あたり最大 3 バイトで、これより短い） |
  | 理由 | 500 × 6 = 3,000 | 同上（SPA は trim して送る） |
  | プロパティ名 | 256 | allowlist の名前は数十文字。余裕を見る |
  | JSON の枠 | 約 40（整形の空白を見て 1,024） | `{"property":"","value":"","reason":""}` |
  | 合計 | 約 53,432 | **64 KiB に収まり、約 12 KiB の余裕** |

- 413 を選ぶ（400 にしない）: 「大きすぎる」は入力の中身の誤りではなく転送の大きさの拒否であり、HTTP の意味どおりの状態コードがある。Kestrel の本文上限が返すのも 413 で、運用時の見え方が揃う。SPA は入力規則（値 8192・理由 500）で送る前に止めるため、画面の利用者が 413 を踏む経路は無い。
- Kestrel の `MaxRequestBodySize`・`[RequestSizeLimit]` を使わない: 拒否がハンドラの外（本文を読んだ瞬間の例外）で起き、**監査に残らない**うえ、**TestServer は強制しないので試験で確かめられない**。

### フォローアップ 7: metadata 再作成時の最終更新者

- 一覧は **`record.Version == current_version` かつ `record.UpdatedAt == versions[current].created_time`（同じ瞬間）** のときだけ `lastUpdatedBy` を返す。
- 記録の `UpdatedAt` は書き込み応答の `data.created_time`（Vault が現在版の metadata に保存する時刻と同じ値）である。書き込み応答に時刻が無く BFF の時計で埋めた場合は一致せず「記録なし」になる（**誤った名前を出さない側に倒れる**）。
- **TTL は置かない**（IADR-0454 決定 3）。
- FakeVault の作成時刻を「版から決める」から「書き込みごとに 1 秒進める」へ変え、作り直した版 1 が別の時刻を持つようにする。

## 走査した母集合

**記憶で挙げず、誤りの側の文字列で走査してから挙げた**（`.claude/rules/traceability.repo.md` 規則 9）。すべて `src/ai-stock-trading`（submodule）を除く。

| # | 走査 | コマンド | 結果 |
| --- | --- | --- | --- |
| 1 | 区別されていない失敗の語 | `git grep -n "vault-rejected"` | IADR-0453（143・164・192・229 行）・作業仕様書 #1411（110 行）・`openapi.yaml` 3629 行・`SecretItemBffEndpoints.cs` 159・162 行・`BffSecretItemEndpointTests.cs` 406 行。**直すのはコード・試験・`openapi.yaml`**。IADR-0453 はフォローアップへの追記で引く（本文は凍結）。#1411 の仕様書は `status: done` の凍結記録で対象外 |
| 2 | 削除済みの扱い | `git grep -n "planning#631\|現在版\|現在の版\|ソフト削除"` | SC-22 に関わるのは IADR-0453・仕様書 #1411・`openapi.yaml`（3573・3575・5845〜5858 行）・`SecretItemDto.cs`・`VaultKvClient.cs` 59・62 行・画面仕様書 52・57 行・生成物（`bff.schemas.ts` / `secret-items.ts`）。**残り（FR-06 / FR-20 の楽観ロック・Obsidian 同期）は別機能の「現在版」で無関係** |
| 3 | 状態の列挙の利用箇所 | `git grep -n "VaultMetadataState\|IVaultKvClient\|new VaultKvClient"`（`VaultKvClient.cs` 自身を除く） | `SecretItemBffEndpoints.cs`（46・63・80・106・214・246〜249 行）と DI 登録 1 行だけ。**`Absent` を分割しても他に壊れる利用者はいない** |
| 4 | FakeVault の時刻に依存する試験 | `git grep -n "CreatedAt\|\.Deleted\|01, 0, "`（`src/platform/backend`） | SC-22 では `FakeVault.cs` 自身だけ。**時刻の値を直接比べている試験は無い**（他の `CreatedAt` は文書 DTO で無関係） |
| 5 | 本文の型の利用箇所 | `git grep -n "UpdateSecretItemRequest"` | `openapi.yaml`・`contract-schema-baseline.json`・端点・DTO・生成物。**DTO と契約の形は変えない**（読み方だけ変える）ので baseline は動かない |
| 6 | 端点に触れる文書・構成 | `git grep -ln "bff/secrets\|secret.item.update\|SecretItemBffEndpoints"` | 20 ファイル。うち**追随するのは** `docs/api/BFF_bff-surface.md`（184 行: 502 / 503 だけを列挙）・`docs/api/openapi.yaml`・`docs/security/security.md`（303 行: `failed` の内訳）・`docs/tests/SC-22_*`。helm 3 ファイルは 503 の説明だけで対象外。`BffEndpointComposition*` は経路の登録で、経路は増えない |
| 7 | 最終更新者の記述 | `git grep -n "最終更新者"`（全体） | 出力 73.4 KB で、大半が別機能（コネクタの更新者等）。**そこで 6 の 20 ファイルと SC-22 の文書・画面に絞って** `git grep -n "最終更新者\|記録なし\|lastUpdatedBy\|LastUpdatedBy"` を引き直した → `openapi.yaml` 3575・5858 行・画面仕様書 55〜58 行・`SecretItemDto.cs` 16〜17 行・`SecretWriteRecordStore.cs` 11 行・端点 65 行・試験。**「版が一致するときだけ」と書く箇所に「作成時刻も」を足す** |
| 8 | 上限値 | `git grep -n "8192\|MaxValueLength\|MAX_VALUE_LENGTH"`（全体） | 出力 116.7 KB（ロックファイル等の数値を含む）。SC-22 の該当は IADR-0453・仕様書 #1411・`openapi.yaml` 3621・5867 行・画面仕様書 66・76 行・`secretItemVocabulary.ts`・画面。**8192 自体は変えない**。本文上限は新しい値として `openapi.yaml` の 413 に書く |
| 9 | IADR の最大番号 | `ls .ai-context/adr \| grep -oE "^IADR-[0-9]{4}" \| sort \| tail -2` | `IADR-0452` / `IADR-0453`。**本作業は事前割り当ての `IADR-0454` を使う** |

**除外と理由**:

- `src/ai-stock-trading`（submodule）: 別リポジトリ。本作業は AST の構成に触れない。
- `.ai-context/specs/20260914_issue-1411_*`・`20260911_issue-1411_*`: `status: done` の凍結記録。本文を書き換えない。
- `src/platform/frontend/src/lib/api/generated/**`: 手で直さず `pnpm run codegen` の再生成で追随させる。
- 走査 7・8 の全体出力のうち SC-22 以外の行: 別機能の同じ語（「最終更新者」「8192」）で、本作業の変更で誤りにならない。

**走査で見つけたが直さないもの（本 issue の射程外。報告に残す）**:

- `docs/tests/SC-22_secret-item-management.md` T-33 の期待結果が「data は create / patch / **update**」のまま。IADR-0453 決定 10 で `update` を外しており、試験（`SecretItemVaultPolicyTests`）は `create` / `patch` だけを固定している。**文書だけが古い。**

## 受け入れ基準

- [ ] **AC-01**（FU5）FakeVault が「現在版が削除・破棄された KV への `PATCH` は 404」「metadata が在る KV への `POST` は 403」を返す
- [ ] **AC-02**（FU5）その状態への `PUT` は **409**・problem type `…:current-version-deleted`・監査 `failed reason=current-version-deleted`。**`POST` を送らず**、Vault の中身と削除状態は変わらず、書き込み記録を作らない
- [ ] **AC-03**（FU5）`VaultKvClient` 単体でも削除（`deletion_time`）と破棄（`destroyed`）の両方で `CurrentVersionDeleted` を返し、metadata は `Deleted` を返す。KV が無いときは従来どおり作る（陽性対照）
- [ ] **AC-04**（FU5）一覧は削除済みを従来どおり `notSet` と出す
- [ ] **AC-05**（FU5）SPA は 409 で「現在の版が削除されている・復元してから更新し直す・値は保存されていない」を出す
- [ ] **AC-06**（FU6）本文が 64 KiB を超える `PUT` は **413**・監査 `denied reason=body-too-large`・Vault に触れない。`Content-Length` がある送り方と無い送り方の両方で
- [ ] **AC-07**（FU6）最悪の大きさ（値 8192 文字・理由 500 文字をすべて `\uXXXX` で送る。50 KB 超）は通る（陽性対照）
- [ ] **AC-08**（FU6）不正 JSON・JSON の `null` は **400**・監査 `denied reason=invalid-body`。本文の断片が監査にもログにも出ない
- [ ] **AC-09**（FU6）JSON でない Content-Type は **415**・監査 `denied reason=unsupported-media-type`
- [ ] **AC-10**（FU6）運用者・管理者以外は本文が壊れていても **403 ＋ 監査 `reason=forbidden`**（本文より前にロールを見る）。CSRF ヘッダの扱いは変わらない
- [ ] **AC-11**（FU7）metadata を削除して作り直した KV（版 1・別の作成時刻）では、古い記録の版が一致しても `lastUpdatedBy` は null
- [ ] **AC-12**（FU7）画面から書いた版が現在版のままなら名前が出る（既存の試験が陽性対照）
- [ ] **AC-13** 値・値の長さ・ハッシュが応答・ログ・監査に出ない（既存の試験を保ち、新しい経路も同じ）
- [ ] **AC-14** Vault の policy は変わらない（`SecretItemVaultPolicyTests` が緑のまま・HCL の差分なし）
- [ ] **AC-15** 3 件それぞれに**修正前の実装で落ちる試験**があり、落ちたことを本書に記録した
- [ ] **AC-16** IADR-0454 が決定を記録し、索引に登録され、IADR-0453 フォローアップ 5〜7 から参照される
- [ ] **AC-17** planning#631 の裁定 (a) を IADR-0453 フォローアップ 1 と画面仕様書へ**別コミットで**記録した
- [ ] **AC-18** `pnpm run codegen` / `pnpm run i18n` の再実行で差分なし・未翻訳キーなし

## テスト方針

| 層 | 追加する試験 | 対応 |
| --- | --- | --- |
| xUnit（端点） | 削除済み KV への `PUT` → 409（`POST` なし・記録なし・一覧は `notSet`）／破棄済みも同じ | AC-01・02・04 |
| xUnit（クライアント単体） | `VaultKvClient` を FakeVault の handler で直接組み、削除・破棄・不在の 3 通りの書き込み結果と metadata の状態 | AC-03 |
| xUnit（端点） | 64 KiB 超（`Content-Length` あり／無し）→ 413／最悪の大きさの本文 → 200／不正 JSON・`null` → 400 ＋ 監査／`text/plain` → 415／他ロール ＋ 不正 JSON → 403 | AC-06〜10・13 |
| xUnit（端点） | 画面で書いた後に metadata を消して作り直す → `lastUpdatedBy` null | AC-11 |
| Vitest（画面） | 更新が 409 で失敗 → 削除済みの文言 | AC-05 |

赤の確認（TDD）: 各試験を先に書き、**修正前の実装で落ちることを実行して確かめてから**直す。結果は下の「赤 → 緑の記録」へ書く。

## 赤 → 緑の記録

### フォローアップ 5（削除済みの版への書き込み）

| 段 | 実行 | 結果 |
| --- | --- | --- |
| 赤 1 | FakeVault を実 Vault に合わせ（削除・破棄への `PATCH` 404／既存 KV への `POST` 403）、端点の試験を足した時点で `dotnet test … --filter BffSecretItemEndpointTests`（本番コードは未変更） | **失敗 2 / 合格 28**。`Expected response.StatusCode to be HttpStatusCode.Conflict {value: 409}, but found HttpStatusCode.BadGateway {value: 502}`（削除・破棄の両方） |
| 赤 2 | 同じ時点で `vitest run knowledge/frontend/src/features/sc22-secrets` | **失敗 1 / 合格 8**。受け取った文言は境界層の title そのまま（「……削除されているため、画面から書き込めません。」）で、「削除されています」「復元」「値は保存されていません」が無い |
| 赤 3 | 列挙の値だけを足し（振る舞いは未変更）、`VaultKvClientTests` を足して実行 | **失敗 2 / 合格 2**。`Expected metadata.State to be VaultMetadataState.Deleted {value: 3}, but found VaultMetadataState.Absent {value: 1}`。陽性対照（不在は作る・在れば metadata を読まずに部分更新）は緑 |
| 緑 | クライアント・端点・SPA を直して `dotnet test … --filter "BffSecretItemEndpointTests\|VaultKvClientTests\|SecretItem"` と vitest | **合格 52 / 失敗 0**、**合格 9 / 失敗 0** |
| 再生成 | `pnpm run codegen` → `secret-items.ts` だけ更新（409 の応答型）。`pnpm run i18n` → 初回は en の未翻訳 1 件で compile が失敗し、訳を入れて再実行で **Missing 0** |

（フォローアップ 6・7 は実装時に記入する）

## 検証

（PR 前に記入する）

## 計画書との差異

- 差異: なし（計画の要求は変えない。IADR-0453 の実装判断の穴を埋める）。
- planning#631 は裁定 (a) で閉じた（主要素 3 は項目単位で満たす）。記録は別コミット。

## 未決事項

- なし。
