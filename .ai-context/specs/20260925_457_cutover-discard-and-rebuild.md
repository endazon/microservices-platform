---
title: "再実装版への切替（#457）— オーナー裁定「6 資産は破棄・realm は realm.json から作り直す」の下での切替仕様書・検証スクリプト・裁定の記録"
type: spec
status: in-progress
related_ids: [NFR-05, NFR-18, ADR-0002, ADR-0005, ADR-0008, ADR-0032, IADR-0079, IADR-0082, IADR-0197, IADR-0210, IADR-0369, IADR-0377, IADR-0457, IADR-0459]
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md 非機能要件（可用性 99.9%・運用）
  - planning:projects/microservices-platform/06_technical/06_migration-roadmap.md
---

# 仕様書: 切替 —— 破棄と再構築（#457）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: 可用性 99.9% 目標（`NFR-05`。**計画停止を除く**）／シークレット管理（`NFR-18`。Vault は破棄の対象外であることの根拠）
- 計画: `06_migration-roadmap`（4 フェーズ）／`ADR-0002`（DB per service。共有インスタンス上の DB 単位で消す根拠）／`ADR-0008`（k3s）／`ADR-0032`（BFF セッション）
- 関連 IADR: `IADR-0082` 決定 4（realm を作り直す破壊的経路）／`IADR-0369`（永続化既定オン・realm の静的 import ＋差分 Job・門 G9/G10/G11）／`IADR-0197`（realm 名を `platform` へ）／`IADR-0210`（Qdrant・可観測性の永続化）／`IADR-0457`（Vault の永続化。破棄しない側）／**`IADR-0459`（本作業で起こす。事前割り当て）**
- 起票: #457（親 #454）。先行の下書き: `.ai-context/specs/20260909_issue-457_cutover-decision-table-draft.md`（PR #1355）

### 裁定（本作業の前提。変えない）

- **2026-08-16（#457 コメント・利用者裁定）**: 6 資産（platform アプリ DB / Keycloak realm / Qdrant / MinIO / Wiki.js / 可観測性データ）はすべて破棄。Keycloak realm は `deploy/keycloak/*-realm.json` から再構築（旧 realm 名の食い違いの是正を兼ねる）。`authz_svc` は `deploy/local/abac-seed/` ＋ `scripts/seed-abac-policies.js` から再投入。**ai-stock-trading 側 DB は本 issue の射程外。**
- **2026-09-25（#457 コメント・オーナー）**: 上の裁定は有効。9/09 の下書きと 9/11 の監査が承認待ちとして扱っていた点は解消。切替の文書と検証スクリプトはこの前提で進める。

## #457 の残作業（2026-08-16 コメントが列挙したもの）と本 PR の射程

| # | 残作業 | 本 PR | 理由 |
| --- | --- | --- | --- |
| 1 | 判断表を `docs/migration/` の移行仕様書へ写す | ✅ | 裁定済み。`docs/migration/cutover-discard-and-rebuild.md` を新設 |
| 2 | 切替方式（メンテナンスウィンドウ／段階切替）とロールバック手順 | ✅（文書化と方式の選定まで） | 破棄裁定で段階切替の対象（移すデータ）が消えた。方式の論拠は `IADR-0459` |
| 3 | 件数突合スクリプトを再実行可能な形で残す（`measure-abac-combinations.js` の `--json` / `--dump` / `--input` の型を踏襲） | ✅ | `scripts/measure-cutover-inventory.js` を新設。集計・判定は純関数で `scripts.repo.test.js` が固定する |
| 4 | 旧 ArgoCD Application・イメージ・不要ブランチの整理 | ❌（オーナー作業として列挙） | 稼働クラスタ・レジストリ・リモートブランチへの破壊的操作。AI は実行しない |
| — | 切替の実行・リハーサル | ❌（オーナー作業） | 稼働クラスタはオーナーの売買 PoC が動いている。本作業は稼働クラスタに一切触れない |
| — | `CLAUDE.md` / `AGENTS.md` / `docs/` / README の「再実装後の実態」への更新・#454 の最終トリアージ | ❌ | 切替の実行後に行う（実態が変わる前に書くと、書いた内容が先に腐る） |
| — | go-live 前提（#439 / #458） | ❌ | 2026-09-25 時点で両方 OPEN（`gh issue list` で確認） |

