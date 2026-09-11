---
title: SC-22 の退避手段（コンソール投入 Runbook）と BFF へ Vault 書き込みを与える設計の起草
type: spec
status: done
related_ids: [SC-22, FR-05, ADR-0040, ADR-0042, ADR-0095, IADR-0433]
author: claude
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: SC-22 の退避手段の文書化と、BFF の Vault 書き込み経路の設計起草（#1411）

> **本 PR は画面（SC-22）を実装しない。** モックアップは人間が作る（利用者裁定 2026-07-30）。
> ADR-0095 のフォローアップのうち **3（退避手段の文書化）と 2 の設計（BFF の書き込み経路）だけ**を先に置く。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（管理・権限）／NFR-18（シークレット管理）・NFR-11（全経路 HTTPS）
- ユースケース（UC）: なし（運用・保守要求）
- 画面（SC）: SC-22（秘密情報・接続設定の管理。起案 2026-09-11・**未実装・モックアップ未受領**）
- 関連 ADR: ADR-0095（決定 3・決定 4）・ADR-0040 決定 3・ADR-0042 決定 4・ADR-0007・ADR-0032
- 関連 IADR: IADR-0094・IADR-0096・IADR-0097・IADR-0098・IADR-0099・IADR-0251・IADR-0273
- 計画書リンク: `projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md`（隣接クローンの `origin/main` を読んだ。2026-09-11 取得）
- 起票: #1411（環流 planning#599 の裁定を受けた実装側 issue）

## 目的・背景

2026-09-10 の稼働作業で、**鍵 1 本を差し替えるために全項目を賭ける**状況が起きた。
正規手順（`deploy/local/vault/eso/bootstrap.sh`）は 28 項目すべてを再投入するため、
稼働中の OIDC / DB の秘密まで既定値で上書きする。結果として**手順書に無い回避策**
（鍵 1 本だけを狙った `kubectl apply`）がその場で選ばれた。

ADR-0095 はこれを「手順の側の欠陥」と裁定し、決定 4 で退避手段の正式文書化を、
決定 3 で BFF に Vault 書き込みを与える（項目の集合の外へは書けない）ことを定めた。
本 PR は**画面が無い今この瞬間から使える退避手段**と、**画面が来たときに即着手できる設計**を置く。

## 対象範囲

- 対象:
  1. 運用 Runbook（決定 4）。項目単位の書き込み・ESO refresh・確認（長さのみ）・使用の記録
  2. 実装 ADR **IADR-0433**（決定 3 の設計）。Vault policy・k8s auth ロール・allowlist の単一情報源・監査イベントの形
  3. 項目 allowlist のデータファイル（値を持たない。名前と分類だけ）
- 対象外:
  - **SC-22 の画面実装**（モックアップ未受領。着手条件 blocked:human）
  - **BFF 端点 `PUT /bff/secrets/{item}` の実装**（契約の素描だけを IADR に置く）
  - **Vault policy / ServiceAccount の実配備**（IADR は形を決めるだけ。配備は画面と同時＝ADR-0095 統制の表）
  - **稼働クラスタ・稼働 Vault への操作**（本 PR は 1 度も行っていない）

## 走査した母集合

**記憶で挙げず、文字列で走査してから列挙した**（`.claude/rules/traceability.repo.md` 規則 9）。

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| ESO が同期する MSP の項目 | `grep -rn "remoteRef" -A 3 deploy/ --include=*.yaml --include=*.yml` の `key:` を一意化 | **28 件**（`msp/...`） |
| bootstrap が seed する項目 | `deploy/local/vault/eso/bootstrap.sh` の `vault kv put` 行 | **28 件**（上と同一集合。差分 0） |
| AST（隣接クローン）の項目 | `deploy/helm/ai-stock-trading/values.yaml` の `remoteKey` と `secretKeyRef` の `key:` | KV **3 件**（`ai-stock-trading/app-secrets` / `moomoo` / `moomoo-rsa`）・`app-secrets` のプロパティ **15 件** |
| 既存 Runbook の置き場と書式 | `ls docs/operations/` ＋ `docs/templates/runbook_template.md` | Runbook は `docs/operations/*-runbook.md`（4 件）。**`docs/runbook/` は存在しない** |
| Runbook の相互リンク点 | `grep -rn "-runbook" docs/ --include=*.md` | 索引ファイルは無い。`docs/operations/operations.md` §障害対応（Runbook）と各仕様書から個別に張られている |
| IADR の最大番号 | `ls .ai-context/adr/ | grep -oE "^IADR-[0-9]{4}"` | `IADR-0430`（`origin/develop` `cee62d58` 時点） |

