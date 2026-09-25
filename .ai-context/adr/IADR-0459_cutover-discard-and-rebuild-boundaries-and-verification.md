---
title: IADR-0459 再実装版への切替は 6 資産の破棄と realm.json からの作り直しで行う —— 共有 PVC は消さず DB 単位・realm 単位で消し、方式はメンテナンスウィンドウ、検証は「作り直されたか」を時刻で見る
type: impl-adr
status: Accepted
related_ids:
  - NFR-05
  - NFR-18
  - ADR-0002
  - ADR-0008
  - ADR-0032
  - IADR-0079
  - IADR-0082
  - IADR-0197
  - IADR-0210
  - IADR-0369
  - IADR-0377
  - IADR-0457
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-05（可用性 99.9%。計画停止を除く）/ NFR-18
  - planning:projects/microservices-platform/06_technical/06_migration-roadmap.md
  - planning:projects/microservices-platform/07_adr/ADR-0002（DB per service）
---

# IADR-0459: 切替は破棄と作り直し。消すのは DB と realm の単位、確かめるのは作り直しの時刻（#457）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: endazon（利用者裁定 2026-08-16・オーナー再確認 2026-09-25 —— 破棄と realm の作り直し）／ Claude Code（破棄の境界・方式・検証の起案）

## 起点・関連

- issue: #457（親 #454）
- 作業仕様書: `.ai-context/specs/20260925_457_cutover-discard-and-rebuild.md`
- 先行の下書き: `.ai-context/specs/20260909_issue-457_cutover-decision-table-draft.md`（PR #1355。承認欄は空のまま）
- 移行仕様書（人が読む正）: `docs/migration/cutover-discard-and-rebuild.md`
- 検証スクリプト: `scripts/measure-cutover-inventory.js`
- 関連 IADR: [IADR-0082](IADR-0082_local-k8s-infra-persistence.md) 決定 4（realm を作り直す破壊的経路）／
  [IADR-0369](IADR-0369_persist-by-default-and-realm-reconcile-job.md)（静的 import ＋差分 Job・門 G9 / G10 / G11）／
  [IADR-0197](IADR-0197_realm-rename-and-auth-policy.md)（realm 名 `platform`）／
  [IADR-0210](IADR-0210_local-k8s-observability-persistence.md)（Qdrant・可観測性の永続化）／
  [IADR-0457](IADR-0457_local-vault-file-storage-pvc-and-in-pod-unseal.md)（Vault の永続化 —— 破棄しない側）

## コンテキストと課題

#457 は「6 資産それぞれを移行するか破棄するかを利用者が決める」ことを待って 2026-08-30 から止まっていた。
判断は **2026-08-16 に既に下りていた**（#457 コメント: 6 資産はすべて破棄・realm は realm.json から作り直す・
`authz_svc` は seed から再投入・AST 側 DB は射程外）が、9/09 の下書き（PR #1355）と 9/11 の監査は承認欄が空のまま
「承認待ち」と扱った。**2026-09-25 にオーナーが 8/16 の裁定の有効性を再確認した。**

裁定は「何を捨てるか」を決めたが、**「どう捨てるか」は決めていない。** 稼働構成を走査すると、
捨てる資産と捨ててはならない資産が**同じ入れ物に同居している**:

| 入れ物 | 捨てる | 同居していて捨ててはならない |
| --- | --- | --- |
| Postgres（`platform-infra`・PVC `postgres-data`） | MSP の DB 13 本（`wikijs` を含む） | **AST の DB 7 本**（裁定で射程外。オーナーの売買 PoC が使っている） |
| Keycloak（H2・PVC `keycloak-data`） | realm `platform`（旧名 `microservices-platform` が残っていればそれも） | **master realm と AST realm**（ConfigMap `keycloak-realms` が同梱） |
| Prometheus（PVC `prometheus-data`） | MSP の履歴 | AST のメトリクスも同じ TSDB に入る（裁定は可観測性を破棄としており、分けて残す手段も無い） |

**「PVC を消して作り直す」を一律に当てると、裁定が射程外とした AST の DB と、6 資産に含まれない master / AST realm まで消える。**

## 決定

### 決定 1: 裁定を記録する（6 資産は破棄・realm は realm.json から作り直す）

| 資産 | 扱い | 作り直した後の状態の出どころ |
| --- | --- | --- |
| platform アプリ DB（13 本） | 破棄 | 各サービスの起動時マイグレーション。`authz_svc` は `deploy/local/abac-seed/` を `scripts/seed-abac-policies.js` で、タグ辞書は `scripts/seed-tag-dictionary.js` で再投入 |
| Keycloak realm | realm.json から作り直す | `deploy/keycloak/microservices-platform-realm.json`（静的 import）＋差分 Job（`IADR-0369` 決定 2） |
| Qdrant | 破棄 | 空。文書が入り直せば索引が作られる |
| MinIO | 破棄 | 空。バケットは起動時に作られる（`EnsureBucketOnStartup`） |
| Wiki.js | 破棄 | 空。初期化は `deploy/local/wikijs-setup/bootstrap.sh`（冪等） |
| 可観測性データ | 破棄 | 空 |

