---
title: 作業仕様書 — src/README.md の経過措置レイアウト図を撤去する（IADR-0282 / ADR-0065 で既に古い）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - IADR-0027
  - IADR-0280
  - IADR-0282
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
related_specs:
  - "20260828_wave45-vsa-migration.md"
  - "20260830_issue-1061_remove-worker-layer.md"
issue: ""
---

# 作業仕様書 — `src/README.md` の経過措置レイアウト図を撤去する

## 何が古いか

`src/README.md` §サービスユニットの標準レイアウト（backend 内）の前段が、
**`IADR-0282`（8 要素プロジェクトの撤回・単一プロジェクト＋VSA/DDD フォルダ規範）と
計画 `ADR-0065` の時点で既に古い。**

同節は冒頭で「**下図は現行実態（移送波までの経過措置）である**」と宣言しているが、
**図が描いている構造は 1 つも実在しない。**

さらに **同じ節の後段が「移送は完了した（2026-08-28）」「14 サービス全件が新配置へ移送済み」と書いており、
節が自分自身と矛盾している。**

## 実測（`develop` ＋ #1076 着地後の状態）

| 図が描いている構造 | 実在数 |
| --- | ---: |
| `Services/*/src/` の中間層（**追跡下**） | **0**（未追跡の `obj` 残骸ディレクトリが 11 あるが、追跡ファイルは 0） |
| `Services/*/tests/` の中間層 | **0** |
| `*.Application` / `*.Domain` / `*.Contracts` / `*.SharedKernel` の `.csproj` | **0** |
| `Services/` 配下の `Composable/` | **0** |
| `Services/` 配下の `Foundation/` | **0** |
| `.Api.Tests.csproj` / `.Worker.Tests.csproj` | **0**（#1076 着地後。develop 時点では 2） |

🔴 **ただし `Foundation/` は消えていない。** `Shared/` 配下に **48 ファイル**在る
（`Platform.Shared.Infrastructure` 28 ／ `Platform.Shared.Infrastructure.Tests` 20）。
**「`Foundation/` は無くなった」と書いてはならない。** 無くなったのは
**サービスの層プロジェクト内の第 1 階層フォルダとしての区分**である。

## 母集合の引き方（規則 9: 誤りの側の文字列で全走査してから挙げる）

`.Api.` / `.Worker.` / `Composable/` / `Foundation/` / `src/<ServiceName>.` / `tests/<ServiceName>.` /
`<ServiceName>.Application` / `<ServiceName>.SharedKernel` の 8 語で追跡下を走査した。
基準は **#1076 着地後の状態**（`origin/refactor/NFR-1061-remove-worker-layer`）——
develop 基準で数えると `.Worker.` が 100 件出るが、その大半は #1076 が消す分である。

### 本作業の対象（1 件）

- **`src/README.md`** —— 経過措置の樹形図と、それに付随する 2 項

### 🔴 同型に古いが、本作業では対象外にしたもの（理由つき）

**いずれも「本変更で新たに誤りになった」のではなく、`IADR-0282` の時点で既に古い。**
規則 10 が求めるのは「この変更で新たに誤りになる自分の記述の引き直し」であり、
**先行して存在する同型の誤りは規則 10 の対象ではない。** 別 issue へ送る。

| ファイル | 古い記述 | 件数 |
| --- | --- | ---: |
| `docs/tech/tech-requirements.md` | `Worker/<Name>.Worker.csproj` の樹形図 ＋「実装の現況は `<Name>.Api.Tests` / `<Name>.Worker.Tests`」 | 1 |
| `docs/tests/TEST_STRATEGY.md` | 同じ「実装の現況は…」 | 1 |
| `docs/tests/FR-*.md` / `SC-*.md` / `UC-*.md` | テストプロジェクト名を `<Service>.Api.Tests` と書いている（実際は `<Service>.Tests`） | 13 |
| `docs/how-to/adding-a-unit-submodule.md` | 同上 | 1 |
| `.github/workflows/ci.yml:758` | コメントが `SampleService.Api.csproj` を指すが、雛形の実体は `SampleService.csproj` | 1 |
| `docs/tech/composability-classification.md` / `composable-component-guide.md` / `docs/functional/FR-14_composability.md` | 「**各プロジェクト内**の配置は `Foundation/`（固定）/ `Composable/`（可変）」—— **Shared では今も真だが Services では偽**。一般化が過剰 | 3 |
| `templates/unit-template/README.md` / `deploy/helm/microservices-platform/files/README.md` | `Composable/` への言及 | 2 |

