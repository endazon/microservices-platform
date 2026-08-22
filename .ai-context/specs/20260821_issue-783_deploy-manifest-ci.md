---
title: 作業仕様書 — chart / overlay の検証を CI ジョブ化し、走査漏れと沈黙の緑を機械で止める
type: spec
status: in-progress
related_ids:
  - NFR
author: claude
created: 2026-08-21
updated: 2026-08-22
plan_refs:
  - "ADR-0007（CI/CD）"
  - "ADR-0021（エッジ・実行基盤）"
related_adrs:
  - IADR-0130
  - IADR-0209
  - IADR-0169
  - IADR-0240
issue: "#783"
related_issues:
  - "#442"
  - "#466"
---

# 作業仕様書: chart / overlay の検証を CI ジョブ化する

## 起点となる計画書（トレーサビリティ）

- 機能要求: 該当なし（**NFR**。CI 基盤。`traceability.md`「場合 2」＝メタ作業に当たる番号が計画側に無い）
- 関連計画 ADR: `ADR-0007`（CI/CD）／ `ADR-0021`（エッジ・実行基盤）
- 親 issue: **#442**（エッジ・実行基盤・CI/CD の再構築）の子 5

## 着手前の再検証（実測）

**#783 の現況記述は正しかった。** `.github/workflows/` 全 14 本に helm / kustomize / kubeconform の
検証ジョブは 1 つも無い。

一方、**ブロック理由「helm / kustomize が手元にも CI にも無い」は成り立たなくなった**。
2026-08-21 にこのコンテナで実測した結果:

```console
$ helm lint deploy/helm/microservices-platform
[INFO] Chart.yaml: icon is recommended
1 chart(s) linted, 0 chart(s) failed

$ find deploy -name kustomization.yaml | sed 's#/kustomization.yaml##' | while read d; do
    kubectl kustomize "$d" >/dev/null && echo "OK  $d" || echo "FAIL $d"; done
OK  deploy/local/edge
OK  deploy/local/edge/tls
OK  deploy/local/headlamp
OK  deploy/local/infra
OK  deploy/local/infra-persistence
OK  deploy/local/observability
OK  deploy/local/observability-persistence
OK  deploy/local/vault
```

**overlay は 8 件である。** 事前調査は 6 件と報告していたが、実際に `find` で引いたら 8 件だった
（`infra-persistence` / `observability-persistence` が漏れていた）。**この 2 件の差が本作業の設計を
決める** —— 列挙をワークフローへ書くと、次に overlay が増えたとき静かに検査対象から外れる。
本リポジトリが 4 回踏んだ「`paths:` の片側取りこぼし」（#558 / #562 / #747 / #801）と同じ型である。

## スコープ

- `scripts/check-deploy-manifests.js` を新設し、**overlay と chart を走査で発見**して検証する
- `.github/workflows/ci.yml` に `deploy-manifests` ジョブを追加する
- `scripts/scripts.repo.test.js` に「ジョブが fail-open の抜け道を使っていない」突合を足す

### スコープ外

- **統合スタックを CI で起こす経路**（#783 のやること④）。これは **#466** の射程であり、
  GitHub Actions の実行時間予算（全 PR ゲートか nightly か）という**利用者判断**が要る
- `helm template` のスナップショット比較。**まず「壊れていたら落ちる」を作る**のが先で、
  スナップショットは差分の意味を読む仕組みが要る（別 issue）
- Istio / cert-manager 等の CRD を要する検証（`kubeconform` の schema 追加）。CRD スキーマの
  供給元を決める判断が要る

## 設計

### 1. 列挙を持たない —— 走査で発見する

`deploy/` 配下の `kustomization.yaml` と `deploy/helm/*/Chart.yaml` を**走査で集める**。
ワークフローにも script にも overlay 名を書かない。

### 2. 0 件走査で緑を返さない

overlay が 0 件、または chart が 0 件なら **exit 1**。走査が壊れて 0 件になったときに
「違反ありません」で緑を返すのが、本リポジトリが繰り返し踏んできた「**沈黙の exit 0**」（#797）である。

### 3. ツール不在は fail-closed。抜け道は 1 つだけ、CI では使わせない

