---
title: 作業仕様書 — #1435 wiki-js の Deployment を Recreate にし、新旧 2 Pod の同時 migration で knex のロック表が壊れて integration-stack が間欠的に落ちる形を消す
type: spec
status: done
related_ids: [FR-13, UC-07, ADR-0011, IADR-0020, IADR-0210]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
related_specs:
  - 20260816_issue-787_k8s-observability-persistence
  - 20260925_1499_object-storage-seaweedfs
issue: "1435"
---

# 作業仕様書 — #1435 wiki-js の更新戦略を Recreate へ

## 起点

- issue #1435「[CI] integration-stack が失敗している」（自動起票。同じワークフローの失敗はコメントで積まれる）。
- 積まれた失敗は根本原因が 3 つに分かれる。**本仕様書が直すのは ③ だけ**である。

| # | 形 | 代表 run | 状態 |
| --- | --- | --- | --- |
| ① | minio の Pod が `ErrImagePull` / `ImagePullBackOff`（quay の匿名取得が 401） | 36166083622 ほか 2026-09-12〜25 の大半 | #1513（MinIO → SeaweedFS）で解消済み。以後の run に minio は居ない |
| ② | `check-password-reset-mail.js` の T-25 所要時間が系統差 0 でも不合格 | 36174317714（`b54b719c`） | #1547（時計を ms へ戻す）・#1552（順位和検定）で解消済み |
| ③ | wiki-js の 2 Pod が `CrashLoopBackOff`、ログは `Migration table is already locked` | 36188276324（`3befcd07`）・35277541043（`25ee1753`）・34748189616（`3371783a`） | **未解消（間欠）。本仕様書** |

## ③ の原因（CI ログからの推定。稼働クラスタは使っていない）

1. `k8s-local-up.sh` は ISTIO=1 のとき、`helm upgrade --install` の直後に
   `kubectl -n microservices-platform rollout restart deployment` を打つ（サイドカー注入）。
2. wiki-js の Deployment は既定の `RollingUpdate`（maxSurge 1 / maxUnavailable 0）なので、
   helm が作った Pod（旧 ReplicaSet）を残したまま restart の Pod（新 ReplicaSet）が立つ。
   3 件とも、2 つの Pod の年齢が同じ（21m）で、どちらも `CrashLoopBackOff`（再起動 8 回）。
3. 2 つの Wiki.js 2.5.315 が**空の DB `wikijs` へ同時に** `knex.migrate.latest({ tableName: 'migrations' })` を走らせる。
   Wiki.js 2.5.315 の knex は `0.21.7`（requarks/wiki `package.json`）。knex 0.21 の `ensureTable` は
   ロック表 `migrations_lock` を作ったあと「`select *` が 0 行なら `insert { is_locked: 0 }`」を**排他なしで**行う
   （`lib/migrate/table-creator.js`）。同時に走ると**行が 2 つ**できる。
4. `_lockMigrations` は `update migrations_lock set is_locked=1 where is_locked=0` の更新行数が 1 でなければ
   `Migration table is already locked` を投げる（`lib/migrate/Migrator.js`）。行が 2 つなら毎回 2 行が更新され、
   トランザクションは巻き戻り、**状態が変わらないまま以後すべての起動が同じ理由で落ちる**。
   ログの文言は 3 件ともこの `_lockMigrations` のもの。

**待ちでは直らない**（壊れたのは DB の状態）。**2 つ目の Pod を同時に立てない**ことで形そのものを消す。

## 変更

1. `deploy/helm/microservices-platform/templates/wikijs.yaml` の Deployment に `strategy: { type: Recreate }`。
   - IADR-0210 決定 7「PVC（ReadWriteOnce）を掴む Deployment は Recreate」の**適用漏れ**である。wiki-js は
     PVC `wiki-js-data` を掴む。同決定の母集合は kustomize の overlay だけで、helm チャートが入っていなかった
     （チャート側の seaweedfs は IADR-0461 で Recreate 済み）。**新しい判断は無いので IADR は起こさない。**
   - Recreate にすると restart は旧 Pod を落としてから新 Pod を立てる。旧 Pod は helm install の数秒後に落ちるため、
     CI の流れでは Wiki.js が起動し終わる前（サイドカーの準備前）に止まる。
2. `scripts/k8s-local-up.test.js` に検査を 1 件足す: チャートの**全テンプレート**を読み、`persistentVolumeClaim:` を
   マウントする Deployment の文書すべてに `strategy.type: Recreate` を求める。対象は名前で列挙しない。
   走査が既知の 2 件（seaweedfs / wiki-js）を拾えないときも赤にする（空の母集合で緑にしない）。
3. `docs/operations/operations.md` の永続化の節（Recreate の段落）に、helm チャートの Deployment も同じであることを 1 項足す。

## 母集合（規則 9・10）

- **誤りの側の文字列で走査**: `git grep -l claimName -- '*.yaml' '*.yml'`。
  - `templates/seaweedfs.yaml` → Recreate 済み
  - `templates/wikijs.yaml` → **無し（本件）**
  - `deploy/local/infra-persistence/kustomization.yaml` / `observability-persistence/kustomization.yaml` → #787 の検査が見ている
  - `deploy/local/vault-persistence/deployment-patch.yaml` → base の `deploy/local/vault/vault-dev.yaml` が `strategy: { type: Recreate }`
  - `deploy/local/platform-backup/vault/cronjob.yaml` → CronJob（Deployment ではない）。**除外**
- **この変更で誤りになる自分の記述**: `git grep -n RollingUpdate -- docs deploy scripts '*.md'` と
  `git grep -n Recreate -- docs`。`operations.md` の「計 7 件」は overlay の数として正しいまま（書き換えず、チャート分を別項で足した）。
  確定済みの `.ai-context/specs/`・IADR は書き換えない。

## 受け入れ基準

1. `helm template` で描いた wiki-js の Deployment に `strategy.type: Recreate` が出る。
2. 足した検査が、修正後のチャートで緑、develop の `wikijs.yaml`（Recreate 無し）へ戻すと赤。
3. `helm lint deploy/helm/microservices-platform` が通る。
4. CI（`static-checks` の `node scripts/k8s-local-up.test.js`）が通る。
5. integration-stack の再発は本 PR の CI では観測できない（間欠のため）。3 件の run の署名で原因を示すに留める。

## 検証（ローカル。稼働クラスタは使わない）

```console
$ helm template msp deploy/helm/microservices-platform -f deploy/local/values-local.yaml --show-only templates/wikijs.yaml | grep -A1 "strategy:"
  strategy:
    type: Recreate
$ helm lint deploy/helm/microservices-platform
1 chart(s) linted, 0 chart(s) failed
# 足した検査だけを切り出して実行（修正後 / develop の wikijs.yaml）
  ok  #1435: helm チャートでも PVC を掴む Deployment は Recreate（IADR-0210 決定 7 の母集合を広げる）   → EXIT=0
AssertionError [ERR_ASSERTION]: wikijs.yaml: Deployment wiki-js は PVC を掴むのに spec.strategy が Recreate でない…   → EXIT=1
```

## 射程外

- `wikijs.replicas` を 2 以上にした場合（Recreate でも同時起動になる）。既定は 1 で、Wiki.js 2 の複数レプリカは
  共有ストレージと HA 設定が要る別の話である。
- 既にロック表が壊れたクラスタの復旧（`migrations_lock` の行を 1 つへ戻す）。使い捨ての CI スタックでは不要。