- **AST 側 DB・AST namespace・AST realm は射程外**（2026-08-16 裁定）。**Vault は 6 資産に含まれない**（SC-22 で入れた秘密と AST のブローカー資格情報がある）。どちらも触らない。

### 決定 2: 消す単位は「資産」であり「入れ物」ではない。共有の PVC は消さない

| 資産 | 消し方 |
| --- | --- |
| MSP の DB 13 本 | **DB 単位で `DROP DATABASE … WITH (FORCE)` → `CREATE DATABASE … OWNER kp`**。`postgres-data` は消さない。SQL は `deploy/local/infra/postgres.yaml` の初期化 SQL から `measure-cutover-inventory.js --print-recreate-sql` が導出して**表示するだけ**（DB 名を文書へ書き写さない） |
| realm | **realm 単位で削除し、Keycloak を再起動する。** `--import-realm` は既存の realm を黙って飛ばす（IGNORE_EXISTING）ので、**消した realm だけが ConfigMap から入り直し、master と AST realm は触られない。** その後に差分 Job を当て、門 G9 で差分 0 を確かめる。`keycloak-data` は消さない —— `IADR-0082` 決定 4 の破壊的経路（PVC ごと消す）は **master と AST realm まで消すので使わない** |
| Qdrant / MinIO / Wiki.js の PVC / Prometheus・Loki・Tempo | **PVC ごと作り直す**（MSP 専用、または裁定が破棄とした入れ物）。PVC を消してから使っている Pod を消し（Pod が居る間は削除が保留される）、起動器（`scripts/k8s-local-up.sh`）に PVC を作り直させる。**Deployment には触らない**（下記） |
| RabbitMQ の MSP のキュー | 6 資産ではないが、**滞留した旧イベント（文書の更新・削除）は作り直した DB と索引に孤児を作る。** MSP のキュー（`<サービス>.<キュー>`・サービスは `pipeline.json` の steps）だけを空にする。AST のキューは触らない |

- 🔴 **MSP のサービスを `kubectl scale` で止めない。** chart の Deployment は Helm がサーバサイド apply で所有しており、
  `kubectl` で `replicas` を書くと所有者が移って**以後の `helm upgrade` が conflict で失敗し続ける**
  （[IADR-0377](IADR-0377_mesh-mtls-single-writer-and-drift-gate.md) が mTLS モードで実測した事故と同じ形）。chart の値でも 0 にできない
  （`$svc.replicas | default 1` は 0 を既定の 1 に置き換える）。代わりに **DB を消す（`WITH (FORCE)` が接続を切り、表が無いので
  書けなくなる）→ MSP のキューを空にする（DB を消した**後**。先に空にすると処理に失敗した旧イベントが再試行で戻る）→
  `kubectl rollout restart`（Helm が所有しない注記を書くだけ）でマイグレーションを当てさせる**順で静止の代わりにする。

### 決定 3: 方式はメンテナンスウィンドウ（静止 → 破棄 → 再構築 → 検証 → 再開）。段階切替は採らない

- **段階切替（新旧の並走）が守るのは「移すデータ」である。破棄の裁定で移すデータが無くなった**ので、並走させる対象が無い。
  同じクラスタ上の同じ配備を作り直すだけであり、旧と新が別に存在しない。
- **可用性 99.9%（`NFR-05`）は計画停止を除く。** しかも本切替は go-live の**前**の作業であり、SLO の評価期間に入らない。
- **窓の長さは実測で見積もる**（リハーサルで測る。本 IADR では値を置かない）。窓を開く**時刻はオーナーが選ぶ** ——
  realm の作り直しで AST のサービスアカウントのトークンと人のセッションが切れるため、**売買 PoC を止めてよい時間帯**を選ぶ必要がある。

### 決定 4: 検証は「空か」ではなく「作り直されたか」を時刻で見る。触らない側が作り直されていないことも見る

`scripts/measure-cutover-inventory.js --since <破棄を始めた時刻>`:

| 側 | 見るもの | 合格の条件 |
| --- | --- | --- |
| 破棄した側 | MSP の DB 13 本の作成時刻（`PG_VERSION` の更新時刻）・realm の人間の利用者の `createdTimestamp`・作り直した PVC の `creationTimestamp`・Prometheus の最古サンプル | `since` 以降 |
| **触らない側（陰性対照）** | AST の DB 7 本の作成時刻・`postgres-data` / `keycloak-data` / `vault-data` の `creationTimestamp` | **`since` より前のまま** |
| 中身 | realm `platform` がある・旧名が無い・seed 利用者とクライアントがそろう／`authz_svc` の属性辞書とポリシーが seed と一致／Wiki.js のページ 0／Qdrant の点 0・MinIO のオブジェクト 0（**書き込みの再開前に測る**）／MSP のキューの滞留 0 | 各行のとおり |

