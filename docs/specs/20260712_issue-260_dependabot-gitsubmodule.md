---
title: Dependabot gitsubmodule による submodule pin 自動更新の有効化（Issue #260）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - IADR-0058
  - IADR-0060
  - IADR-0065
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット。ユニット submodule の pin 鮮度維持が関連)"
related_specs:
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../adr/IADR-0065_public-unit-submodule-ci-fetch-no-token.md"
  - "../how-to/adding-a-unit-submodule.md"
  - "../../.github/dependabot.yml"
---

# 作業仕様書: Dependabot gitsubmodule による submodule pin 自動更新の有効化（Issue #260）

## 起点となる計画書・Issue（トレーサビリティ）

- Issue: #260（起点: #245「サンプルユニット submodule 通し検証とトークン/Renovate 運用」の残作業のうち
  Renovate/Dependabot 有効化部分。`Refs #260`）
- 関連 IADR: [[IADR-0058]]（private submodule の CI 取得）／[[IADR-0060]]（submodule 運用・バージョン固定・
  Renovate/Dependabot 言及）／[[IADR-0065]]（public ユニットの CI 取得はトークン不要・`src/*` 非再帰 init）
- 横断ハンドオフ: planning#22（repo-template への gitsubmodule 同梱）／AST#109
  （AST 自リポへの個別適用。本作業はその 3 リポ横断作業の最終タスク）

## 目的・背景

IADR-0060 決定⑤・`docs/how-to/adding-a-unit-submodule.md` §6 では、submodule の gitlink pin 更新を
Renovate/Dependabot の `git-submodules`（Dependabot 表記では `gitsubmodule`）マネージャで自動化できる
としつつ、有効化はメンテナ判断として繰り延べていた（#245 の残作業として #260 に切り出し）。
本作業はこれを実施し、`.github/dependabot.yml` に `gitsubmodule` エコシステムのブロックを追加する。

ツール選定（Dependabot、Renovate ではない）は project-planning / ai-stock-trading / microservices-platform
の 3 リポ横断で事前にオーナー承認済み（3 リポとも既に Dependabot を採用しており一本化する）。

## 当初案（issue #260 本文）からの変更点と理由

issue #260 の本文（スコープ節）には「対象を `src/*` のユニット submodule に限定し、private な `planning`
を更新対象から除外する」とある。**本実装ではこの方針を変更し、`planning` を除外せず対象に含める。**

理由:

1. 3 リポ横断の統一方針（planning#22 → AST#109 → 本 #260）として、`planning` pin も
   計画リポの前進に追従させることが望ましいと判断された（AST#109 の主目的そのものが
   `planning` pin の自動追従であり、MSP だけ `planning` を除外すると 3 リポで方針が割れる）。
2. Dependabot の `gitsubmodule` エコシステムは `directory: "/"` を指定すると、その配下の `.gitmodules` に
   列挙された **全 submodule** を対象にする。特定 submodule（`planning`）だけを除外することは技術的には
   可能で、`ignore` に `dependency-name`（submodule のパス、例 `planning`）を指定すれば除外できる。
   したがって「除外できないから含める」のではなく、**あえて除外せず含める**（理由 1 の統一方針。
   `planning` pin も追従させたい）。除外を選ぶと、`ignore` の保守が要るうえ 3 リポで方針が割れ、
   また `directory` を `src/ai-stock-trading` 等へ個別指定する代替案は `src/*` ユニット追加のたびに
   `dependabot.yml` 追記を招き、IADR-0060 が目指す「ユニット追加は構成変更のみで完結する」（FR-14）
   という設計方針とも相性が悪い。以上より `directory: "/"` で全 submodule 対象＋除外なしを採る。
3. `planning` は private リポだが、Dependabot 側で該当 PR が生成できない場合でも（下記「private planning
   への Dependabot アクセス」参照）、Dependabot のログにエラーが残るのみで CI 自体は壊れない
   （`gitsubmodule` updater は取得可能な submodule のみ PR を作成する）。設定 PR 自体をブロックする要因には
   ならない。

この変更は 3 リポ共有の context-brief（横断作業の単一情報源）で確定した統一方針に追従するものであり、
MSP 独自の新たな設計トレードオフの導入ではないため、新規 IADR の起票は必須としない
（下記「IADR 起票の要否」参照）。

## 実装内容

`.github/dependabot.yml` の `updates:` に、`github-actions` ブロックの直後、コメントアウト済みの
言語別エコシステム例の前に、以下を追加する。

```yaml
  # NFR: submodule の pin 自動更新（gitlink を追跡先の先端へ前進させる更新 PR を生成する）。
  # 既定は自動マージしない（人手レビュー必須）。
  # planning（private の project-planning）を含む root .gitmodules の全 submodule が対象。
  # private submodule の更新には Dependabot が当該 private リポを read できる権限が要る
  # （詳細・マージ後の確認事項は docs/specs/20260712_issue-260_dependabot-gitsubmodule.md「private planning への Dependabot アクセス」節を参照）。
  - package-ecosystem: "gitsubmodule"
    directory: "/"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
```