**⇒ `Refs #457`（close しない）。** AI が今できる残りは無いが、実行とリハーサルがオーナー作業として残る。

## 母集合（自分で引いた）

基点 `origin/develop` `d3e8b2a4`・shallow でない。

### 破棄の対象と「触らないもの」の境界（稼働構成を走査して確定）

| 実体 | 置き場 | 共有者 | 本作業の扱い |
| --- | --- | --- | --- |
| MSP のサービス DB 13 本（`ALTER DATABASE … OWNER TO kp` の 13 行） | `platform-infra` の Postgres（PVC `postgres-data`） | **同じインスタンスに AST の DB 7 本**（`CREATE DATABASE … OWNER ai`） | **DB 単位で DROP / CREATE。PVC は消さない**（消すと AST の DB が消える） |
| Keycloak realm `platform`（と旧名 `microservices-platform` が残っていればそれ） | `platform-infra` の Keycloak（H2・PVC `keycloak-data`） | **同じ H2 に master realm と AST realm**（ConfigMap `keycloak-realms` が同梱） | **realm 単位で削除し、Keycloak の再起動で `--import-realm` に作らせる**（IGNORE_EXISTING なので無い realm だけが入る）。**PVC は消さない** |
| Qdrant | `platform-infra`（PVC `qdrant-storage`） | 無し（AST は検索を MSP 経由で呼ぶ。AST の配備に qdrant の参照 0 件） | PVC ごと作り直す |
| MinIO | `microservices-platform`（helm の PVC `minio-data`） | 無し（AST の配備に minio の参照 0 件） | PVC ごと作り直す（バケットは起動時に `EnsureBucketOnStartup` が作る） |
| Wiki.js | `wikijs` DB（上の 13 本に含まれる）＋ helm の PVC `wiki-js-data` | 無し | DB は上と同じ扱い・PVC は作り直す。初期化は `deploy/local/wikijs-setup/bootstrap.sh`（冪等）が行う |
| 可観測性（Prometheus / Loki / Tempo） | `platform-infra`（PVC `prometheus-data` / `loki-data` / `tempo-data`） | AST のメトリクスも同じ Prometheus に入る | PVC ごと作り直す（裁定は破棄。AST の履歴も消えることをオーナー作業に明記） |
| **Vault（SC-22 の秘密・AST のブローカー資格情報）** | `platform-infra`（PVC `vault-data`。`IADR-0457`） | AST | 🔴 **6 資産に含まれない。触らない** |
| **AST namespace（`ai-stock-trading`）とその DB 7 本・AST realm** | — | — | 🔴 **射程外（2026-08-16 裁定）。触らない** |
| RabbitMQ のキュー | `platform-infra` | AST と共有 | 6 資産ではないが、**MSP のキューに滞留した旧イベントは作り直した DB へ孤児データを作る**。MSP のキュー（`<サービス>.<キュー>`・サービスは `pipeline.json` の steps）だけを空にする |
| Redis（BFF セッション） | `platform-infra` | — | 6 資産ではない。realm の作り直しで旧セッションは無効になる（再ログイン） |

### DB の名前は書き写さない

13 本と 7 本の一覧は `deploy/local/infra/postgres.yaml` の初期化 SQL が単一情報源であり、文書へ複写すると片方が腐る。スクリプトが**同ファイルを読んで**分類し、DROP / CREATE の SQL も同ファイルから導出して**出力するだけ**（実行しない）にする。

### 追随する文書の母集合（規則 9）

検索語: `docs/migration` / `cutover` / `切替` / `判断表` / `measure-abac-combinations`（型の参照元）/ `#457`。

| ヒット | 扱い |
| --- | --- |
| `docs/migration/rename-knowledge-platform.md` | 無関係（#228 のリネーム） |
| `.ai-context/specs/20260909_issue-457_cutover-decision-table-draft.md` | **追随する**（日付つき経過追記で裁定と正本の所在を示す。本文と承認欄は書き換えない） |
| `scripts/README.md` の測定器の表 | **追随する**（新スクリプトの行） |
| `scripts/scripts.repo.test.js` の `NOT_CHECKERS` | **追随する**（測定器は検査器の母集合に入れない。`measure-abac-combinations.js` と同じ扱い） |
| `docs/operations/*` | 触らない（運用 Runbook の索引は並行 PR #1495 が触る。切替は運用ではなく一度きりの移行なので `docs/migration/` に置く） |

