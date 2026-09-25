---
title: 作業仕様書 — SC-22 に項目ごとの供給元（画面 / Git）を出し、送る前に消費側の再起動を告げる（#1502・ADR-0104 決定 2・4）
type: spec
status: done
related_ids:
  - SC-22
  - FR-05
  - NFR-18
  - ADR-0104
  - ADR-0095
  - IADR-0460
  - IADR-0456
  - IADR-0433
  - IADR-0453
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0104_sc22-item-kinds-and-dual-path-for-env-ids.md (Accepted 2026-09-17)
  - planning:projects/microservices-platform/05_screens/01_screens.md (§SC-22 主要素 1・2・§入力・§アクション の 2026-09-17 改訂)
related_specs:
  - 20260915_issue-1477_screen-only-poc-setup.md
issue: "#1502"
---

# 作業仕様書 — SC-22 の供給元と消費側の再起動の告知

## 目的と射程

計画 ADR-0104（planning#635 の裁定）の 5 決定を MSP の実装（#1466 / #1469 / #1478 / #1480・IADR-0453〜0457）と突き合わせ、
**未実装の決定 2（供給元の表示）と決定 4 のうち「消費側が再起動する」旨の告知**を実装する。

### 決定ごとの実装状況（着手時 `origin/develop` a24e6258 で実測）

| 決定 | 実装状況 | 根拠（コマンドと結果） |
| --- | --- | --- |
| 1 両経路・効くのは一方 | 有る（AST 側 `externalSecrets.appSecrets.enabled` と `AST_ESO`。AST の IADR-0341） | `git -C ../ai-stock-trading grep -n "appSecrets" 471cbf3 -- deploy/helm/ai-stock-trading/templates/` → `external-secrets.yaml:82` と `deployment.yaml:35` が同じ述語 `and $es.enabled $es.appSecrets.enabled` |
| **2 供給元を出す** | 🔴 **無い** | `SecretItemStatusDto` に欄なし。画面の列は 5 つ（T-01 が固定）。`git grep -n -i "supplySource\|供給元" -- src/knowledge/frontend/src/features/sc22-secrets src/platform/backend/Bff` → 0 件 |
| 3 3 種別・変換方式の明示 | 有る（IADR-0456 決定 1〜3。「パスワードはそのまま保存されません。MD5 に変換した値だけが…」） | `SecretItemManagementPage.tsx` の `secret-md5-note` |
| 3 公開鍵の表示・複写 | 🔴 **無い** —— **本 issue では扱わない**（下記 §扱わないもの） | 生成の注記は「鍵はこの画面にも表示されません」 |
| 4 同期を促し、再起動まで一連の操作 | 同期の依頼（force-sync）と Reloader は有る（IADR-0456 決定 4・5） | `ExternalSecretSync.cs`・`values-local.yaml` の注釈 |
| **4 「消費側が再起動する」旨を出す** | 🔴 **無い**（鍵の生成の確認だけが OpenD の手動再起動を書く） | 書き込み後の文言は「即時同期を依頼しました。アプリケーションへの反映まで少し時間がかかることがあります。」だけ。moomoo のログイン情報（OpenD が消費・Reloader の対象外）の書き込みで再起動の言及 0 件 |

## 実装

判断の記録は **IADR-0460**（決定 1: 判定の方法／決定 2: 告知の置き場）。

### 境界層

1. `Foundation/Secrets/ExternalSecretPresence.cs`（新規）: `IExternalSecretPresenceReader` / `ExternalSecretPresenceReader`。
   同期依頼と同じ構成（`ExternalSecretSync:*`）・通信路（`KubernetesApi` クライアント・SA の CA で TLS 検証）・名乗り（SA トークン）で
   `GET …/externalsecrets/{name}` を投げ、2xx → `Present`／404 → `Absent`／それ以外 → `Unknown`。
2. `SecretItemServiceCollectionExtensions.AddSecretItemInjection`（`Program.cs` が呼ぶ）で登録。
3. `SecretItemBffEndpoints` の一覧: ロール判定と保管先の確認の**後**に全項目を並べて判定し、`supplySource` を埋める。
4. 契約 `SecretItemStatusDto.SupplySource`（既定 `unknown`・後方互換）と `SecretItemSupplySources`（`screen` / `git` / `unknown`）。
   OpenAPI（応答スキーマは非 null ⟹ required）・orval 生成物・契約スナップショットを追随。

### 画面

1. 一覧に「供給元」列（画面＝success／Git＝warning／確認できない＝neutral。`StatusBadge`）と判定の注記。
2. 更新フォーム: `git` → 送る前に警告（送信は拒否しない）・保存後は同期の成否の代わりに「反映されません」。`unknown` → 反映されるかを言えない旨。
3. 送る前の再起動の注記（`secretConsumerRestart`）: 自動（限定つき）／OpenD 手動／表に無い項目は「ことがある」。`git` では出さない。
4. Lingui カタログ ja / en（13 キー）。初期ロードが +4,074 B（`chunk-budget-baseline.json` に記録）。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験（テスト仕様書の ID） |
| --- | --- | --- |
| 1 | 同期先が在る／無い／拒否・障害・不達・未構成で `screen` / `git` / `unknown`。`unknown` を畳まない | T-68・T-69（`BffSecretItemEndpointTests`） |
| 2 | 権限外・保管先不達では Kubernetes API に触れない | T-70 |
| 3 | 本番の合成が判定器を登録している | T-71（加えて T-68〜T-70 は本物の `Program` を起動した `BffTestFactory` を通る。差し替えは HTTP ハンドラだけ） |
| 4 | 一覧に供給元が色 ＋ アイコン ＋ テキストで出る。値の無い行は「確認できない」 | T-72（画面）・T-01 を 6 列へ更新 |
| 5 | `git` の項目は送る前と後に「反映されない」を出し、送信は拒否しない | T-73 |
| 6 | `unknown` の項目は反映されるかを言えない旨を出す | T-74 |
| 7 | 送る前に消費側の再起動の旨を出す（moomoo は手動） | T-75 |
| 8 | 契約・生成物・カタログ・テスト仕様書が追随する | codegen / i18n の再生成差分・`check-contract-schema`・`check-i18n-catalogs` |