`directory: "/"` は root の `.gitmodules`（`planning` と `src/ai-stock-trading` の 2 submodule）を
両方とも対象にする。ディレクトリの個別指定は不要（上記「変更点と理由」参照）。

auto-merge 設定は一切追加しない（fail-safe: pin 更新は必ず PR 経由・既定で自動マージしない）。
リポジトリ内に dependabot 向けの自動マージワークフロー（`.github/workflows/*auto-merge*` 等）は
存在しないことを確認済み（`ls .github/workflows` で該当なし）。

## Dependabot gitsubmodule と IADR-0065（CI の submodule 取得）の関係整理

両者は**別レイヤ**であり競合しない。

- **IADR-0065（CI 取得）**: `ci.yml` 等の GitHub Actions ワークフローが、ビルド/テスト実行のために
  `src/*` の public ユニット submodule を**非再帰**（`submodules: false` + 個別 `git submodule update
  --init` 等）で取得する経路。実行時（PR/push 毎）に**既存の pin**（gitlink）のコードを取得するだけで、
  pin 自体は変更しない。
- **Dependabot gitsubmodule（本設定）**: 週次スケジュールで**追跡先ブランチの最新コミット**と現在の
  gitlink pin を比較し、差分があれば **pin 自体を前進させる更新 PR** を生成する。CI 実行とは独立した
  別プロセス（Dependabot サービス）が担う。

両レイヤは疎結合: Dependabot が生成した pin 更新 PR は、その PR 自体の CI 実行時に IADR-0065 の取得経路
（非再帰 `src/*` init）を通ることになるため、pin 更新 PR が既存 CI で緑になることの実証にもなる
（issue #260 の受け入れ条件のひとつ）。

## IADR 起票の要否

本作業は、3 リポ横断で事前確定済みの正準スニペット（context-brief）をそのまま適用するものであり、
MSP 内でのローカルな設計トレードオフの選択（新たな技術選定・アーキテクチャ変更）は発生していない。
「planning を含める」という判断も、3 リポ統一方針への追従であって MSP 単独の設計判断ではない。
したがって IADR-0060/0065 の適用範囲内の運用整備と位置づけ、**新規 IADR は起票しない**。
（AST#109 でも同様の判断がなされている。）

## 検証

- YAML 構文: `npx --yes js-yaml .github/dependabot.yml`
- コミット規約: `GITHUB_BASE_REF=develop node scripts/check-commit-messages.js --verbose`
- doc-links: `node scripts/check-doc-links.js`
- PR 作成後、非レビュー系 CI（commit-messages / pr-title / doc-links / ci / security 等）の green化を確認する。
  claude-review の指摘対応は本作業のスコープ外（メインセッションが担当）。

## private planning への Dependabot アクセス（マージ後の確認事項）

`planning`（`https://github.com/endazon/project-planning.git`、private、同一オーナー endazon）への
Dependabot の read アクセス可否は、本設定 PR の作成時点では未検証（実際に Dependabot を有効化・実行して
いないため）。マージ後、以下を確認する必要がある。

- Dependabot が `planning` submodule の pin 更新 PR を実際に生成できるか（Insights / Dependabot ログで
  エラーが出ていないか）。
- アクセスできない場合、最小権限での許可設定（Dependabot に対象 private リポへの明示的アクセス許可）
  または個人アクセストークンの登録（IADR-0058 のトークン運用方針に整合させる）が必要になる。
- アクセスできなくても `src/ai-stock-trading`（public）側の pin 更新 PR 生成・本設定 PR 自体はブロックされない。

## 受け入れ基準（issue #260 スコープ / 変更点を反映）

- [x] `.github/dependabot.yml` に `package-ecosystem: gitsubmodule` を追加した。
- [x] （変更）`planning` を除外せず、root `.gitmodules` の全 submodule（`planning` + `src/ai-stock-trading`）
      を対象にした。理由は本仕様書「当初案からの変更点と理由」に記載、PR 本文にも明記する。
- [ ] 自動更新 PR が実際に生成され、追跡ブランチ先端へ pin を前進させ、既存 CI で緑になることの実証は
      本 PR のスコープ外（Dependabot の週次スケジュール実行後に確認。上記「マージ後の確認事項」参照）。
- [x] 自動マージ既定オフ（`dependabot.yml` に auto-merge 設定を書かない。人手レビュー必須）。
- [x] how-to（`docs/how-to/adding-a-unit-submodule.md` §6）を更新し、Renovate/Dependabot 併記の
      「有効化できる」という将来案の記述から、実際に Dependabot `gitsubmodule` で有効化済みである
      実運用の記述（対象範囲・自動マージなし・private submodule の権限要件）へ差し替えた。
