---
title: IADR-0116 全面再実装の進行方式 — 子 issue 単位のブランチ / PR と develop 直接統合
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0030, ADR-0031, ADR-0032, IADR-0034, IADR-0115, IADR-0118, IADR-0119]
author: Claude
created: 2026-08-02
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/INDEX.md"
---

# IADR-0116: 全面再実装の進行方式 — 子 issue 単位のブランチ / PR と develop 直接統合

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-02
- 決定者: 利用者裁定（#454「プルリクは issue 毎に行う」）＋ 実装（Claude）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（保守性・運用性）／再実装の対象は `FR-01..21` / `UC-01..11` /
  `SC-01..21` / `ADR-0001..0039` 全域（[ADR-0030](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md) /
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) /
  [ADR-0032](../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md) が土台）
- 関連する実装仕様書: [20260802_issue-454_reimplementation-kickoff.md](../specs/20260802_issue-454_reimplementation-kickoff.md)
- 関連 issue: #454（親トラッキング）と配下の 20 件（#438〜#453 ＝ 16 件 ＋ #455〜#458 ＝ 4 件）
- 上流の起点: project-planning PR #144（2026-08-02 マージ）

## コンテキストと課題

計画リポジトリの大幅更新（オープン issue 40 件の反映・ADR 11 件の起案・モックアップ全 24 画面の同期）を
受け、本リポジトリの実装をほぼ全面的に作り直す。作業量は**子 issue 20 件・4 フェーズ**にわたり、
数週間〜数ヶ月の期間、旧実装と新実装が同一リポジトリに同居する。

決めるべきは「この 20 件をどの単位でブランチ / PR に切り、どこへ統合するか」である。本リポジトリには
既に **develop を前提に動く自動化とゲート**があり、統合方式の選択がそれらの有効性を直接左右する。

- [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) のカバレッジ ratchet
  （[IADR-0034](IADR-0034_frontend-coverage-gate.md)。しきい値は develop 到達点を床とする）
- [`changelog.yml`](../../.github/workflows/changelog.yml)（`develop` / `main` への push で CHANGELOG 再生成）
- `check-action-versions.js` の `--compare-with-ref origin/develop`（[IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)）
- Dependabot（リポジトリ直下の `.github/workflows/` のみ走査）とブランチ保護の必須チェック

## 検討した選択肢

| | 案A: 子 issue 単位の PR を develop へ直接 | 案B: 長寿命の統合ブランチ（例 `reimpl/v2`）に積み、完了後に一括マージ | 案C: フェーズ単位で 1 PR |
| --- | --- | --- | --- |
| レビュー単位 | 小（issue 1 件） | 小（ただし最終マージが巨大） | 大（4〜6 issue 相当） |
| CI ゲートの実効性 | develop 基準のまま有効 | ratchet / 版数比較の基準が二重化し実質死ぬ | 有効 |
| 統合リスク | 継続的に解消 | 最終マージに集中（巨大コンフリクト） | 中 |
| フェーズ内並行 | 可 | 可 | **不可**（#454 の前提と衝突） |
| develop の状態 | 旧新が一時混在 | 常に旧のまま（新機能が届かない） | 旧新が段階的に入れ替わる |

## 決定

**案A を採る。** 具体的な規約は次のとおり。

1. **子 issue 1 件 = ブランチ 1 本 = PR 1 本**とし、`develop` へ直接マージする。親 issue #454 は
   トラッキング専用とし、#454 自体で実装 PR を出さない（本 PR＝準備作業のみ例外的に #454 に紐づく）。
