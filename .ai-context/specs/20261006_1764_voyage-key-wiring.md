---
title: 作業仕様書 — 経路B の LLM ゲートウェイへ Voyage の埋め込みの鍵（Embedding__Voyage__ApiKey）を Secret で結線する（#1764）
type: spec
status: done
related_ids: [FR-02, FR-03, SC-22, NFR-18, ADR-0016, ADR-0095, ADR-0127, IADR-0504, IADR-0096, IADR-0256, IADR-0433, IADR-0456, IADR-0494]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
issue: "#1764"
---

# 作業仕様書 — Voyage の埋め込みの鍵の Secret 配線（#1764）

> 本仕様書は実装着手前に作成した（着手 2026-10-06）。判断の記録は **IADR-0504** に置く。
> 計画は project-planning `aa068ac`（隣接クローン）を読んだ。基点は MSP `origin/develop` `34ee800e`。
> 🔴 **稼働中のクラスタには何も実行しない**（描画・単体試験・スタブの試験だけ）。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-02**（取り込み・埋め込み・索引）。検索クエリの埋め込み（**FR-03**）も同じ鍵を使う。
- 計画 ADR: **ADR-0016**（埋め込みの既定は Voyage voyage-3.5・ティアB・契約でゼロ保持）・**ADR-0095**（秘密情報の投入の面は製品の画面）・ADR-0127（高機密文書は埋め込まない）。
- 画面: **SC-22**（秘密情報・接続設定の管理。項目表に `voyage-api-key` を足す）。
- 起点 issue: **#1764**（オーナー裁定 2026-10-06: 再索引の前に鍵を Secret で結線する。ゼロ保持の認定 #1740 は未了のまま使うことを受け入れる）。関連 #1762（再索引）・#1696。
- 採番: `origin/develop` の IADR の最大は 0502。0503 は未マージの PR #1763 が使っている。本件は **IADR-0504**（**#1763 のマージが先**。順が逆だと採番の欠番検査が赤になる）。

## 現状（2026-10-06 `34ee800e` の実測）

| 経路 | 鍵の扱い |
| --- | --- |
| compose（`deploy/docker-compose.yml:727`） | `Embedding__Voyage__ApiKey: ${VOYAGE_API_KEY:-}` |
| helm 本番像（`values.yaml` の `services.llmgateway.extraEnv`） | `Llm__ApiKey` ← `llm-provider-credentials/anthropic-api-key` だけ |
| helm 経路B（`values-local.yaml`） | 同上（extraEnv はリストの置換なので本番像と同じ 1 件を再掲） |
| ESO（`deploy/local/vault/eso/externalsecret-llm.yaml`） | `anthropic-api-key`・`openai-api-key` の 2 キー |
| Vault の種（`bootstrap.sh`） | 同 2 プロパティ。在る KV は env が空でないキーだけ差し替え |
| ESO 無しの手動 Secret（`k8s-local-up.sh`） | 同 2 キー |
| SC-22 の項目表 | `llm-provider-credentials` の properties は同 2 つ |
| LlmGateway | `VoyageEmbeddingProvider` が `Embedding:Voyage:ApiKey`（既定空）を構築時に読む。空なら外部へ送らず `InvalidOperationException` → `EmbedUseCase` が `Embedded=false`・`Retryable=true` |
| ルーティング | `appsettings.json` の `voyage-managed`（index 0）が `Enabled: true`・Priority 10。経路B の values は埋め込みのルーティングを上書きしない（`LOCALEMBED=1` のときだけ index 2 を有効化）→ 鍵が入れば Voyage へ行く。**ルーティングの設定の変更は要らない** |

## 設計（正は IADR-0504）

1. 置き場所: Secret `llm-provider-credentials` のキー `voyage-api-key` ／ Vault `secret/msp/llm-provider-credentials` のプロパティ `voyage-api-key`。
2. env: `Embedding__Voyage__ApiKey` を `valueFrom.secretKeyRef`（`optional: true`）で `values.yaml` と `values-local.yaml` の両方へ。
   チャートの `extraEnv` / `extraEnvAppend` の描画に `secretKeyRef.optional` を足す（指定した項目にだけ描く）。