`helm` / `kubectl` が PATH に無いとき、**既定は exit 1**（何を入れればよいかを表示する）。
`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` のときだけ notice を出して skip する。

**この抜け道を CI が使っていないことを機械で固定する**（`scripts.repo.test.js`）。
[IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) の
`include ⊆ paths` 突合と同じ型で、「片方に足してもう片方に足し忘れる」を先回りで塞ぐ。

## 受け入れ基準

1. `node scripts/check-deploy-manifests.js` が **exit 0** で、**発見した overlay 数と chart 数を出力**する
2. `--self-test` があり、CI が本走査の前に呼ぶ
3. **変異試験 A**: overlay の 1 つを壊す（存在しない resource を足す）と **exit 1** する
4. **変異試験 B**: chart を壊す（`Chart.yaml` の必須項目を削る）と **exit 1** する
5. **変異試験 C**: 走査ルートを空にすると（0 件）**exit 1** する
6. **変異試験 D**: `ci.yml` から `deploy-manifests` ジョブを消す、または
   `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS` を CI で立てると、`scripts.repo.test.js` の突合が **fail** する
7. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
8. **新しい overlay を足しても検査対象に自動で入る**（ハードコードした列挙が無いことの確認）

## 実行結果（証跡）

環境（[[IADR-0180]] に従い本セッションで測り直した）: dotnet 10.0.400 / dockerd v29.3.1（再起動後 2 秒で READY）/
helm v3.21.4 / kubectl / k3d v5.7.4 / node v22.22.2。`src/ai-stock-trading` は populate 済み。
🔴 **このワークツリーは shallow clone**（`git rev-parse --is-shallow-repository` = `true`）であり、
`git log` / `git blame` を出典に引いていない（planning#410）。

### 受け入れ基準

| # | 基準 | 実行したコマンド | 結果 |
| --- | --- | --- | --- |
| 1 | 本走査が exit 0 で件数を出す | `node scripts/check-deploy-manifests.js` | **EXIT=0**「chart 1 件 / overlay 8 件がレンダリングできる」 |
| 2 | `--self-test` があり CI が本走査の前に呼ぶ | `node scripts/check-deploy-manifests.js --self-test` | **self-test OK: 5 件**。`ci.yml` の `deploy-manifests` ジョブが本走査の前に呼ぶ |
| 7 | 伴走テストが緑 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **✓ 575 tests passed**（新規 2 本を含む） |
| 8 | 新しい overlay が自動で入る | 走査で発見する設計。変異 D-3 が直書きを禁止する | 下記 D-3 |

### 変異試験（**6 本すべて実測。素通りは無い**）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| A | `deploy/local/vault/kustomization.yaml` へ存在しない resource を足す | exit 1 | **EXIT=1**「kubectl kustomize が失敗した: deploy/local/vault」 |
| B | `deploy/helm/microservices-platform/Chart.yaml` から `name:` を削る | exit 1 | **EXIT=1**「helm lint が失敗した」＋「helm template が失敗した」の 2 件 |
| C | 走査ルートを空のディレクトリにする | exit 1 | **failures 2 件**（overlay 0 件 / chart 0 件をそれぞれ検出） |
| D-1 | `ci.yml` から `deploy-manifests` ジョブを消す | 突合テストが fail | **AssertionError**「ci.yml に deploy-manifests ジョブが無い」 |
| D-2 | `ci.yml` で `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS: "1"` を立てる | 同上 | **AssertionError**「ci.yml が … を立てている。立てると helm / kubectl の導入が失敗しても検査が素通りする」 |
| D-3 | ジョブ本文へ overlay パス（`deploy/local/vault`）を直書きする | 同上 | **AssertionError**「deploy-manifests ジョブに overlay のパスが直書きされている」。スイート EXIT=1 |

各変異のあと**必ず復旧を確認**した（A/B/C は本走査 EXIT=0、D-1〜D-3 は `✓ 575 tests passed`）。

### 追随した母集合

- `scripts/scripts.repo.test.js` の**検査器の母集合ラチェット**を `35` → **`36`** へ。
  **これはラチェットが設計どおり発火したもの**であり、新設時にまず落ちて宣言を促す仕組みである。
  `TRACKED_CHECKERS` / `HEAD_CHECKERS` には**載せない** —— 本検査器は git を一切呼ばず
  `fs` と外部コマンドで走る（`check-trace-blocks.js` と同じ扱い）。
