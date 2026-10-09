---
title: 作業仕様書 — Argo CD を版のタグの URL へ、k3s を既定で K3S_IMAGE 固定にし、scripts/ が入れる 6 製品を点検の母集合に含める（#1843）
type: spec
status: done
related_ids: [NFR, ADR-0135, ADR-0107, ADR-0112, ADR-0005, ADR-0007, ADR-0008, ADR-0023, ADR-0095, ADR-0110, IADR-0519, IADR-0514, IADR-0248, IADR-0077]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0135_infrastructure-inspection-population-and-immediate-version-pinning.md 決定 1〜3・フォローアップ 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 1（2026-10-09 改訂節）・決定 2〜5
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1・3
  - planning:projects/microservices-platform/10_feedback/20261009_infra-product-inspection-rulings.md 裁定 5・§残るもの
issue: "#1843"
---

# 作業仕様書 — Argo CD・k3s の版の固定と、scripts/ が入れる製品の点検（#1843）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0519** に置く。
> 計画は隣接クローン `project-planning` の `origin/main` を読んだ（読み取り専用）。基点は MSP `origin/develop` `afa9b917`。
> 🔴 **稼働中のクラスタには何も実行しない。** 上流へは匿名の HEAD / GET（manifest・LICENSE・タグの一覧）だけを送った。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0135**（決定 1＝対象は名指しの製品と前提として引く製品の和集合・配備の経路を問わない、決定 2＝版が固定されていない製品は即時に固定・点検は 2026-10-08 回の追補、決定 3＝3 点セット）。部分改定の対象 **ADR-0107 決定 1**、基準 A〜C は **ADR-0107 決定 2〜4**、基準 D は **ADR-0112 決定 1**。名指しの確定は ADR-0005（Istio）・ADR-0007（Argo CD）・ADR-0008（k3s）・ADR-0023（cert-manager）、前提として引くのは ADR-0095（ESO）・ADR-0110（Reloader）。
- 環流の完了記録: planning `10_feedback/20261009_infra-product-inspection-rulings.md`（裁定 5・§残るもの の「次回の点検の対象」）。
- 非機能要件: 無採番（工程の点検。`.claude/rules/traceability.md` のメタ作業の扱い）。
- 起点 issue: **#1843**（planning#750 の裁定 5）。前提: [IADR-0514](../adr/IADR-0514_infra-image-digest-pinning-and-checker.md)（初回の点検・digest 固定）、[IADR-0248](../adr/IADR-0248_integration-stack-ci-readiness-gate.md) 決定 6（k3s の pin を opt-in で足した）。

## 受け入れ基準（issue #1843）

| # | 基準 | 写像 |
| --- | --- | --- |
| 1 | Argo CD を版のタグの URL へ固定する（`stable` を直接 apply しない） | `k8s-local-up.sh` の `ARGOCD_VERSION`（既定 `v3.5.4`・版のタグ以外を拒否）。`k8s-local-up.test.js`・`scripts.repo.test.js` |
| 2 | k3s を既定で `K3S_IMAGE` 固定にする（未指定でも版が固定される） | `k8s-local-up.sh` の既定 `rancher/k3s:v1.35.4-k3s1`。ワークフローは値を持たない（単一の情報源）。両テスト |
| 3 | `scripts/` が入れる 6 製品に基準 A〜D を当て、2026-10-08 回の追補に載せる | `docs/operations/operations.md` §点検の記録 |
| 4 | `operations.md` の母集合の定義に `scripts/` と前提の製品（ESO・Reloader）を加える | 同 §インフラ製品の点検 の表 |
| 5 | RabbitMQ 3.13 系と Keycloak 24.0 が保守の続く系列ではないことを次回の点検の対象として記録する（裁定なし） | 同 §点検の記録 の追補の「次回の点検へ」 |

## 計画が決めていること・決めていないこと

| 計画が決めている | 計画が決めていない（本件で決める。IADR-0519） |
| --- | --- |
| Argo CD はタグの URL、k3s は既定で固定（即時） | どの版にするか |
| 6 製品へ基準 A〜D を 2026-10-08 回の追補で当てる | 6 製品の判定、点検の母集合の引き方（`scripts/` の分） |
| digest の固定は追補で他の製品と同じ扱い（ADR-0135 決定 2） | chart / 上流マニフェストの内側のイメージをどう扱うか |
| — | k3s の版の情報源をどこに置くか（スクリプトかワークフローか） |