**除外した理由**:

- `src/ai-stock-trading`（submodule）と他エージェントの worktree（`.claude/worktrees/`）は走査対象から外した。
  前者は本リポジトリが採番しない別リポジトリの内容、後者は同一ファイルの複製である。
- AST の項目は**隣接クローンを読み取り専用で読んだ**。
  本リポジトリの `deploy/` には AST の chart が無い（`deploy/local/vault/eso/policy-eso-read.hcl` が
  `secret/data/ai-stock-trading/*` を許可していることだけが痕跡である）。

### 走査の結果得た項目の全体（28 ＋ 3）と、SC-22 が扱う集合の切り分け

**A = SC-22 の allowlist（いま決める）／B = 画面到来後に判断（realm と対で書く必要がある）／C = 恒久的に対象外（稼働中の資格情報を壊す）**

| 区分 | 項目 | 理由 |
| --- | --- | --- |
| **A** | `msp/llm-provider-credentials`（`anthropic-api-key` / `openai-api-key`） | ADR-0095 実測 7 の**最優先**。外部から供給され、人が日常的に差し替える |
| **A** | `msp/keycloak-smtp`（`from` / `user` / `password` のみ） | 組織のメールテナントから供給される。`host` / `port` / `starttls` は**構成**であり Git 経由（ADR-0095 決定 1 の境界） |
| **A** | `msp/wikijs-sync`（`apiKey`） | 外部（Wiki.js）が発行する接続秘密 |
| **A** | `ai-stock-trading/app-secrets` の 7 プロパティ（`finnhub-api-key` / `marketdata-finnhub-api-key` / `fred-api-key` / `edinet-subscription-key` / `discord-webhook-url` / `discord-bot-token` / `discord-bot-killswitch-phrase`） | 外部 API キー・通知の webhook / token。ADR-0095 決定 1 が名指しする種別そのもの |
| **B** | OIDC クライアントシークレット **9 件**（`bff-oidc` / `identity-admin-oidc` / `minio-oidc` / `grafana-oidc` / `vault-oidc` / `headlamp-oidc` / `wikijs-oidc` / `reset-gate-oidc` / `synthetic-monitor-oidc`）。**AST の `*-auth-client-id` / `*-auth-client-secret` 8 プロパティも同型**だが、これらは `app-secrets` の中に同居するため allowlist では `notWritable` として除く | **realm の宣言と同値でなければならない。** 片側だけ書くと認証が静かに壊れる（`bootstrap.sh` が「ズレると PAR が 401」と繰り返し警告している実事象）。Keycloak Admin REST と**対で**書く設計が要る |
| **B** | east-west gRPC の s2s 資格情報 9 件（`*-service-token`） | 同上。realm の機密クライアントと対である |
| **B** | `ai-stock-trading/moomoo` / `moomoo-rsa` | 値が**ファイル形**（PEM）で、テキスト欄とは別の UI 作法が要る。AST 側の面の議論（ADR-0095「残るもの」）と重なる |
| **C** | `msp/postgres` / `postgres-app` / `rabbitmq` / `rabbitmq-app` / `keycloak-admin` / `minio-credentials` / `wikijs-db` | **稼働中のデータストアが既存パスワードで初期化済み**であり、Vault 側だけ書き換えると ESO が誤った資格情報を配って認証が壊れる（`bootstrap.sh` が「値がズレると認証破壊」と明記）。回転は画面の 1 欄では成立しない |