- `scripts/README.md` に 1 行追加（`check-image-mapping.js` の直後。インフラ系の並び）。

### 引いた母集合と除外理由

| 軸 | コマンド | 結果 |
| --- | --- | --- |
| overlay | `find deploy -name kustomization.yaml` | **8 件**。事前調査は 6 件と報告していたが、`infra-persistence` / `observability-persistence` が漏れていた |
| chart | `find deploy/helm -name Chart.yaml` | 1 件（`deploy/helm/microservices-platform`） |

**除外したもの**: 依存 chart の展開先 `charts/`（上流の chart であり本リポジトリの成果物ではない）。
`node_modules` / `bin` / `obj` / `dist` / `coverage` / `.git`（走査の一般的な除外）。
`src/ai-stock-trading`（submodule。`deploy/` 配下に無いため走査に入らない）。

**この 6 → 8 の差が本作業の設計を決めた。** 列挙をワークフローへ書くと、次に overlay が
増えたとき静かに検査対象から外れる。だから走査で発見し、直書きを D-3 の突合で禁止した。


---

## ［2026-08-21 追記 / #783］受け入れ基準の再点検 —— **2 件が未達だった**

PR #878 の AI レビュー（第 2 回・**🔴 重大**）が「#783 は部分実装なのに `Closes` になっている」と
指摘した。**指摘は正しい。** issue #783 の受け入れ基準を issue 本文から引き直したところ、
本作業が満たしたのは **4 件中 2 件**だった。

| # | issue #783 の受け入れ基準 | 判定 |
| --- | --- | --- |
| 1 | chart / overlay の構文エラー・スキーマ不整合が **PR で止まる** | ✅ 達成（`deploy-manifests` ジョブが CI で success） |
| 2 | 子 1 が `k8s-local-up.test.js` に置いた暫定の静的検査を、本ジョブへ**移すか「二重に持たない」判断を明示する** | ❌ **未達だった** → 本追記で判断を明示（下記） |
| 3 | **変異試験**をしている（壊すと実際に落ちる） | ✅ 達成（6 本を実測） |
| 4 | `.github/workflows/` の変更で**起動条件・必須チェックが変わっていない**ことを確認した記録がある | ❌ **未達だった** → 本追記で記録（下記） |

さらに issue #783 の「やること」は **2 つ**あり、本作業は **① だけ**である。

1. chart / overlay を検証する CI ジョブ ← **本作業**
2. **統合スタックを CI で起こす経路**（#466 が載る土台） ← **未着手**

**したがって `Closes #783` は誤りである。`Refs #783` へ改める。**
本リポジトリは #801 で「未達の基準を残したまま `Closes` で閉じ、再オープンした」事故を記録しており、
先例（#787 → PR #816 ／ #493 → PR #818）はいずれも `Refs` に留めている。**同じ判断を採る。**

### 基準 2 への回答 —— **移さない。二重にもならない**

`scripts/k8s-local-up.test.js`（1,675 行）と `scripts/check-deploy-manifests.js` は**見ている面が違う**。

| | `k8s-local-up.test.js` | `check-deploy-manifests.js` |
| --- | --- | --- |
| 何を見るか | **`k8s-local-up.sh` がどの overlay を、どのゲートで、どの順に apply するか**（外部バイナリを記録スタブへ差し替えて呼び出し列を固定する） | **各 overlay / chart が実際にレンダリングできるか** |
| 例 | 「`OBSERVABILITY` 無効なら永続化 overlay が現れない」「cert-manager の CRD 待ちが tls overlay の apply より前にある」 | 「`kubectl kustomize deploy/local/vault` が成功し、出力が空でない」 |
| 壊れ方 | ゲートの意味論・順序の退行 | マニフェストの構文・参照切れ |

**片方が他方を代替しない。** `kustomize build` が通っても apply 順序が壊れていれば #779 が固定した事故は再発するし、
逆に順序が正しくても overlay が build できなければ apply の時点で落ちる。

**内容そのものを見ている少数の assertion**（`infra` kustomization に `inotify-sysctl.yaml` が収録されているか、
ESO マニフェストの `apiVersion` が `v1` か）も**重複しない** —— これらは `kustomize build` が
**成功したうえで中身が違う**場合を捕まえるものであり、レンダリング検査では素通りする。