2. **長寿命の統合ブランチを設けない**。フェーズは実施順序（依存関係）を表すだけで、ブランチを分けない。
3. **ブランチ名**は既存規約どおり `<種別>/<起点ID>-<概要のケバブケース>`。起点 ID は子 issue タイトルの
   スコープ `()` を左から見て **最初に現れる具体 ID**（`FR` / `UC` / `SC` / `ADR`）を採る。`NFR` は
   具体性を欠くため**併記の有無にかかわらず読み飛ばす**。具体 ID が 1 つも無い場合（スコープが `NFR`
   単独のとき）に限り `NFR` を使う。
   - 例: #455 `feat(NFR,ADR-0030)` → `feat/ADR-0030-backend-app-standard`（`NFR` を読み飛ばす）
   - 例: #442 `feat(NFR,ADR-0021/0023,ADR-0007/0008)` → `feat/ADR-0021-edge-runtime-cicd`
   - 例: #453 `test(NFR)` → `test/NFR-regression-test-foundation`（具体 ID が無いため `NFR`）
4. **1 PR が大きくなる場合は issue を分割する**（PR を分割して 1 issue に複数 PR をぶら下げない）。
   分割時は #454 のチェックリストへ新 issue を追加し、元 issue を親にする。
5. **既存実装は各 issue の範囲内で置換する**。リポジトリ規模の一括削除は行わず、旧実装の廃止・データの
   移行 / 破棄は **#457（切替計画）へ集約**する。#456（ABAC 属性組み合わせ数の実測）は旧データを
   必要とするため、**#456 完了前に旧データを破棄しない**。
6. **各 PR は `/verify` 通過と [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) の充足を条件**とする。
   退行防止テスト基盤（#453）の完了後は、そのゲートも各 PR の受け入れ条件に加わる。
7. **ADR-0035（GraphRAG 検索戦略）は未起案**であり、RAG へのグラフ組み込み部分には着手しない
   （#448 / #450 の該当スコープのみ保留し、他の部分は進める）。