## 実装方針

1. **`IADR-0459`**: 裁定の記録・破棄の境界（DB 単位／realm 単位で消し、共有 PVC を消さない）・方式（メンテナンスウィンドウ）・検証の考え方（「空か」ではなく「作り直されたか」を時刻で見る。触らない側は作り直されていないことを見る）・ロールバックの扱い（前進復旧。任意の保全）。
2. **`scripts/measure-cutover-inventory.js`**（読み取り専用。Node 標準のみ）:
   - 収集: PVC 一覧（`kubectl get pvc -o json`）・Postgres の DB 作成時刻とテーブル件数（`psql`。スーパーユーザ `postgres`）・Keycloak の realm 一覧と `platform` の利用者（`createdTimestamp`）・クライアント（`kcadm get`）・Qdrant のコレクションと点数（API サーバのサービスプロキシ経由の GET）・MinIO のオブジェクト数（Pod 内の `xl.meta` を数える。資格情報を使わない）・RabbitMQ のキュー深さ（`rabbitmqctl list_queues`）・Prometheus の TSDB `minTime`（サービスプロキシ）。
   - 出力: 既定は要約、`--json`、`--dump <path>`（生データ保存）、`--input <path>`（再集計）、`--baseline <before.json>`（切替前後の件数突合）、`--since <ISO8601>`（判定）、`--print-recreate-sql`（MSP の 13 DB の DROP / CREATE を**出力するだけ**）。
   - 判定（`--since`）: 破棄の側（MSP DB・realm の人間の利用者・作り直す PVC・Prometheus の最古サンプル）が `since` 以降に作られたこと／**触らない側**（AST の DB・`postgres-data`・`keycloak-data`・AST realm）が `since` より前のままであること（陰性対照）／MSP のキューが空であること／`authz_svc` のポリシーが seed と一致すること。1 件でも fail なら終了コード 1。
3. **`docs/migration/cutover-discard-and-rebuild.md`**: 判断表（裁定済み）・方式・手順（事前実測 → 静止 → 破棄 → 再構築 → 検証 → 再開）・ロールバック・リハーサル・オーナー作業・未決事項。表示テキストに ID を書かない。
4. **`scripts.repo.test.js`**: 純関数の単体試験（分類・realm 宣言の読み取り・判定の正負・突合）と、ドキュメントとスクリプトの対応（文書が挙げる引数をスクリプトが受け付ける）。

## 受け入れ基準（Given-When-Then）

