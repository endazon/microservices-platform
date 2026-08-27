---
title: 作業仕様書 — 全 open issue（72 件）の一括対応 統括計画（波 0〜5）
type: spec
status: in-progress
related_ids: [NFR, IADR-0116, IADR-0139, IADR-0141, IADR-0179, IADR-0230, IADR-0279]
author: claude
created: 2026-08-27
updated: 2026-08-28
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 作業仕様書: 全 open issue の一括対応 —— 統括計画

> **本書は統括である。** 個別 issue の実装内容・母集合・受け入れ基準は**各作業仕様書が持つ**。
> 本書はそれらを代行しない（§6）。本書が持つのは**波の構成・並列判定の材料・対応しない issue の
> 判定記録**である。

## 1. 目的と裁定

### 1-1. 目的

**open issue 72 件を波 0〜5 に分けて一括対応する。** 変更単位の規約上の根拠は
[IADR-0279](../adr/IADR-0279_wave-stacked-prs.md)（**波単位の積み上げ PR** ——
[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1 の第 3 の限定例外）である。
**同 IADR 決定 1 の条件 W-A が求める「オーナーの明示許可」は下記 1-2 が正本**であり、
W-B（1 コミット = 1 論理変更・件名に起点 ID）・W-C（対応表と非クローズ理由）・W-D（波ごとに push し CI 緑）は
各波の PR で満たす。

### 1-2. オーナー裁定（2026-08-27）

**許可（W-A の根拠）**: リポジトリオーナーが**全 open issue の一括対応**と、その過程で
**複数 issue を 1 PR に束ねること**を明示的に指示した。**この許可は本セッションに限る**
（IADR-0279 決定 1 の W-A。過去・将来のセッションへは引き継がない）。

あわせて、次の 4 点の裁定を得た。

| # | 裁定 | 本計画への効き方 |
| --- | --- | --- |
| 1 | **直接クローズ可** | 対応済み・不要と判定した issue を、追加の裁定依頼を挟まずクローズしてよい。**判定の理由は PR 本文と本書 §7 に残す**（W-C） |
| 2 | **#451 は Obsidian プラグイン本体を除く** | #451 の射程はバックエンド側の同期契約・台帳・除外規則までとし、**Obsidian プラグイン本体の実装は含めない** |
| 3 | **最難関の作業には高能力モデルを充ててよい** | 難所（契約の破壊的変更を伴う辺の切替・認可の選言）にはより能力の高いモデルを配してよい。**配役の宣言は `ai-roster.json` の役割スロットに従う** |
| 4 | **#801 は検証用の別ブランチ PR 可** | #801（templates の CI paths）は、検証のために本流と別のブランチで PR を立ててよい |

### 1-3. アーキテクチャ裁定（案 C′）

**8 要素プロジェクトへの物理配置**を採る。適用の順序は次のとおり。

1. **土台先行** —— 配置の受け皿（プロジェクト構造・参照方向）を先に置く。
2. **新規は新様式** —— 本セッション以降に新規作成するものは、最初から新しい配置で書く。
3. **既存の移送は専用波** —— 既存コードの移動は他の作業と混ぜず、**波 4.5（アーキ移送）**に隔離する。

**混ぜない理由**: 移送は差分が「移動」に見えて実質は全面書き換えに見えるため、
**機能変更と同じ波に置くとレビューで両者を分離できない**。

**決定の記録の所在**: 本節は裁定の結論だけを持つ。採否の根拠（他案との比較・配置写像・
IADR-0027 / IADR-0218 との関係）は**波 1 で起票する実装 ADR に譲る**（統括と個別決定の主従）。
planning への環流は planning#490。

## 2. 環境の実測（2026-08-27・着手前ベースライン）

| 対象 | 実測 | 帰結 |
| --- | --- | --- |
| .NET SDK | **10.0.400 が `/root/.dotnet` に存在**（**PATH の外にあった**） | **バックエンドのローカルビルド・テストが可能**。PATH へ通してから使う |
| Node | 22 系あり | フロント・検査器ともローカル実行可 |
| pnpm | あり | `src/` の workspace 操作が可能 |
| helm / kubectl / kubeconform | **無し** | `check-deploy-manifests` は**ローカル実行不可** |
| Docker | **無し** | `check-stack-ready` は**ローカル実行不可** |

- **`scripts/check-*.js` は上記 2 件を除き HEAD で全て緑である**（着手前ベースライン）。
- **ローカル実行不可の 2 件は CI で担保する。** 「実行しなかった」を「緑だった」と書かない
  ——本書の実測欄にも、各波の PR 本文にも、**未実行として明示する**。

## 3. 宣言ファイル領域（並列判定用）

**並列作業は宣言済みファイル領域の非重複で機械的に判定し、交差する作業は直列化する**
（`CLAUDE.md`「実装作業の進め方」）。本セッションで用いる領域表は次のとおり。

| 記号 | 領域 |
| --- | --- |
| **T-RET** | `src/knowledge/backend/Services/RetrievalService/**` |
| **T-GRA** | `src/knowledge/backend/Services/GraphService/**` |
| **T-DOC** | `src/knowledge/backend/Services/DocumentService/**` |
| **T-WIK** | `src/knowledge/backend/Services/WikiService/**` |
| **T-AIA** | `src/knowledge/backend/Services/AiAnalysisService/**` |
| **T-DS** | `src/knowledge/backend/Services/DataSourceService/**` |
| **T-ING** | `src/knowledge/backend/Services/IngestionService/**` |
| **T-CNV** | `src/knowledge/backend/Services/ConversionService/**` |
| **T-NOT** | `src/platform/backend/Services/NotificationService/**` |
| **T-KBFF** | `src/knowledge/backend/Bff/**` |
| **T-PBFF** | `src/platform/backend/Bff/**` |
| **T-PSH** | `src/platform/backend/Shared/**` |
| **T-KCON** | `src/knowledge/backend/Shared/Knowledge.Contracts/**` |
| **T-INT** | `src/knowledge/backend/Tests/**` |
| **T-PFE** | `src/platform/frontend/**` |
| **T-KFE** | `src/knowledge/frontend/**` |

**判定規則**: 2 つの作業の領域が交わらなければ並列してよい。交わるなら**直列化する**。
**領域表に無いファイルを触る作業は、触る前に領域を宣言してから着手する。**

## 4. 共有ホットファイル（直列化点）

次のファイルは**複数の作業が同時に触ると必ず衝突する**。作業ごとに扱いを決めて直列化する。

| # | ファイル | 扱い |
| --- | --- | --- |
| **S1** | `docs/api/openapi.yaml` | **編集は各作業が行う。`codegen` は波末に 1 回だけ回す** |
| **S2** | orval 生成物 | **手で書かない**（S1 の codegen の出力） |
| **S3** | `scripts/contract-schema-baseline.json` | **`--update` は波末に 1 回だけ** |
| **S4** | `scripts/event-topology-baseline.json` | 同上（辺の切替と同じ波で動く） |
| **S5** | `deploy/helm/microservices-platform/files/pipeline.json` | **1 作業 1 行・直列**（同時編集しない） |
| **S6** | helm values ＋ `docker-compose` | 直列 |
| **S7** | `src/coverage-floor.json` | 直列（ratchet。引き上げは波末） |
| **S8** | `src/vitest.config.ts` の `thresholds` | 直列（同上） |
| **S9** | `scripts/xunit1051-baseline.json` | 直列 |
| **S10** | `scripts/test-spec-coverage-baseline.json` | **`--update` は PR の最終コミットで 1 回** |
| **S11** | platform routing 3 ファイル ＋ knowledge `features/index.ts` | 直列（合成点） |
| **S12** | `scripts/check-bff-downstreams.js` の `CALLERS` | 直列 |
| **S13** | knip / i18n / eslint-suppressions | 直列 |
| **S14** | 各サービスの `Program.cs` | サービス単位で直列（領域表の T-* と対応） |

## 5. 波の構成

| 波 | 内容 |
| --- | --- |
| **波 0** | **地ならし ＋ 帳簿** —— 変更単位の規約（IADR-0279）・本統括計画・作業仕様書の status 是正・文書の自己矛盾の解消 |
| **波 1** | **独立 5 トラック**（領域が交わらない 5 本を並列に進める） |
| **波 2** | **メッセージング辺の切替 E3a・E3b** ＋ #1016 / #911 / #912 / #970 / #989 段 3 |
| **波 3** | **platform フロント第 2 段** → #449 / #451abc / #600 / #447 / #992 / #1013 |
| **波 4** | #1012 → #882 → baseline 更新 → #448 を最終に置く |
| **波 4.5** | **アーキ移送**（§1-3 の 3。他の作業と混ぜない） |
| **波 5** | **帳簿・引き継ぎ**（対応表の確定・残件の申し送り） |

**波の順序は依存関係を表す。** 波の内部は §3 の領域非重複で並列してよいが、
**波をまたいだ並列はしない**（W-D: 波ごとに push し CI の緑を確認してから次の波を積む）。

## 6. 母集合の取り方

- **是正・追随の母集合は、キット規則 1〜8 ＋ 本リポジトリ固有の規則 9・10 に従って引く**
  （`.claude/rules/traceability.md` §是正・追随の母集合の取り方 と `traceability.repo.md` 同節）。
- **母集合は各作業仕様書が個別に引く。** 引いた結果と**除外したものとその理由**を、
  その作業の仕様書へ書く（規則 6）。
- **本書は統括であり、個別の母集合を代行しない。** 統括が一度引いた母集合を各作業が流用すると、
  **走査時点が作業ごとにずれたまま「引いた」ことになる**（規則 8 の同型）。

## 7. 対応しない issue の判定記録

**W-C が求める「閉じない issue の理由」の正本である。** 各波の PR 本文からは本節を参照する。

### 7-1. env-blocked（11 件）—— 実クラスタ・実配備・実運用値・稼働 DB が必須

**#271 / #336 / #380 / #388 / #457 / #458 / #466 / #780 / #781 / #782 / #1017**

いずれも「実際に動いている環境で測る／流す」ことが受け入れ基準に入っており、
**本セッションの環境（Docker・kubectl・helm が無い。§2）では検証まで到達できない。**
コードだけ書いて検証を伴わない着地は、**「統制を定めた」と「統制が働いている」の読み分けを壊す**
ため行わない。

### 7-2. AST 別リポ（3 件）

**AST#827 相当 / AST#858 相当 / AST#1015 相当（本リポの #827 / #858 / #1015）**

対象が submodule（`src/ai-stock-trading`）の中にある。**submodule 内は編集しない規約**であり、
本リポジトリの PR では閉じられない。**別リポジトリ側で扱う。**

### 7-3. 利用者作業（1 件）

**#854** —— `.claude/settings.json` は **AI から編集できない**（ハーネスの設定であり、
変更は利用者自身が行う）。**依頼内容を PR 本文へ残し、利用者へ引き渡す。**

### 7-4. 裁定待ち（5 件）

**#516 / #546 / #936 / #1011 / #1014**

実装中に判断が要る論点が残っており、
[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 条件 B /
[IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md) 条件 M-E と同じ理由で**束に入れない**。
**裁定依頼は小さく高頻度に計画リポへ流す**（`decision-needed` ラベル）。

### 7-5. 合計

**対応しない 20 件**（env-blocked 11 ＋ AST 3 ＋ 利用者作業 1 ＋ 裁定待ち 5）。
**残り 52 件が波 0〜5 の対象である。**

**数えた時点（規則 8）**: 母数 72 は **2026-08-27・波 0 のクローズ実施前**の open 総数の実測である。
52 のうち 18 件は波 0 の帳簿正常化（実装済みの手動クローズ）で消化済みのため、
**波 1 以降の残対象は 34 件**（クローズ後の open 総数は 54 = 34 ＋ 対応しない 20）。

## 8. 検証

- 各波の PR で `/verify` と `docs/DEFINITION_OF_DONE.md` の充足を確認する
  （[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6。本例外は規約 6 を免除しない）。
- **ローカル実行不可の検査器（`check-deploy-manifests` / `check-stack-ready`）は CI の結果を待つ。**
  **未実行を緑と書かない**（§2）。
- **監査の対象数は束ねても減らない**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)）。

## 9. 未決事項

- 波 1〜5 の各トラックへの issue の割り当ては、波の着手時に各作業仕様書で確定する
  （本書は波の構成までを固定する）。
- 波 4.5（アーキ移送）の移送対象の確定は、土台（§1-3 の 1）が着地してから引く。