**28 ＋ 3 のうち A は 4 KV・13 プロパティである**（`anthropic-api-key` / `openai-api-key` / `from` /
`user` / `password` / `apiKey` ＋ AST の 7 件。allowlist ファイルの `properties` を
`node -e` で数えた値であり、記憶で書いていない）。**B は Vault パスで 20 件、C は 7 件である。**

## 設計

### 1. Runbook（ADR-0095 決定 4）

置き場は **`docs/operations/secret-item-console-injection-runbook.md`** とする。
🔴 **#1411 の本文は `docs/runbook/` と書いているが、そのディレクトリはこのリポジトリに存在しない。**
`docs/README.md`（種別表の正本）は `runbook` の出力先を **`docs/operations/`** と定めており、
既存 Runbook 4 件もそこにある。**正本に従い、issue の記述との差異を本書に残す**（後掲「計画書との差異」）。

内容の骨子:

1. **`bootstrap.sh` を既定にしない**ことを冒頭で名指しする。理由（28 項目を既定値で上書きする）と
   2026-09-10 の事故を書く。
2. **項目単位の書き込み**。プロパティ 1 つだけを狙う `kv patch` と、KV 全体を置き換える `kv put` の
   使い分けを書く。`msp/keycloak-smtp` のように構成と秘密が同居する KV では `put` が構成を消す。
3. **ESO の refresh**（`force-sync` の annotate）と、待たずに反映させる手順。
4. **確認は長さだけ**。`kubectl get secret -o jsonpath` で base64 を復号して `wc -c` を取る。
   🔴 **値を端末へ出す手順は書かない**（履歴・スクロールバック・画面共有に残る）。
5. **使ったことを記録する**（決定 4 の 3 点目）。監査ログに乗らないため、**#1411 へのコメント**を正式の記録先とする。

### 2. IADR-0433（ADR-0095 決定 3 の設計）

| 論点 | 決定（いま） |
| --- | --- |
| Vault policy | 項目ごとに `path "secret/data/<item>"` へ `create` / `update` / `patch` だけを与える。**`read` は与えない**（値を読み返せない）。`secret/metadata/<item>` に `read` だけ与える（**版と更新時刻。値は含まない**）。`list` はどの階層にも与えない |
| 項目の列挙 | **Vault に訊かない。** allowlist ファイルが唯一の出所である（`list` を与えないので訊けない、が正しい順序） |
| ワイルドカード | **使わない。** `secret/data/msp/*` と書くと ADR-0095 決定 3 の「項目の集合の外へ書けない」が成立しない |
| k8s auth ロール | BFF **専用の ServiceAccount** に束縛する。🔴 **現状 BFF は `default` SA で動いており、`default` に束縛すると名前空間の全 Pod が同じ権限を得る**（`vault-auth-rbac.yaml` が同じ理由で `default` を避けている先例がある） |
| allowlist の単一情報源 | **`deploy/bootstrap/sc22-secret-items.json`**。policy の生成・BFF の起動時読み込み・Runbook の対象表が同じ 1 ファイルを引く |
| ESO | **読み取り専用のまま**（`policy-eso-read.hcl` は変更しない） |
| 監査イベント | 既存の `IAuditLogger`（`Audit=true` 構造化ログ）へ相乗り。`action=secret.item.update` ／ `subject` ／ `outcome` ／ `detail`（項目名・プロパティ名・書き込み後の版番号）。🔴 **値・値の長さ・ハッシュのいずれも入れない** |
| 端点契約（素描） | `PUT /bff/secrets/{item}` ・ `AdminOnly` ・ allowlist 外は **400**（404 ではない。存在秘匿の対象ではなく「入力が不正」である）・**値を返す口は作らない** |