したがって [IADR-0141]「参照点を 1 つに畳む」に照らしても**畳む対象が無い**。移設も削除もしない。

### 基準 4 への回答 —— 起動条件・必須チェックは変わっていない（実測）

**① `ci.yml` の起動条件は変更していない。** 追加したのは `deploy-manifests` ジョブ 1 件だけで、
`on:` ブロックには一切触れていない。

```yaml
on:
  push:
    branches: [develop, main]
  pull_request:
    types: [opened, synchronize, reopened]
```

**② 必須チェックの集合も変わっていない。** `docs/ai-workflow.md` §必須チェックの有効化 が挙げる 6 件は
`build-and-test` / `lint` / `commit-messages` / `pr-title` / `image-build` / `claude-review` であり、
**`deploy-manifests` は必須にしていない**（同表を変更していない）。新設ジョブが自動的に必須になることはない。

**③ 実行された事実**: PR #878 の `deploy-manifests` は `conclusion: success`（`05:20:45` → `05:20:59`）。

### 🟢 指摘への回答 —— `paths:` フィルタを付けないのは意図的である

レビューは「`deploy-manifests` は `paths:` を持たず全 PR で起動する（setup コストが毎回乗る）」を挙げた。
**意図的にそうしている。** 理由は 3 つ。

1. **`ci.yml` のジョブは元々 `paths:` を持たない。** `ci.yml` 自体が全 PR で起動する設計であり、
   このジョブだけ例外にすると起動条件が読みづらくなる。
2. **`paths:` を付けると、この PR が塞いだのと同じ穴を開けることになる。** chart / overlay は
   `deploy/` の外の変更（生成スクリプト・共通の値ファイル）でも壊れうる。**本リポジトリは
   「`paths:` の片側取りこぼし」を 4 回踏んでいる**（#558 / #562 / #747 / #801）。
   絞るなら [IADR-0209] と同型の `包含 ⊆ paths` 突合をもう 1 本足す必要があり、**割に合わない**。
3. **コストが小さい。** 実測 **14 秒**（`azure/setup-helm` / `azure/setup-kubectl` 込み）。
   `build-and-test`（数分）に比べて無視できる。

**この判断は測り直す価値がある**（ジョブが重くなったら 2 の費用対効果が変わる）。そのときは
「`paths:` を足す」ではなく「**nightly へ分離する**」を先に検討すること —— #783 のスコープが
「実行時間が PR ゲートの上限を超えるなら nightly へ分離する」と既に書いている。

### 残る作業（#783 を閉じるために要るもの）

- [ ] やること② **統合スタックを CI で起こす経路**（#466 が載る土台）。
      **実行時間が PR ゲートの上限を超えるなら nightly へ分離する**判断を含む
- 依存の #780（Keycloak をエッジへ・issuer https 化）は**まだ open** である。
      本作業（①）は #780 に依存しなかったが、②は統合スタックを起こすため依存する可能性が高い

---

## ［2026-08-22 追記 / #783］前半の残り（スキーマ突合・必須チェック昇格）に着手

### 着手前の再検証 —— `blocked` ラベルの裏取り

棚卸しセッションの判定「#783 は `blocked` だが前半に実質的な依存は無い」を自分でも裏取りした。
依存として本文に挙がる #779（クローズ済み）・#780（Keycloak を エッジへ、issuer https 化）を確認した
ところ、#780 の変更範囲（Keycloak の Ingress 露出・issuer host・realm 設定・.NET/フロントの issuer 検証）
は helm lint / kustomize build / kubeconform によるチャート・オーバーレイの構文・スキーマ検証と無関係
だった。**前半は #779/#780 いずれにも依存しない**。後半（やること②「統合スタックを CI で起こす経路」・
#466 の土台）は #466 が実ブラウザ OIDC ログインを要求するため #780 に依存し得る
（[IADR-0227] が issuer は移さないと確定済みだが、#466 の受け入れ基準は実クラスタでの認証成功を要求する
ため、#780 の Ingress 露出自体は前提になり得る。**この切り分けは未確定のまま次セッションへ引き継ぐ**）。

### 前半に残っていた 2 件