## 現状（実測。`afa9b917`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `scripts/` が入れる製品は 6 つ: Istio（`base`・`istiod`・`gateway` の chart 1.30.4）・ESO（chart 2.8.0）・Reloader（chart 2.2.17・image v1.4.22）・cert-manager（release v1.21.1 の `cert-manager.yaml`）・Argo CD（`stable` の `install.yaml`）・k3s（k3d の `cluster create`） | `grep -nE 'helm upgrade --install\|kubectl apply .*https\|k3d cluster create' scripts/*.sh` |
| 2 | Argo CD の install は `scripts/k8s-local-up.sh:992` で `argo-cd/stable/manifests/install.yaml`。直前のコメント `:991` の「URL/バージョンは不変」は事実に反する | 同ファイル |
| 3 | 同じ `stable` の URL を手順として書いた文書が 3 つある: `deploy/argocd/README.md:26`・`deploy/local/argocd/README.md:14`・`docs/how-to/deployment.md:68`（最後の 1 つは `--server-side` も欠けており、#348 の既知の失敗をそのまま踏む） | `git grep 'argo-cd/stable'` |
| 4 | `stable` は現在 **v3.5.4** を指す。`stable` と `v3.5.4` の `install.yaml` は**バイト一致**（sha256 `1feb02cc…e010`）。中のイメージは `quay.io/argoproj/argocd:v3.5.4`・`ghcr.io/dexidp/dex:v2.45.1`・`public.ecr.aws/docker/library/redis:8.2.3-alpine` | `curl` 2 本と `sha256sum`・`raw/stable/VERSION` |
| 5 | k3s は `K3S_IMAGE` を与えたときだけ `--image` を付ける（`:115-125`）。既定は k3d が同梱する版 | 同ファイル |
| 6 | CI の 2 ワークフロー（`integration-stack.yml`・`cutover-rehearsal.yml`）は `K3S_IMAGE: rancher/k3s:v1.35.4-k3s1` を env で与える。値の一致は `scripts.repo.test.js` が突き合わせている | 各ワークフロー |
| 7 | Rancher Desktop 経路は内蔵 k3s を使い、スクリプトは版を選ばない（Rancher Desktop の設定が決める） | `:126-134` |
| 8 | `check-image-digests.js` は `deploy/` と `src/` だけを走査する。`scripts/` の chart・上流マニフェストの内側のイメージは見ない | 同ファイルの冒頭 |

## 設計（正は IADR-0519）

1. **Argo CD**: `ARGOCD_VERSION="${ARGOCD_VERSION:-v3.5.4}"` とし、URL は `argo-cd/${ARGOCD_VERSION}/manifests/install.yaml`。**版のタグ（`vX.Y.Z`）以外は起動前に拒否する**（`stable`・`master` 等のブランチ名で固定が外れる口を塞ぐ。判定は副作用より前）。版は「いま `stable` が指していた版」（実測 4。動作を変えない）。文書 3 つも同じ URL にする（`deployment.md` は `--server-side --force-conflicts` も足す）。
2. **k3s**: `K3S_IMAGE="${K3S_IMAGE:-rancher/k3s:v1.35.4-k3s1}"` とし、`--image` を常に付ける。**版の情報源はスクリプトの既定 1 か所**にし、CI の 2 ワークフローから `K3S_IMAGE` を消す（同じ値を 2 か所に持つと、片側だけの更新で乖離する。ワークフローに残すと「既定は誰が決めるか」が 2 つになる）。実効値は変わらない（同じ文字列が渡る）。Rancher Desktop 経路は対象外（実測 7。IADR-0519 の残余）。
3. **digest**: 6 製品のイメージは chart / 上流マニフェストの内側にあり、本リポジトリに参照の行が無い。digest で固定するには製品ごとに values の上書きか post-render が要る。**本件では版の固定（chart / release / タグ）に留め、digest は追補の点検で「固定していない（理由つき）」と記録し、別 issue に回す**（ADR-0135 決定 2 の「他の製品と同じ扱い」＝例外は理由つきで記録する、の形）。k3s の `--image` だけは本リポジトリに参照があるが、k3d が digest つきの参照を受けるかを本作業では実測できない（実行機にクラスタ・docker が無い）ため、同じく版に留める。
4. **点検の追補**: 6 製品＋ Argo CD の同梱物（dex・redis）に基準 A〜D を当て、`operations.md` §点検の記録 2026-10-08 に**日付つきの追補の小見出し**で足す（初回の表は書き換えない）。基準 B は匿名で index の digest を 2 回解決して一致すること（初回と同じ手順）。
5. **母集合の定義**: `operations.md` の表の「母集合」を `--list`（`deploy/`・`src/`）＋ `scripts/` が入れる製品（引き方の grep を書く）＋ 計画が前提として引く製品（ESO・Reloader）の和集合へ改め、「母集合の外（未決）」の行を「解消」へ改める。
6. **次回の点検へ（裁定なし）**: RabbitMQ 3.13 系・Keycloak 24.0 を追補に記録する。