> **［2026-08-03 追記］規約 6 の具体（#453 完了に伴うフォローアップの消化・#474）。**
> 規約 6 が予告した「退行防止テスト基盤（#453）のゲート」が PR #464 のマージで確定したため、各 PR の
> 受け入れ条件となる**コマンドとしきい値**を次のとおり具体化する。本追記は規約 6 の内容を変えるもの
> ではなく、予告部分を実値で埋めるものである。
>
> | ゲート | コマンド | しきい値 / 判定 |
> | --- | --- | --- |
> | 受け入れ基準 → テストの写像 | `node scripts/check-test-traceability.js` | `docs/tests/` に仕様書がある FR/SC にテストが 1 件も無ければ **fail**。`scripts/test-traceability-allowlist.json` にある未写像は warn、写像済みなのに allowlist へ残置は **fail** |
> | バックエンド カバレッジ床 | `node scripts/check-coverage-floor.js`（`ci.yml` の `build-and-test`） | [`src/coverage-floor.json`](../../src/coverage-floor.json) の床 **`line 34` / `branch 17`** 未満は **fail**（[IADR-0118](IADR-0118_backend-coverage-floor.md)。ratchet のため引き上げ後は本表も追随させる。値の正は同 JSON） |
> | ライブラリ標準（ADR-0030） | `node scripts/check-backend-libraries.js` | `scripts/backend-library-baseline.json` の **ratchet**。不採用ライブラリの新規混入・baseline の減らし忘れは **fail**（#455） |
> | フロント カバレッジ ratchet | `npm run test:coverage`（`frontend-tests.yml`） | [`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` 未満は **fail**（[IADR-0034](IADR-0034_frontend-coverage-gate.md)） |
>
> ゲートの全体像・検査対象ユニットの切り分け（`ai-stock-trading` は対象外）・各ドメイン issue が守ることは
> [テスト戦略](../tests/TEST_STRATEGY.md)を参照する。`/verify` 通過と
> [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) の充足という規約 6 本文の条件は変わらない。

> **［2026-08-03 追記］規約 7 の適用範囲と規約 3 の明確化（#474）。**
>
> - **規約 7 の適用範囲は [IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md) で拡張した。** 規約 7 は
>   ADR-0035（GraphRAG 検索戦略）の未起案を理由に #448 / #450 の該当スコープを保留すると定めたが、計画側は
>   **FR-17〜21 を起案段階の要求**としており、保留すべき範囲はより広い。**FR-17〜21 の着手保留とその着手条件
>   （前提 ADR-0033〜0037 の確定への連動）は IADR-0119 が決定する**（適用範囲を変える新しい決定であるため、
>   本 IADR への追記ではなく新 IADR で行った。先例は
>   [IADR-0117](IADR-0117_platform-shared-kernel-placement.md)）。規約 7 の本文と #448 / #450 に関する記述は
>   変わらない。
> - **規約 3 の「具体 ID」には `IADR-xxxx` を含む**（記述の明確化であり、規約 3 の決定内容を変えるもの
>   ではない）。規約 3 の列挙は `FR` / `UC` / `SC` / `ADR` だが、
>   [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) は起点 ID の種別に `IADR-xxxx`
>   （実装 ADR）を含めており、実運用でも起点が実装 ADR の子 issue は `IADR-xxxx` を具体 ID として採っている。
>   `NFR` を読み飛ばす扱いは従来どおりである。

## 理由

- **案B が壊すもの**が具体的である。カバレッジ ratchet は「develop 到達点を床とする」設計であり、
  統合ブランチ上では床が更新されないため、数ヶ月ぶんの退行を検出できない。`--compare-with-ref origin/develop`
  も同様に基準が古いまま固定される。Dependabot はリポジトリ直下の `.github/workflows/` しか走査しないため、
  統合ブランチ側の Actions 版数は自動追随しない。**「退行を発生させない」を最優先とする #454 の方針と、
  退行検出器を無効化する統合方式は両立しない。**
- **案C はレビュー可能性を損なう**。CLAUDE.md は「人間がレビューできる変更単位を維持する」を目的に掲げる。
  またフェーズ 1 は 6 issue あり、#454 が明示する「フェーズ内は並行可」を 1 PR 方式では実現できない。
- 案A の代償は「develop 上で旧実装と新実装が一時的に混在する」ことだが、これは**フェーズ 0 で
  退行防止テスト基盤（#453）を先行させる**という #454 の順序設計そのものが吸収する。混在期間中に
  壊れていないことを保証する仕組みを先に置く、という順序であるため、案A の弱点は既に手当てされている。
- 規約 4（PR ではなく issue を分割する）は、issue 単位の PR という不変条件を保つためである。1 issue に
  複数 PR を許すと、issue のクローズ条件が PR から読めなくなり、#454 のチェックリストが進捗を表さなくなる。

## 結果

- 良い影響:
  - develop 上の CI ゲート（ratchet・版数比較・security・CHANGELOG）が再実装期間を通じて有効なまま働く。
  - #454 のチェックリストが「マージ済み PR 数」とそのまま対応し、進捗が機械的に読める。
  - コンフリクト解消が各 PR に分散し、最終統合の一括リスクが消える。
- 悪い影響・トレードオフ:
  - develop が一時的に旧実装と新実装の混在状態になる。フェーズ 0 の #453 完了までは、退行検出が
    既存のゲート（`ci.yml` / `frontend*.yml`）の水準にとどまる。
  - 依存関係のある issue（例 #439 → #438、#450 → #456）で待ちが発生する。フェーズ順を守ることで対処する。
- フォローアップ:
  - ~~#453 完了時に、本 IADR の規約 6（受け入れゲート）へ具体的なコマンド / しきい値を追記する。~~
    → **消化済み（2026-08-03・#474）**。#453 は PR #464 でマージされ、規約 6 の具体（4 ゲートのコマンドと
    しきい値の表）を上記［2026-08-03 追記］として記載した。バックエンド床の決定そのものは
    [IADR-0118](IADR-0118_backend-coverage-floor.md) に起票した。
  - ADR-0035 起案（#456 の実測が前提）後に、保留したスコープを #448 / #450 で再開する。
  - #457 で旧実装の廃止を実施する際、本 IADR の規約 5 を根拠として破棄範囲を確定する。

## 関連

- Supersedes: なし
- Superseded by: なし