上の「残る作業」節は issue #783 の「やること②」だけを追跡していたが、**issue #783 の受け入れ基準 1
自体（「chart / overlay の構文エラー・スキーマ不整合が PR で止まる」）が、前回（#878）の実装では
半分しか満たされていなかった。**

1. **スキーマ突合**: `helm lint` / `helm template` / `kubectl kustomize` は**構文検証のみ**で、
   Kubernetes スキーマへの適合（型・enum 等）は見ていなかった。`.ai-context/specs/` の当初版
   「スコープ外」節がこれを「CRD スキーマの供給元を決める判断が要る」として保留していた。
   **[IADR-0240] でその判断を確定し、`kubeconform` ＋ `datreeio/CRDs-catalog` を追加した。**
   設計・実測（変異試験含む）は IADR 本文を参照。
2. **必須チェックの昇格**: `docs/ai-workflow.md` §必須チェックの有効化 の表に、chart/overlay 検証が
   走る `static-checks-units` ジョブを追加した（下記）。

### 変異試験（2 本。C# 同様、壊すと実際に落ちることを先に実測してから判定した）

| # | 変異 | 対象 | `helm lint`/`helm template`/`kubectl kustomize` | `kubeconform` |
| --- | --- | --- | --- | --- |
| E（chart） | `deploy/helm/microservices-platform/templates/deployment.yaml` の `replicas: {{ $svc.replicas \| default 1 }}` を `replicas: "not-a-number"` へ | `ingestion-service` / `conversion-service` の 2 Deployment | **EXIT=0**（両方） | **EXIT=1**「got string, want null or integer」× 2 件 |
| F（overlay） | `deploy/local/headlamp/headlamp.yaml` の `replicas: 1` を `replicas: "not-a-number"` へ | `headlamp` Deployment | **EXIT=0**（`kubectl kustomize`） | **EXIT=1**「got string, want null or integer」 |

各変異のあと復旧を確認した（E/F とも該当ファイルの `git diff` が空になることを確認）。
**E・F とも「レンダリングは通るがスキーマには違反する」を実際に再現し、拡張前の検証（helm/kubectl のみ）
がこれを検出しないこと、拡張後（kubeconform 追加）が検出することの両方を実測した。**

### 実行環境（本セッションの実測）

本作業は共有ワークツリーでの並行セッション事故（他セッションの未コミット変更を巻き込むリスク）を
避けるため、`.claude/worktrees/issue-783-deploy-schema-validation`（`origin/develop` 基点の
git worktree）で行った。helm v4.2.1、kubectl（kustomize v5.7.1 内蔵）、kubeconform v0.8.0
（`yannh/kubeconform` の公式リリースから取得。ローカル検証専用でリポジトリには含めない）。

🔴 **`scripts/check-deploy-manifests.js` の `hasTool()`（`command -v` を `spawnSync(..., {shell:true})`
で呼ぶ）は、本セッションの Windows 環境では常に失敗する**（Node の `shell:true` は win32 で `cmd.exe`
を起動し、POSIX の `command` ビルトインを持たないため）。この挙動は本変更で**新たに壊したものではない**
——同じ機構を使う既存の helm / kubectl 判定も同一環境で同じく失敗する（`git stash` 相当の巻き戻しで
変更前のコードでも再現を確認済み）。CI は `ubuntu-latest`（POSIX シェル）で走るため実害は無いが、
**この Windows 環境では `node scripts/check-deploy-manifests.js` の end-to-end 実行そのものは
自己検証できない**。かわりに `run()` / `validateSchema()` が呼ぶ実処理（`spawnSync` を `shell:true`
無しで直接 `helm.exe` / `kubeconform.exe` を起動する経路）を単体で再現し、変異試験 E・F を含め
実際の helm / kubectl / kubeconform 呼び出しで検証した（上表）。`--self-test`（`node
scripts/check-deploy-manifests.js --self-test`）は 6 件全て OK（新設 1 件を含む。**本 Windows 環境では
`hasTool()` が常に false を返すため、追加した「kubeconform 不在時の fail-closed」自己試験は
早期 return せず実際にアサーションを通った**）。

### 必須チェック昇格の実態 —— branch protection は未配備のまま

