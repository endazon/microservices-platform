---
title: 作業仕様書 — 計画 ADR-0128 の見直し 3 条件を棚卸しで機械的に確かめるかを決める（#1747）
type: spec
status: done
related_ids:
  - NFR
  - FR-11
  - ADR-0128
  - ADR-0010
  - IADR-0499
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0128_llm-gateway-keep-own-no-oss-with-review-conditions.md 決定 2・決定 3 (Accepted 2026-10-05)
related_specs:
  - 20261005_1748_plan-range-adr-0128
issue: "#1747"
---

# 作業仕様書 — 計画 ADR-0128 の見直し 3 条件を棚卸しで確かめるか（#1747）

## 目的と射程

計画 ADR-0128（planning#720 の裁定。planning#721）は、LLM ゲートウェイを自前で維持し、次の 3 条件のいずれかが成立したら OSS ゲートウェイとの併用を再評価すると定めた。

1. 有効な生成プロバイダが 4 つ以上になる、または Claude 以外を常用するようになる。
2. 運用者向けの UI が計画に入る。
3. ゲートウェイの共有範囲が変わる。

同 ADR 決定 3 は、条件の成立を確かめる現在の実現手段を「無い」とし、暫定手段（計画側が新しい送信先・UI・共有範囲を足すときに照らす）を置いたうえで、**実装側の棚卸し（`backlog-audit.yml`）で確かめるかは実装が決めて IADR に残す**とした（planning#720 の close 条件 4）。本作業はその判断を下し、IADR-0499 に残す。

**射程**: 判断（IADR-0499）と、判断から導かれる最小の手当て（実装側の変更点＝生成エンドポイント定義の機能仕様書への注記）。**射程外**: 条件 2・3 の計画側の運用（計画リポの持ち物）、SDK のパッケージ名の食い違い（planning#720 補足 2。別件）。

## 実測（`origin/develop` `c63351de`）

```console
$ grep -c "\.\.\.section(" scripts/backlog-audit.js
6
$ node -e "…Llm.Routing.Endpoints…"
claude-managed claude B Enabled=true
copilot-managed copilot C Enabled=false
selfhosted-oss selfhosted A Enabled=false
$ git grep -n "Llm__Routing" -- deploy
(0 件)
$ git ls-files 'src/platform/backend/Services/LlmGateway/*Provider.cs'   # 生成のアダプタ
…/ClaudeProvider.cs  …/CopilotProvider.cs  …/SelfHostedProvider.cs
```

- 棚卸しの節は全部「止まっているもの」の列挙である（Proposed の IADR・動いていない作業仕様書 / 文書・写像後回しのテスト・blocked issue・ci-failure issue・古い PR）。**設定の状態を条件と照らす節は無い。**
- 生成の有効エンドポイントは 1（Claude）。deploy 配下に生成エンドポイントの `Enabled` を上書きする注入は無い（上書きの前例は埋め込みだけ）。
- 生成のアダプタは 3 種類。条件 1 の「4 つ目」は、既存に無い種類のアダプタ（`ILlmProvider` の新しい実装）を足す PR か、`Llm:Routing:Endpoints` の追加・有効化の PR でしか起きない。

## 判断（IADR-0499）

**棚卸しでは機械的に確かめない（報告だけの行も足さない）。** 代わりの手当ては、実装側の変更点（生成エンドポイント定義の機能仕様書）に「足す・有効にするときは計画の見直し条件と照らす」を書くことと、計画側の暫定手段（ADR-0128 決定 3）である。理由と、機械化へ倒す条件は IADR-0499。

## 母集合（規則 9・10）

- 誤りの側の文字列で走査: `git grep -n -E "ADR-0128|見直し条件|OSS ゲートウェイ|LiteLLM"` → 本作業前は `traceability.repo.md` のレンジ宣言と #1748 の作業仕様書・別紙だけ（ADR-0128 を引く live な文書は無い）。
- 生成エンドポイントの定義を説明する live な文書: `git grep -l "copilot-managed" -- docs` → `docs/functional/FR-11_llm-egress-routing.md` の 1 件。ここへ注記する。`docs/tech/composability-classification.md` は差し替え点の分類表であり、手順を書く場所ではないため除外。
- 本変更で新たに誤りになる自分の記述: 無い（コード・ワークフローを変えない）。

## 受け入れ基準

- [x] 3 条件それぞれについて、機械で確かめるか否かと理由を IADR-0499 に残す。
- [x] 確かめない場合の代わりの手当てを IADR-0499 に記録し、実装側の変更点の文書へ反映する（表示テキストに計画 ID を書かない）。
- [x] 機械化へ倒す条件（同型事故 2 回）を書く。
- [x] 索引（`.ai-context/adr/README.md`）へ IADR-0499 を足す。

## 検証

`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-trace-blocks`・`check-doc-updated --base origin/develop`・`check-commit-messages --range origin/develop..HEAD`・`gen-knowledge-graph --check`・`check-adr-numbering`・`check-reading-budget`・gitleaks。ワークフローは変えないので actionlint は対象外。