- **件数 0 を主たる判定にしない理由**: 再構築の直後から書き込みは始まり得る（合成監視・seed・AST の取り込み）。件数 0 は測る時刻に依存して脆い。**作成時刻は、後から入った書き込みに影響されない。**
- **陰性対照を置く理由**: 本切替で最も起きやすい事故は**消しすぎ**である（決定 2 の表）。「作り直された」だけを見る検証は、`postgres-data` を消して AST の DB まで作り直しても緑になる。
- 事前実測（`--dump before.json`）を切替後の `--baseline` に渡すと、**切替前後の件数突合**（2026-08-16 の実測値が基準値になる、と #457 が求めた形）を参考表として出す。
- 収集と集計を分け（`--dump` / `--input`）、集計・判定は純関数として `scripts.repo.test.js` が固定する（`measure-abac-combinations.js` と同じ型）。

### 決定 5: ロールバックは「前進復旧」。破棄は戻さない。保全は任意

- 破棄は裁定であり、**戻す先を作らないことが既定である。** 失敗時は再構築（起動器の再実行・冪等）をやり直して前へ進める。
- **任意の保全**: オーナーが望むなら、破棄の直前に MSP の DB の `pg_dump` と realm のエクスポートを取る（手順は移行仕様書）。
  これは「戻せるようにする」ためではなく**調査用の記録**である。**取るか取らないかはオーナーが決める。**

## 理由

- **決定 2**: 裁定の文言は資産を名指ししており、入れ物を名指ししていない。入れ物ごと消すと、裁定が射程外と明言した AST の DB と、
  6 資産に含まれない master / AST realm を**裁定なしに**消すことになる。DB 単位・realm 単位の削除は、既存の機構
  （初期化 SQL・`--import-realm` の IGNORE_EXISTING・差分 Job）をそのまま使える。
- **決定 3**: 方式の選択肢を分けていたのは「移すデータの整合をどう保つか」であり、その前提が消えた。
- **決定 4**: 「検証スクリプトが緑」を「正しく切り替わった」と読ませるには、**正しくない切替（消し足りない・消しすぎ）の両方で赤になる**必要がある。
  試験は両方向の入力で判定を固定する。

## 結果

- 良い影響: #457 の残作業のうち判断表・方式・手順・検証スクリプトが揃う。**切替の実行とリハーサルだけがオーナー作業として残る。**
- 良い影響: 旧 realm 名（`microservices-platform`）の食い違いが残っていれば、realm の作り直しで同時に解消する（2026-08-16 裁定のとおり）。
- 悪い影響 / トレードオフ: **realm の人間の利用者の資格情報（TOTP 登録を含む）は失われる。** seed 利用者は realm.json の初期値と
  `CONFIGURE_TOTP` で作り直され、実行時に作った利用者は作り直されない。**オーナーの売買 PoC のログインに影響する。**
- 悪い影響 / トレードオフ: realm のクライアントの secret は realm.json の宣言値に戻る。宣言値と違う値を配っている（ローテーション済み）なら、
  作り直しの後に配り直す必要がある。
- 悪い影響 / トレードオフ: Prometheus の履歴は AST の分も消える（裁定が可観測性を破棄としたため。分けて残す手段が無い）。

### 残るもの

1. **オーナー**: 切替の実行（稼働クラスタ）・窓の時刻の選定・任意の保全の要否・TOTP の再登録・実行時に作った利用者の作り直し。
2. **オーナー（または使い捨てクラスタを持つ者）**: リハーサル（移行仕様書の手順を使い捨てクラスタで通し、窓の長さを測る）。
3. **オーナー**: 旧 ArgoCD Application・イメージ・不要ブランチの整理（稼働クラスタ・レジストリ・リモートへの破壊的操作）。
4. **実装（切替の後）**: `CLAUDE.md` / `AGENTS.md` / `docs/` / README の「再実装後の実態」への更新と、#454 の残存 issue の最終トリアージ。
5. 🔴 **収集部（`kubectl` を叩く部分）は稼働環境で一度も走らせていない。** Pod ラベル・kcadm の出力形・Qdrant / Prometheus の応答形・
   MinIO の Pod に `ls` があること・`rabbitmqctl` の JSON 形はいずれも未検証であり、**初回の事前実測がその検証を兼ねる**
   （形が合わなければ例外で止まる。読めなかった資産は fail として出る）。
6. go-live の前提（#439 / #458）は 2026-09-25 時点で OPEN である。本 IADR は切替の**手段**を定めるものであり、**実行の時期**を定めない。

## 関連

- Supersedes: なし
- Superseded by: なし