## テスト（受け入れ基準 1・2 の回帰の固定）

- `k8s-local-up.test.js`:
  - `K3S_IMAGE` 未設定でも `cluster create` に `--image rancher/k3s:vX.Y.Z-k3sN` が付く（既定の期待値 `EXPECTED_DEFAULT_CREATE` を改める）。設定時はその値が勝つ。
  - `ARGOCD=1` の install 行が `argo-cd/vX.Y.Z/manifests/install.yaml` であり、`/stable/` を含まない。
  - `ARGOCD_VERSION=stable` は非 0 で止まり、install 行を出さない。
- `scripts.repo.test.js`:
  - リポジトリ全体（凍結記録の `.ai-context/specs/`・`.ai-context/superpowers/` を除く）で、Argo CD の install の URL の参照が**版のタグ**であり、**スクリプトの既定と同じ版**であること（文書の片側だけの更新を止める）。
  - `k8s-local-up.sh` の `K3S_IMAGE` の既定が版で固定されていること。CI の 2 ワークフローが `K3S_IMAGE` を持たないこと（単一の情報源）。
- **変異で検出力を確かめる**: (a) Argo CD の URL を `stable` に戻す (b) `K3S_IMAGE` の既定を外す (c) ワークフローへ `K3S_IMAGE` を戻す (d) 文書の 1 つだけ版を変える —— 各々でテストが赤になること。

## 母集合の走査（規則 9・10）

誤りの側の文字列で全文書を走査した（`git grep`。凍結記録 `.ai-context/specs/`・`.ai-context/superpowers/` は除く）。

| 走査語 | 当たり | 扱い |
| --- | --- | --- |
| `argo-cd/stable` | `scripts/k8s-local-up.sh:992`・`scripts/k8s-local-up.test.js:1979`・`deploy/argocd/README.md:26`・`deploy/local/argocd/README.md:14`・`docs/how-to/deployment.md:68` | すべて版のタグへ改める |
| `URL/バージョンは不変` | `scripts/k8s-local-up.sh:991`・`scripts/k8s-local-up.test.js:1975` | 事実に合わせて改める |
| `K3S_IMAGE` | `scripts/k8s-local-up.sh:115-124,1164`・`k8s-local-up.test.js:383,1195-1214`・`scripts.repo.test.js:10952,11011`・`integration-stack.yml:62,141`・`cutover-rehearsal.yml:49`・`deploy/local/edge/README.md:51`・`deploy/local/edge/traefik-entrypoint.yaml:20` | 既定で固定に合わせて改める。IADR-0248（凍結）は日付つき追記で受ける |
| `既定バイト等価`（k3s の文脈） | `integration-stack.yml:141`・`k8s-local-up.test.js:1199` | 改める（他の文脈の「既定バイト等価」は対象外） |
| `母集合の外`・`scripts/ が入れる製品` | `docs/operations/operations.md:1681,1756`・`IADR-0514:119` | operations.md は定義を改め、初回の記録の行は残して追補で受ける。IADR-0514（凍結）は日付つき追記 |

**この変更で新たに誤りになる自分の記述（規則 10）**: `integration-stack.yml:56-59` の注記（「pin する理由」は env の直上にある）は、env から `K3S_IMAGE` を消すと宙に浮く —— 注記を「版はスクリプトの既定が持つ」へ改める。`cutover-rehearsal.yml:47` の「integration-stack.yml と同じ pin」も `K3D_VERSION` だけの話へ改める。`scripts.repo.test.js` の #1781 の突き合わせ（`K3D_VERSION`・`K3S_IMAGE`）は `K3D_VERSION` だけになる。

**窓（規則 11）**: 本件は時間差を扱わない（該当なし）。

## 検証

`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/k8s-local-up.test.js`・`node scripts/check-image-digests.js`・`actionlint`・`shellcheck -x scripts/k8s-local-up.sh`・`check-deploy-manifests`・`check-trace-blocks`・`check-adr-numbering`・`check-commit-messages --base origin/develop`・`check-doc-updated --base origin/develop`・`gen-knowledge-graph --check`・`check-reading-budget`・`check-plan-id-qualification`・`check-cross-repo-refs`・`check-doc-links`。

🔴 **integration-stack の経路に触れる**（ワークフローの env から `K3S_IMAGE` を消し、スクリプトの既定で同じ値を渡す）。実効の `k3d cluster create` 引数は変わらない想定だが、マージ前に `integration-stack` を dispatch して確かめる。