**赤の確認（実測）**: ①境界層で `supplySource` を常に `unknown` にすると T-68 と T-69 の 404 ケースが落ちる（2 失敗・5 合格）。
②画面で `secretSupplySource` を「git 以外は screen」にすると T-72 と T-74 が落ちる（2 失敗・16 合格）。どちらも戻して緑。

## 母集合（規則 1〜6・9・10）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1. 誤りの側（5 列と書いた箇所） | `git grep -n -E "5 列\|5 つだけ\|five planned\|最終更新者／操作\|最終更新者 / 操作" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'` | SC-22 画面仕様書（主要素表・計画対応表）・テスト仕様書 T-01・画面の試験。他は無関係（SC-20 の端末一覧・DB の 5 列・確定済み specs） | 主要素表・T-01・試験を 6 列へ。**計画対応表の行「項目の一覧（…／操作）」は計画側の原文の列挙なので残し**、供給元は別の行を足した。`.ai-context/specs/20260914_…` は確定済みで書き換えない |
| 2. 契約の型の利用者 | `git grep -n "SecretItemStatusDto" -- . ':!src/ai-stock-trading'` | C#: 端点・試験・契約。TS: 生成物・`useSecretItems.ts`・画面・e2e のスモーク | e2e の固定データへ `supplySource` を足した（typecheck で検出）。IADR-0453:160 は型名の言及だけ（並行 PR #1494 が触った記録でもあり変えない） |
| 3. 一覧の応答を説明する文書 | `git grep -n -i "bff/secrets" -- docs` | `docs/api/BFF_bff-surface.md`・`openapi.yaml`・SC-22 画面／テスト仕様書・運用 Runbook | BFF_bff-surface・openapi を追随。**運用 Runbook は応答の欄を述べていない**（`git grep -n -E "列\|最終更新者\|供給" -- docs/operations/secret-item-console-injection-runbook.md` → 該当なし）ので変えない（並行 PR #1495 の領域でもある） |
| 4. 同期・再起動を述べる文書 | `git grep -n -E "同期の経路に触れない\|再起動" -- docs/screens/SC-22* docs/tests/SC-22* src/knowledge/frontend/src/features/sc22-secrets` | 画面の生成の確認文言・テスト仕様書の対象外表 | 画面仕様書に「消費側の再起動」節を足した。テスト仕様書の対象外表（稼働クラスタでの作り直し）は正しいので残し、未決事項へ判定の実測を足した |
| 5. 権限の字面 | `SecretItemExternalSecretRbacTests`（T-61） | Role の verbs は既に `get` / `patch` | 権限は変えない（試験も変わらない） |

## 扱わないもの（判断・稼働クラスタが要る）

1. **決定 3 の公開鍵の表示**: 境界層は保管先の値を読めない（IADR-0433 決定 1）ので、公開鍵の置き場（生成時に別の場所へ書く等）の設計が要る。
   さらに 🔴 **ADR-0104 実測 4 の前提「公開鍵を moomoo へ登録する必要がある」を実装は裏付けない** ——
   AST の `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MoomooBrokerOptions.cs:11` は「OpenD 側の `<rsa_private_key>` と同一鍵を指す」、
   `deploy/opend/entrypoint.sh:19` は「クライアントも同一鍵で暗号化接続する」であり、公開鍵を外部へ登録する経路が無い。計画への確認が要る（PR 本文に記録。起票は判断を仰ぐ）。
2. 再起動を**いつ**行ってよいかの制約と、同期を促す UI の形（ADR-0104 フォローアップ 2。計画で未定）。
3. 稼働クラスタでの判定の実測（テスト仕様書の未決事項。T-40 と同じ場）。

## 実装中に見つけて直したこと

- 🔴 **判定の位置**: 初版は判定を「保管先の確認の後・metadata の読み取りの前」に置いていた。プロジェクト全体の実行で T-70 が落ち、
  **トークンを保持した状態で保管先に届かないと、ログイン確認を飛ばして判定まで進み、Kubernetes API へ触れてから 503 を返す**ことが分かった
  （IADR-0453 決定 5 の「保持中のトークンでログイン確認を飛ばした後に Vault が落ちた場合」）。判定を「一覧を返すと決まった後」へ移し、
  T-70 をトークン保持の状態で 503 を起こす形に改めた（決定的に再現する。`VaultKvClient.CanAuthenticateAsync` は保持中のトークンがあれば Vault へ問い合わせない）。

## 付随した修正（試験の順序依存）

`FakeVault.Reset()` が `CurrentToken` を既定値へ戻していたため、トークンを回す試験（`Vault_token_is_reused_and_renewed_once_on_403`）の直後に
走る試験の最初の書き込みが 403 → 再ログイン → 再送になり、data への要求が 2 本になって `Update_patches_a_single_property_with_merge_patch` が落ちた
（本 PR で試験を足して実行順が変わり顕在化。単独では緑）。BFF の Vault クライアントのトークンは singleton で試験をまたいで残るので、Reset はトークンを戻さない。

## 検証

PR 本文に記録する（コマンドと結果）。