**モックアップ受領まで先送りする事項**も IADR に明記する（区分 B の扱い・一覧画面の表示項目・
プロパティ単位か KV 単位かの UI 粒度・確認ダイアログの有無）。

### 3. allowlist ファイル

`deploy/bootstrap/sc22-secret-items.json`。**値は 1 つも持たない**（名前・Vault パス・プロパティ名・
同期先 Secret 名・分類と理由だけ）。`deploy/bootstrap/` を選んだのは、ここが既に
「環境非依存の secret ブートストラップ宣言」の置き場（`secret-templates.example.yaml`）だからである。

## 受け入れ基準

- [x] Runbook が**項目単位**の手順を持ち、`bootstrap.sh` を既定にしないことを明示的に書いている
- [x] Runbook が 2026-09-10 の事故を引用して理由を説明している
- [x] Runbook の確認手順が**値を端末へ出さない**（長さだけ）
- [x] Runbook が「退避手段を使ったことを記録する」手順と記録先を持つ
- [x] Runbook が `docs/operations/operations.md` から辿れる
- [x] IADR-0433 が policy・ロール・allowlist・ESO・監査・端点契約の 6 点を書いている
- [x] IADR-0433 が「いま決めること」と「モックアップ受領後に決めること」を区別して書いている
- [x] IADR-0433 が `.ai-context/adr/README.md` に登録されている（本文タイトルと 12 文字以上共有・200 字以内）
- [x] allowlist ファイルに**秘密の値が 1 つも無い**
- [x] `docs/` 配下の新規・更新文書が計画 ID を表示テキストへ書かず trace ブロックへ入れている
- [x] 検査器（後掲）が通る（IADR の欠番 0431 / 0432 を除く。並行 PR が着地すれば埋まる）

## テスト方針

**本 PR はコードを 1 行も足さないため、実行可能なテストは無い。** 検証は文書検査器で行う。

- `node scripts/check-trace-blocks.js` / `check-doc-type-vocabulary.js` / `check-doc-links.js` /
  `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `gen-knowledge-graph.js --check` /
  `check-reading-budget.js` / `check-adr-numbering.js` / `check-doc-updated.js` / `check-commit-messages.js`
- **Runbook の手順そのものは実走させない**（稼働クラスタ・稼働 Vault に触れない。#1411 の作業条件）。
  🔴 **したがって「この手順で本当に直る」ことは本 PR では実測していない。** 次に退避が必要になった
  運用の場で確かめる（Runbook の「限界」節にそう書く）。

## 計画書との差異

- 差異: **あり**。
  1. **#1411 本文の `docs/runbook/` は本リポジトリに存在しない。** `docs/README.md` の種別表（正本）に従い
     `docs/operations/` へ置いた。**計画（ADR-0095）は置き場を指定していない**ため、計画との差異ではなく
     issue 本文と実リポジトリ構造の差異である。**環流は不要**と判断した。
  2. **ADR-0095 は SC-22 が扱う項目の集合を列挙していない。** 本 PR が走査して A / B / C に切り分けたが、
     🔴 **B（OIDC クライアントシークレット）を画面から扱うかは計画の判断を要し得る** ——
     realm と対で書く必要があり、実装だけでは「画面から入れられる」と言えない。
     **モックアップ受領時に同じ論点が必ず出るため、そこで一括して問う**（いま起票すると裁定が二重になる）。

## 未決事項

- **B 区分（realm と対の秘密）を SC-22 が扱うか。** 扱うなら Keycloak Admin REST への書き込みが対で要る。
- **プロパティ単位か KV 単位か。** `msp/keycloak-smtp` と `ai-stock-trading/app-secrets` は 1 KV に
  複数プロパティを持ち、うち一部だけが A である。UI の粒度はモックアップが決める。
- **退避手段の記録先を issue コメントでよいか。** ADR-0095 フォローアップ 4 は「記録手段の設計」を
  計画（監査の射程）へ残している。**本 Runbook の記録先は、その設計が来るまでの暫定である。**