3. 鍵が無いときの挙動は変えない（起動する・埋め込みは一時障害で DLQ）。試験で固定する。
4. ESO の data に 1 行。Vault の種は「無いときだけ空で足す」・作るときも持たせる。手動 Secret も持つ。
5. SC-22 の項目表に `voyage-api-key` を足す（分類は動かさない）。
6. 描画の検査 `scripts/helm-llm-provider-keys.test.js` を CI の `static-checks-units` に足す。
7. 手順書 `docs/operations/voyage-embedding-key-runbook.md` を新設し、運用仕様書の埋め込みの節から引く。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | `helm template` の差分が LLM ゲートウェイの env（secretKeyRef）に限られる（既定・values-local） | 前後の描画の diff（下の §検証） |
| A2 | env は llmgateway-service にだけ、secretKeyRef で、リテラルを持たない | `helm-llm-provider-keys.test.js`（変異 4 つで赤になることも） |
| A3 | ExternalSecret のキーが 1 つ増え（3 つちょうど）、項目表の properties と一致する | 同上 |
| A4 | 鍵が無い・空・空白のとき外部へ送らず、埋め込みは `Retryable=true`。鍵が在れば Bearer で送る | `VoyageEmbeddingKeyTests`（LlmGateway.Tests） |
| A5 | Vault の種は在る KV に `voyage-api-key` を空で足し（無いときだけ）、在れば書かない。env の値は標準入力で渡し出力に出ない | `scripts.repo.test.js` の #1764 の 3 本（#1728 の器） |
| A6 | PoC の運用者が Vault へ鍵を入れ、同期と env と埋め込みを値を表示せずに確かめられる手順がある | 手順書（パス・プロパティ・同期の促し・長さの確認・最小の送信・費用・#1740） |

## 母集合（規則 9・10。2026-10-06 `34ee800e` 時点）

### 規則 9（誤りの側の文字列で走査してから追随先を挙げる）

走査: `git grep -nE "embedding-voyage|Embedding__Voyage__ApiKey|anthropic-api-key|openai-api-key|Voyage.{0,30}(未配線|配線していない|配線されて)"`（`src/ai-stock-trading`・`.ai-context/specs` を除く）。

| 当たり | 追随 |
| --- | --- |
| `values.yaml`・`values-local.yaml` の llmgateway の extraEnv | 追随（env を足す） |
| `externalsecret-llm.yaml`・`bootstrap.sh`・`k8s-local-up.sh`（手動 Secret・冒頭の env 一覧・作り直しの注記） | 追随 |
| `sc22-secret-items.json`・`policy-bff-secret-write.hcl`（注記のプロパティ列挙） | 追随（policy はパス単位なので注記だけ） |
| `secret-templates.example.yaml`・`deploy/bootstrap/README.md`・`deploy/local/README.md` | 追随（`deploy/local/README.md` の「本番 chart は未参照」は #308 で既に誤り。同じ段落なので直した） |
| `docs/operations/operations.md` §埋め込みプロバイダ（`embedding-voyage` / `api-key`） | 追随（日付つきで改める・いつ読むかの表に 1 行） |
| `scripts/verify-oidc-edge-flow.sh:598・905`（「どこにも配線されておらず」「未配線」） | 追随（「値が空」へ） |
| `docs/operations/secret-item-live-sync-check-runbook.md:70・150`（Secret のキーは 2 つ・数は 2） | **追随しない**: 手順は「数を控えて前後比較する」で、2 は例示の期待値。手順書自体が「違えば数をそのまま控える」と書いており、3 になっても手順は壊れない。T-40 の手順の改訂は別の機会に回す |
| `docs/operations/secret-rotation-runbook.md:114`・`secret-item-console-injection-runbook.md`（`anthropic-api-key` の例） | 追随しない（例示。プロパティの列挙ではない） |
| `src/knowledge/frontend/.../SecretItemManagementPage.test.tsx`・`BffSecretItemEndpointTests.cs`・e2e | 追随しない（試験の入力として自前の項目を与えている。項目表を読まない） |
| `.ai-context/adr/IADR-0256:80`・`IADR-0284:53`（統合スタックに鍵が無い） | 追随しない（凍結記録。統合スタックの値は今も空であり、記述は誤りにならない） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- `scripts.repo.test.js` の #1728 の器: 既定の世界の `llm-provider-credentials` の KV は空（プロパティ無し）→ 本変更の種が `voyage-api-key` を足すので
  「何も書かない再実行は静か」「足すのは kb-reader の 2 キーだけ」が崩れる。**既定の世界の KV に `voyage-api-key` を持たせ**、無い世界は #1764 の試験が作る。
  env の除去の正規表現にも `VOYAGE` を足す（手元の env が試験に漏れない）。
- `SecretItemCatalogTests.Loads_the_repository_allowlist`（書けるプロパティの合計 21）→ 22 になる。数を直し、`llm-provider-credentials` の 3 つを並びで固定した。
  同じ数を書いている `docs/screens/SC-22_secret-item-management.md:35`・`docs/tests/SC-22_secret-item-management.md:73`（T-29）も日付つきで 22 へ。
  IADR-0433:156・IADR-0453:184・IADR-0456:100 の「21」は当時の数の凍結記録なので直さない。