`gh api repos/endazon/microservices-platform/branches/develop/protection` を実測したところ
`404 Branch not protected` だった（2026-08-22）。**develop に branch protection 自体が無い**ため、
「必須チェックへ昇格」は GitHub API を叩く行為ではなく、`docs/ai-workflow.md` §必須チェックの
有効化 の表（**将来 branch protection を配備するときに `contexts` へ並べる一覧**）へ、
chart/overlay 検証が走るジョブ名を追記するドキュメント変更である。追記した内容と根拠は
`docs/ai-workflow.md` 本体を参照（要点: check 名は `static-checks-units`。理由は本ジョブが
`ci.yml` の `on:` に `paths:` フィルタを持たず全 PR で起動し、`types:` に `reopened` を含み、
matrix ジョブでもないため、既存の「必須チェックに指定する際の注意」の 3 条件をいずれも満たす）。

### ［2026-08-22 追記 2 / #783］`ci.yml` への導入（#900 / #882 の着地順調整の後）

`.github/workflows/**` は #900 / #882 も触るため、着手前に確認を取った。**#882 は `ci.yml` を
触らずに済むことが判明し順番待ちは解除、#900 は床の実測待ちで停止中**だったため、本作業が先に
`static-checks-units` ジョブへ `kubeconform` の導入ステップを足した。

- **バージョン pin**: `KUBECONFORM_VERSION=v0.8.0`（`latest` 追従はしない。ESO helm install の
  `--version` 検査と同じ方針）。
- **チェックサム検証**: 公式リリースの `CHECKSUMS` から取得した `kubeconform-linux-amd64.tar.gz` の
  SHA256（`9bc2bffbf71f261128533edaf912153948b7ff238f9a531ae6d34466ec287883`）を埋め込み、
  `sha256sum -c` で突合してから展開・導入する。
- 導入位置は `azure/setup-helm@v4` / `azure/setup-kubectl@v4` の直後（同じ `static-checks-units`
  ジョブ。別ジョブへ離すと検査が動かないため）。

`scripts/scripts.repo.test.js` の CI 突合テスト（#783 既存の `ok('NFR / #783: ci.yml に
deploy-manifests ジョブが在り...')`）へ、kubeconform 導入の検査を 3 本追加した
（導入されていること／バージョン pin されていること／チェックサム検証をしていること）。

#### 変異試験（2 本。追加した 3 本のうち version pin ／ checksum の 2 本を実測。3 本目は
既存の helm/kubectl 導入検査と同型の `assert.match` であり、その型は既に本仕様書の初版
（変異試験 D 系列）で実証済みのため重複実測はしていない）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| G | `ci.yml` の `KUBECONFORM_VERSION=v0.8.0` を `KUBECONFORM_VERSION=latest` へ | 突合テストが fail | **AssertionError**「kubeconform の導入がバージョン pin されていない（latest 追従になっている）」 |
| H | `ci.yml` から `sha256sum -c` の行を削る | 突合テストが fail | **AssertionError**「kubeconform の導入がチェックサム検証をしていない」 |

各変異のあと `git diff -- .github/workflows/ci.yml` で該当箇所のみの変化を確認してから実行し、
復旧後に `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が `✓ 579 tests passed` へ戻ることを
確認した。

#### 実行結果（本コミット確定版）

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
✓ 579 tests passed
```

`.github/workflows/ci.yml` の `on:` ブロックは変更していない（push: develop/main・
pull_request: opened/synchronize/reopened のまま）。ジョブの追加・削除も無く、既存ステップの
実行順序も変えていない（`static-checks-units` へステップを 1 つ挿入しただけ）。

### 後半の切り分け（引き継ぎ）

- やること②「統合スタックを CI で起こす経路」（#466 の土台）は**未着手のまま**。
- #780（Keycloak エッジ化・issuer https 化）は依然 OPEN。#466 が実ブラウザ OIDC ログインを要求する
  以上、後半は #780 に依存し得ると判断しているが、**依存の強さ（issuer 自体は不要でも Ingress 露出は
  要るのか等）は未確定**。着手時に #780 の実装内容を再確認すること。
- 実行時間が PR ゲートの上限を超える場合は nightly へ分離する判断が要る（#783 の「やること」欄に
  明記済み）。実測見積もりは未着手。