- [ ] Given `deploy/local/infra/postgres.yaml` / When 分類関数に渡す / Then MSP 13 本・AST 7 本に分かれ、MSP 側に AST の DB が 1 本も入らない（陰性対照つき）
- [ ] Given 切替後の実測（作り直した側は `since` 以降・触らない側は以前） / When `--since` で判定 / Then すべて ok・終了コード 0
- [ ] Given AST の DB が作り直されていた（`postgres-data` を消した）入力 / When 判定 / Then fail（触らない側の陰性対照が効く）
- [ ] Given realm の人間の利用者に `since` より前の作成時刻が残る入力 / When 判定 / Then fail
- [ ] Given MSP のキューに滞留がある入力 / When 判定 / Then fail。AST のキューの滞留は fail にしない
- [ ] Given `--print-recreate-sql` / When 出力 / Then MSP の 13 DB だけの `DROP DATABASE … WITH (FORCE)` と `CREATE DATABASE … OWNER kp`（AST の DB 名を含まない）
- [ ] Given `docs/migration/cutover-discard-and-rebuild.md` / When `check-trace-blocks` / Then 緑
- [ ] Given `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / When 実行 / Then 緑（採番の欠番を除く。後述）

## 確かめないこと（射程外・オーナー作業）

- 稼働クラスタでの収集（`kubectl` を叩く部分）は**本作業で一度も実行しない**。収集部は `measure-abac-combinations.js` と同じ作法で書き、判定部だけを試験で固定する。**収集部のコマンド（Pod ラベル・kcadm の出力形・Qdrant / Prometheus の JSON 形）は稼働環境で未検証**である —— 初回実行（事前実測）がその検証を兼ねる。
- リハーサル（使い捨てクラスタでの通し）・切替の実行・旧 ArgoCD / イメージ / ブランチの整理・TOTP の再登録。

［2026-09-25 追記 / #457］**実装 ADR の番号を `IADR-0463` から `IADR-0459` へ改番し、PR を #1504 から出し直した。**
事前割り当ての 0458〜0461 が使われず欠番になり、`check-adr-numbering` が止めたためである（develop の最大は `IADR-0457`）。
`IADR-0458` は #1397 の PR（#1504 より先にマージする）が持つ。本 PR のブランチはその PR のブランチの上に積み、
先行 PR のマージまでの間も欠番を作らない。#1504 のブランチには件名のスコープに `IADR-0463` を持つコミットが残り、
改番後は必須 check `check-commit-messages` の実在性検査が必ず落ちる（履歴を書き換えずに範囲から外す手は無い）ため、
新しいブランチへ 1 コミットで載せ直した。本書・移行仕様書・索引・検証スクリプト・試験・`scripts/README.md`・
9/09 の下書きへの追記はすべて `IADR-0459` に追随させた。

［2026-09-25 追記 / #457・監査の指摘］**監査（GO-with-nits）の指摘 4 点を反映した。**

| 指摘 | 誤っていた記述 | 反映 |
| --- | --- | --- |
| 1. 「触らない側」の検査が文書の主張より弱い | `--baseline` を渡しても、切替前に在った AST の DB が切替後に無いと `skip（AST 未配備）`・終了コード 0。AST realm・master realm の消失は見ていなかった。リスク表の「検証スクリプトの『触らない側』が fail で出す」は作り直し（作成時刻）にしか当たらなかった | `evaluate` が `--baseline` を受け、切替前に在った AST の DB と作り直しの対象でない realm の欠落を fail にする。master は常に見る。`--baseline` が無いときは「基準」の行を skip で出す。試験 4 件（消失の fail・未配備の skip・realm の消失・Prometheus を判定に使わない）。リスク表を「作り直しすぎ」と「消しすぎ」に分け、後者は `--baseline` のときだけ出すと書いた |
| 2. 8/16 裁定の AST 側 DB の行が「破棄」と「射程外」を同時に書いている | `IADR-0459` は「射程外」だけを引いていた | 残す側を**意図した読み**として `IADR-0459` 決定 1 に記録（不可逆性の非対称・PoC が使用中・所有者は別リポジトリ） |
| 3. AST の稼働中の身元は作り直す realm `platform` にある | 「AST realm を触らない」で AST を守れているように読めた | 移行仕様書の破棄の境界・手順 0 の 6・手順 6 の 4・リスク表・オーナー作業、`IADR-0459` 結果・残るもの 1 に明記 |
| 4a. Prometheus の `headStats.minTime` は古いブロックがあると最古ではない | 判定に使っていた | 参考表示へ下げ、判定は `prometheus-data` の PVC の作成時刻 |
| 4b. `ARGOCD=1`（selfHeal / prune） | 触れていなかった | 手順 0 の 7・手順 2 で自動同期を止め、起動器の再実行が戻す |
| 4c. 手順 0 の 2「起動器の再実行は既存の値を上書きしない」 | SC-22 の項目にしか当たらない（他の `secret/msp/*` は `vault kv put` で毎回全置換） | 手順 0 の 2 を是正し、リスク表へ行を足した |

規則 10 で引き直した自分の記述: 「消しすぎを捕まえる」（移行仕様書の検証表・リスク表・`scripts/README.md`・`IADR-0459` 決定 4）、
「最古サンプル」（同 4 箇所と本書 §実装方針 2）、「3(c) で空にし」（キューの手順は 3(b) へ移っていた。リスク表を是正）、「6 の 4」（手順 6 の番号が変わった）。
本書 §実装方針 2 の判定の記述（「Prometheus の最古サンプル」を含む）は着手時の方針の記録として残し、本追記で置き換える。