- `SecretItemBootstrapSeedTests`（put は `vkv_exists` の「無い」側の分岐の中・`-cas=0`）→ put の位置は変えない。崩れない。
- `k8s-local-up.test.js`（手動 Secret の有無・ESO=1 の二重所有回避）→ キーが 1 つ増えるだけ。崩れない（実走で確かめる）。
- PR #1763 の運用仕様書の追記「経路B の Helm の値はこの鍵を配線していない」は、本 PR のマージ後に誤りになる。**#1763 は未マージで本 PR から直せない**ため、PR 本文に記録し、後にマージする側で直す。

## 検証（2026-10-06）

- 描画: `helm template msp deploy/helm/microservices-platform`（既定）と `-f deploy/local/values-local.yaml` の前後の diff は、いずれも llmgateway-service の env の 6 行（`Embedding__Voyage__ApiKey` の secretKeyRef・optional）だけ。
- `node scripts/helm-llm-provider-keys.test.js` 9 本・`dotnet test`（LlmGateway.Tests の該当 11 本・Platform.Bff.Tests の SecretItem* 186 本）・
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 923 本（#1763 の IADR-0503 を一時的に置いた状態。置かないと採番の欠番検査で止まる）・`k8s-local-up.test.js` 260 本。

## 残るもの

- #1740（ゼロ保持の認定）は未了。鍵の欠落の自動検知は無い（IADR-0504 §残るもの）。
- 経路B へ当てるのは PoC の運用者（手順書）。本作業はクラスタに触らない。

［2026-10-06 追記 / #1764］監査の指摘への対応（IADR-0504 末尾の追記と同じ内容）:

- `deploy/local/vault/eso/bootstrap.sh`: `llm-provider-credentials` の作成は全プロパティ空の `put -cas=0`、続けて env が空でない鍵だけ `vkv_patch_nonempty`（stdin）。
  `scripts.repo.test.js` の #1728 の器に `sh -c` の引数の控え（`$STUB_LOG.argv`）を足し、#1764 監査の 2 本（env ありの作成で鍵が引数に載らない・env なしの作成は put だけ）を足した。
  変異: 作成の put へ `${…_API_KEY:-}` の展開を戻す → 赤／作成後の `vkv_patch_nonempty` 3 行を落とす → 赤。
- `helm-llm-provider-keys.test.js`: put の字面の検査を「`voyage-api-key=''` を持ち、`API_KEY` の展開を持たない」へ。`extraEnvAppend` の optional の描画を 1 本足し、
  `optional:` を数える範囲を llmgateway-service の Deployment へ。変異: append 側の `if .secretKeyRef.optional` を消す → 赤／常に `optional: true` を描く → 赤。
- `docs/operations/voyage-embedding-key-runbook.md` 5-a: `printf 'header = …' | curl -K -`、`grep … || true`。
- SC-22 の語彙（`secretItemVocabulary.ts` の purpose・interrupts）と画面仕様書の再起動の表、ja / en のカタログ（`pnpm run i18n`）。
- trace ブロック: `docs/operations/operations.md`・`docs/screens/SC-22_secret-item-management.md`・`docs/tests/SC-22_secret-item-management.md`。
- 範囲外として残したもの: `msp/wikijs-sync` の作成経路（`apiKey='${WIKIJS_SYNC_APIKEY:-}'`）も同じ形で引数へ展開しているが、本 PR の差分の外（既存）であり別件で扱う。

［2026-10-06 追記 / #1764］#1763 が先にマージされたので develop を取り込み、規則 10 の最後の項（運用仕様書の「経路B の Helm の値はこの鍵を配線していない」）を直した
（配線は Secret から入った・値は Runbook の手順で Vault へ入れる・env の名前は空でも出るので値は長さで測る）。規則 9 で `docs/`・`scripts/`・IADR-0503・#1762 の作業仕様書を
「配線していない／未配線」で引き直し、`scripts/verify-oidc-edge-flow.sh:584` を「値が空」へ直し、#1762 の作業仕様書（f-6）へ日付つき追記を置いた。
IADR-0503 は鍵が無い構成を条件として書くだけ（「無いと全件が DLQ へ行く」）で、配線の有無を断定していないので追記しない。
`operations.md:817` の「鍵の未配線」は DLQ の原因の列挙（鍵が無い状態一般）であり、誤りにならないので直さない。