**合計 22 ファイル。** 本 PR では触らず、フォローアップ issue を起こす。

### 恒久的に対象外

| 対象 | 理由 |
| --- | --- |
| `.ai-context/adr` / `specs` / `superpowers` | **凍結記録。本文プロズを後から書き換えない**（CLAUDE.md） |
| `CHANGELOG.md` | 自動生成物。手で書き足さない |
| `src/ai-stock-trading/**` | submodule。本リポジトリからは触らない |
| 🔴 **`docs/how-to/session-handoff.md`** | **`.Api.` / `.Worker.Composable` は「母集合の引き方を誤った事例」の題材そのもの**である（「`git grep -l '\.Api\.'` は `.Worker.Composable` を拾わない」）。**書き換えると教訓が消える。** 古い名前が出てくること自体が正しい |

## 規則 10 —— この変更で新たに誤りになる自分の記述

**無い。** 本作業は記述を削るだけで、新しい主張を足さない。
削った先を参照している箇所が無いことを確認する（`src/README.md` の当該行を指す
アンカーリンクや「上図」参照が他ファイルに無いこと）。

## やること

1. 経過措置の樹形図（`src/<ServiceName>.<Api|Worker>/` 以下）を**撤去する**
2. 付随する 2 項を撤去する
   - 「名前空間はフォルダ階層に一致させる（例: `IngestionService.Worker.Composable.Steps`…）」
   - 「固定/可変の区分（`Foundation/` / `Composable/`）は層プロジェクト内の第 1 階層フォルダとして温存する」
3. 「存在しない区分のフォルダは作らない」の項は、**層プロジェクトの内側**を前提にしているので、
   前提が消えたことに合わせて書き直す（規則そのものは Shared に対して生きている）
4. 冒頭の「下図は現行実態（移送波までの経過措置）である」を落とし、
   **後段の「サービス直下の標準構成」が唯一の正**であることを明示する
5. 移送の history は**既に同節の後段が持っている**（「移送は完了した（2026-08-28）」＋
   凍結記録 `.ai-context/specs/20260828_wave45-vsa-migration.md` へのリンク）。**そこへ畳む**

## 採った形 —— 「撤去」ではなく「歴史的経緯として畳む」

issue の指示は「撤去するか、明確に『歴史的経緯』として畳む」だった。**畳む方を採った。**

**理由**: 樹形図を跡形もなく消すと、「なぜこの節に 8 要素の話が無いのか」「以前あった記述はどこへ行ったのか」を
次に読む人が辿れない。**`IADR-0282` と `ADR-0065` で古くなったという事実自体が、残す価値のある記録**である。

**帰結として `src/<ServiceName>.` 等の文字列は引用として本文に残る。** これは意図的であり、
**「かつて置かれていた」と明示した引用符の中**にしか現れない。規範として述べている箇所は無い。

## 受け入れ基準

- [x] 経過措置の樹形図が**規範としては**存在しない（引用としてのみ残る）
- [x] 「`Foundation/` / `Composable/` を**層プロジェクト内**に温存する」を規範として述べていない
- [x] 🔴 **`Foundation/` が `Shared/` では現役であることを否定していない**（48 ファイルの実測を明記）
- [x] 節が自己矛盾していない（「現行実態」の宣言を落とし、「現行の標準は次節ただ 1 つ」と述べた）
- [x] 撤去した図を指す参照が他に無い（`上図` の全走査で 0 件）
- [x] `node scripts/check-doc-links.js`（1002 件）/ `check-trace-blocks.js`（158 件）/ `gen-knowledge-graph.js --check` が緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## 順序の制約

🔴 **#1076（issue #1061）と同じファイル（`src/README.md`）を触る。**
CLAUDE.md の「並列は宣言済みファイル領域の非重複で判定し、交差する issue は直列化する」に従い、
**#1076 が着地してから着手する。**
