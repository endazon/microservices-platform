---
title: IADR-0116 全面再実装の進行方式 — 子 issue 単位のブランチ / PR と develop 直接統合
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0030, ADR-0031, ADR-0032, IADR-0034, IADR-0115, IADR-0118, IADR-0119, IADR-0139, IADR-0141]
author: Claude
created: 2026-08-02
updated: 2026-08-07
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
- 上流の起点: PR planning#144（2026-08-02 マージ）

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
   - > **［2026-08-04 追記・事実の更新］`ADR-0035` は 2026-08-04 に起案された**
     > （planning `d980a01` / planning#195。状態 `Proposed`）。したがって本規約の「未起案」という
     > **事実記述は古くなった**。ただし [IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 の
     > 着手条件は「`Accepted` になること（`Proposed` は満たさない）」であるため、
     > **保留そのものは継続する。** 規約 7 の効力は変わらない（保留の根拠が「未起案」から
     > 「`Accepted` 未達」へ移っただけである）。
   - > **［2026-08-07 追記・事実の更新。本規約 7 の保留は解けた / #586］`ADR-0035` は 2026-08-07 に
     > `Accepted` へ移った**（planning `3e58b97` = PR planning#244〔裁定依頼 planning#237〕。実測して確認）。本規約 7 が保留の
     > 根拠としていた事実（未起案 → `Accepted` 未達）は**もう存在しない**。したがって
     > **規約 7 が保留していた #448 / #450 の「RAG へのグラフ組み込み」部分は着手してよい。**
     > **本追記は決定内容を変えない**（規約 7 の条件は当初から前提 ADR の確定に連動しており、
     > その条件が満たされただけである。2026-08-04 追記と同型の事実更新である）。
     > **保留の全体像は [IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md) の 2026-08-07 追補を見ること**
     > ——同 IADR が規約 7 の適用範囲を FR-17〜21 へ広げており、**解除されたのは FR-17 / FR-18 に限る。
     > FR-19〜21 は別条件（Wiki.js の前提検証・要求そのものの確定）で保留が続く。**

> **［2026-08-03 追記］規約 6 の具体（#453 完了に伴うフォローアップの消化・#474）。**
> 規約 6 が予告した「退行防止テスト基盤（#453）のゲート」が PR #464 のマージで確定したため、各 PR の
> 受け入れ条件となる**コマンドとしきい値**を次のとおり具体化する。本追記は規約 6 の内容を変えるもの
> ではなく、予告部分を実値で埋めるものである。
>
> | ゲート | コマンド | しきい値 / 判定 |
> | --- | --- | --- |
> | 受け入れ基準 → テストの写像 | `node scripts/check-test-traceability.js` | `docs/tests/` に仕様書がある FR/SC にテストが 1 件も無ければ **fail**。`scripts/test-traceability-allowlist.json` にある未写像は warn、写像済みなのに allowlist へ残置は **fail**。加えて逆方向（#472）: 計画レンジにあって仕様書が無い ID は warn、うちテストが先行している ID は同 JSON の `specMissing` による ratchet で **fail**（判定の正は[テスト戦略](../tests/TEST_STRATEGY.md)のゲート一覧） |
> | バックエンド カバレッジ床 | `node scripts/check-coverage-floor.js`（`ci.yml` の `build-and-test`） | [`src/coverage-floor.json`](../../src/coverage-floor.json) の床 **`line 33` / `branch 17`** 未満は **fail**（[IADR-0118](IADR-0118_backend-coverage-floor.md)。ratchet のため引き上げ後は本表も追随させる。値の正は同 JSON。**`line` は #571 / [IADR-0138](IADR-0138_coverage-exclude-generated-code.md) で 34 → 33 へ置き直した——生成コードを集計から落とす測定基準の変更に伴うもので、退行ではない**） |
> | ライブラリ標準（ADR-0030） | `node scripts/check-backend-libraries.js` | `scripts/backend-library-baseline.json` の **ratchet**。不採用ライブラリの新規混入・baseline の減らし忘れは **fail**（#455） |
> | フロント カバレッジ ratchet | `npm run test:coverage`（`frontend-tests.yml`） | [`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` 未満は **fail**（[IADR-0034](IADR-0034_frontend-coverage-gate.md)） |
>
> ゲートの全体像・検査対象ユニットの切り分け（`ai-stock-trading` は対象外）・各ドメイン issue が守ることは
> [テスト戦略](../tests/TEST_STRATEGY.md)を参照する。`/verify` 通過と
> [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) の充足という規約 6 本文の条件は変わらない。

> **［2026-08-03 追記］規約 7 の適用範囲と規約 3 の明確化（#474）。**
>
> - **規約 7 の適用範囲は [IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md) で拡張した。** 規約 7 は
>   ADR-0035（GraphRAG 検索戦略）の未起案（**2026-08-04 に起案され `Proposed` となったが、
>   IADR-0119 決定 2 の着手条件である `Accepted` は未達のため保留は継続する**）を理由に
>   #448 / #450 の該当スコープを保留すると定めたが、計画側は
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

> **［2026-08-07 追記・規約 1 の限定例外（#575。改定は
> [IADR-0139](IADR-0139_domain-bundled-contract-prs.md)）］**
>
> **規約 1（子 issue 1 件 = ブランチ 1 本 = PR 1 本）に、条件つきの例外を 1 つ加えた。**
> 利用者裁定（2026-08-07・#572 の施策 1）により、**裁定済みの同型な契約追加は同一ドメイン単位で
> 1 PR に束ねてよい**。ただし例外が働くのは
> [IADR-0139](IADR-0139_domain-bundled-contract-prs.md) 決定 1 の **6 条件をすべて満たすとき**に限る。
> 条件を満たさないものは本規約 1 のままである。
>
> - **判定の単位は「ドメイン」ではなく「資源」である**（同 決定 1 条件 A）。同じ画面（SC）を
>   共有するだけでは足りず、**同じ API 資源（同じ読み書き口の系統）または同じ DTO 群**に閉じることを要する。
> - **束の上限は名目 3 件・実効 2 件**である。実測で 3 件束は 53 ファイルとなり、同 ADR が置いた
>   目安（概ね 50 ファイル / +2500 行）のファイル数側を超えるためである。
>
> - **本 IADR は `Superseded` にしない。** 原則（1 issue = 1 branch = 1 PR）は残り、限定例外が
>   1 つ増えるだけである。改定範囲を 1 点に限る先例は
>   [IADR-0117](IADR-0117_platform-shared-kernel-placement.md)（IADR-0056 決定 3 の部分改定）と
>   [IADR-0122](IADR-0122_contract-schema-source-and-compat-gate.md) 決定 4（IADR-0049 決定 1 の
>   部分繰延解除）。
> - **規約 4（PR ではなく issue を分割する）は変わらない。**
>   [IADR-0139](IADR-0139_domain-bundled-contract-prs.md) は逆向き（束ねる方向）にも同じ歯止めを当て、
>   実測（B 群 1 件 = 15〜25 ファイル / +636〜943 行）から**概ね 50 ファイル / +2500 行**を超えるなら
>   分けると定めた（上記の実効上限 2 件はこの目安の帰結である）。
> - **「1 issue に複数 PR」は依然として認めない。** 例外が認めるのは「1 PR に複数 issue」であり、
>   規約 4 の理由（issue のクローズ条件が PR から読めなくなる）はそのまま生きている。束ねた PR は
>   **`Closes #NNN` を issue ごとに 1 行**書いて閉じる
>   （[IADR-0139](IADR-0139_domain-bundled-contract-prs.md) 決定 3。**本リポジトリはスカッシュのみ**
>   （`allow_merge_commit` / `allow_rebase_merge` がいずれも無効）で、コミット境界は develop に残らない
>   ——「1 コミット = 1 issue」だけではトレーサビリティが担保できないことを実測で確かめた）。
> - **適用対象は #532〜#544 のうち未着手の 10 件**であり、実効は **2 束 ＋ 単独 6 件**である
>   （判定と根拠は [IADR-0139](IADR-0139_domain-bundled-contract-prs.md) 決定 5）。
> - **クロス監査は軽くしない**（同 決定 4）。減るのは PR 単位の固定費だけである。
>   **［2026-08-07 追記 / #594］この一文は「監査の *対象* の数」を指す。** 1 対象あたりの
>   **巡数**は [IADR-0141](IADR-0141_audit-rounds-and-population-drawing.md) が限定した（下記）。

> **［2026-08-07 追記・マージ前クロス監査の巡数と再走範囲（#594。改定は
> [IADR-0141](IADR-0141_audit-rounds-and-population-drawing.md)）］**
>
> **マージ前に `adr-guardian` ＋ `traceability-auditor` を必ず走らせる義務は変わらない。**
> 利用者裁定（2026-08-07・#572 の施策 3）により、**巡数と再走範囲だけ**を次のとおり限定した。
>
> - **全面巡回は 1 回まで**。2 巡目以降は**是正差分のみ**を見る
> - **2 本の監査は同時に走らせる**（互いの結果を待たせない）
> - **監査に打ち切り条件を明言させる**（「これ以上の巡回は不要」または要る範囲の限定）
> - **PR の性質で分岐する**（記録の追随のみ ＝ 全面 1 巡で打ち切り／規約改定・検査器の新設 ＝ 全面 1 巡 ＋ 差分 1 巡）
> - **母集合の引き直しは実装側の義務へ前倒しする**（同 決定 1。監査側の往復を減らすための「厚くする」側）
>
> **「監査を省略してよい」と読み替えないこと。** 裁定の根拠となった実測（PR #585 = 5 巡・#593 = 3 巡）
> では**空振りの巡が 1 度も無く**、削れるのは巡数であって検出の網ではない。
>
> - **本 IADR は `Superseded` にしない。** 走らせる義務そのものは変わらず、巡数と範囲が限定されるだけである。

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
- Superseded by: なし（[IADR-0139](IADR-0139_domain-bundled-contract-prs.md) が**規約 1 に限定例外を追加**するが、
  本 IADR は `Accepted` のまま。決定本体は変わらない）
- **Amended by: [IADR-0139](IADR-0139_domain-bundled-contract-prs.md)**（#575・2026-08-07。
  規約 1 に「同型の契約追加はドメイン単位で最大 3 件まで 1 PR に束ねてよい」という限定例外を足す。
  上記［2026-08-07 追記］が本 IADR 側の記載であり、条件・棄却案・検出しないことは同 IADR にある。
  **本 IADR は `Accepted` のまま**で、条件を満たさない issue には規約 1 がそのまま働く）
