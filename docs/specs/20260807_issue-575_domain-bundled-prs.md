---
title: 作業仕様書 同型の契約追加をドメイン単位で束ねられるよう IADR-0116 に限定例外を加える（#575）
type: spec
status: done
related_ids: [NFR, IADR-0116, IADR-0122, IADR-0130, IADR-0139]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../adr/IADR-0139_domain-bundled-contract-prs.md
  - ../adr/IADR-0116_reimplementation-branching-and-pr-policy.md
  - ../adr/IADR-0122_contract-schema-source-and-compat-gate.md
---

# 仕様書: 同型の契約追加をドメイン単位で束ねられるよう IADR-0116 に限定例外を加える（#575）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。保守性・進行方式——再実装期間中の変更単位とレビュー可能性）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR（実装）: [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（改定対象。
  規約 1「子 issue 1 件 = ブランチ 1 本 = PR 1 本」）／
  [IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2（契約の破壊的分類。
  束ねてよい範囲の外枠）／
  **本作業で起票した [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)**
- 計画書リンク:
  [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md)
- 関連 issue: [#575](https://github.com/endazon/microservices-platform/issues/575)（本作業）。
  起点は [#572](https://github.com/endazon/microservices-platform/issues/572)（issue 消化率の施策 1）と、
  そこに出た**利用者裁定 2026-08-07「ドメイン単位で束ねる（新 IADR を起票）」**。親は #454。

## 目的・背景

[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1 は
「1 issue = 1 branch = 1 PR」を定める。#572 は、裁定済みの契約追加（B 群）が
「DTO へ項目を足す ＋ OpenAPI ＋ contract baseline ＋ テスト ＋ 仕様書」という**ほぼ同一の形**を
しており、1 件ずつ PR にすると固定費が件数分そのまま掛かることを実測で示した。
利用者裁定はこれに対し「ドメイン単位で束ねる（新 IADR を起票）」を与えた。

本作業は**その例外を新 IADR として確定させる文書作業**である。**コードは一切変えない。**

## 対象範囲

- 対象:
  - `docs/adr/IADR-0139_domain-bundled-contract-prs.md`（新規。例外条件・棄却案・検出しないこと）
  - `docs/adr/IADR-0116_reimplementation-branching-and-pr-policy.md`（日付付き追記＋相互リンク。
    **`Superseded` にはしない**——原則は残り、例外が 1 つ増えるだけである）
  - `docs/adr/README.md`（索引への 1 行追加）
  - 本作業仕様書
- 対象外:
  - **issue 側の更新**（束の所属をラベル・本文へ記録すること）。#575 の受け入れ基準にあるが、
    本作業セッションからは GitHub への書き込みができないため**親が行う**。ADR / 仕様書側に表を残す。
  - `.github/PULL_REQUEST_TEMPLATE.md` への「クローズする issue」欄の追加（後述「未決事項」）。
  - B 群の実装そのもの（#532 ほか）。
  - `.github/workflows/` の変更（本リポジトリの制約により編集しない）。

## 採番

- `docs/adr/` の develop 時点の最大は **IADR-0138**。
- **`IADR-0137` は PR [#568](https://github.com/endazon/microservices-platform/pull/568)
  （`feat/FR-12-conversion-dead-letter`）がマージ待ちで予約している**（develop には未着。
  ブランチ上に `docs/adr/IADR-0137_conversion-dead-letter-marker.md` が実在することを確認した）。
  **0137 は使わない。**
- したがって本作業は **`IADR-0139`** を採る。欠番は作らない。
- 採番規約（[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「採番衝突時の改番手順」）は
  **先着尊重（マージ順）**である。本 PR より先に別の PR が 0139 を取った場合は改番し、
  ファイル名・本文・索引・関連仕様書・**PR タイトル**を追随させる。

## 設計（何を決める文書にするか）

新 IADR は次の 5 点を書き切る。詳細は [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) に置き、
本仕様書は「どこまで実測で裏を取ったか」を残す。

1. **例外を認める条件**（#575 の草案 5 項＋実測で足した 1 項）
2. **差し戻しの単位**（マージ前／マージ後で挙動が違う。実測で確定）
3. **サブ issue のクローズ主体**（`Closes` を複数書く。実測で確定）
4. **クロス監査の単位**（issue ごと ＋ 束ごと。**軽くしない**）
5. **実効表**（#572 コメントの表に本 ADR の条件を当てた判定）

## 実測（本作業で確かめたこと）

**「形を仮定して書くと素通りする」型（[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) /
[IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) が名指しした失敗）を自分で踏まないため、
本 ADR の主張はすべてコマンド・API で確かめた。** 以下は再現手順つきの一次情報である。

### (1) B 群 3 件の実物から「同型」と「固定費」を数えた

着手済み 3 件（#538 / #533 / #541）のブランチが手元にあるため、実物を数えた。

```console
$ git diff --name-only origin/develop...feat/SC-06-next-sync-at | wc -l          # #538
21
$ git diff --name-only origin/develop...feat/FR-12-conversion-dead-letter | wc -l # #533
25
$ git diff --name-only origin/develop...feat/FR-04-citation-confidentiality | wc -l # #541
15
```

| 観測点 | #538 | #533 | #541 |
| --- | ---: | ---: | ---: |
| 変更ファイル | 21 | 25 | 15 |
| 追加 / 削除行 | +834 / −39 | +943 / −33 | +636 / −57 |
| ブランチ上のコミット数 | 5 | 6 | 6 |
| 作業仕様書の行数 | 290 | 288 | 360 |
| AI レビュー実行（PR コメント） | 3 本（4m4s〜5m46s） | 3 本（5m3s〜6m34s） | 3 本（6m2s〜6m24s） |

**3 件が共通して触るファイルは 3 件**である（#572 本文は「2 ファイルだけ」と書いていたが、
**orval 生成物が抜けていた**）。

```
docs/api/openapi.yaml
scripts/contract-schema-baseline.json
src/platform/frontend/src/foundation/api/generated/bff.schemas.ts
```

2 件ずつの重なりには `docs/adr/README.md`（#538∩#533）と
`scripts/test-spec-coverage-baseline.json`（#538∩#541）も現れる。

束ねたときの重複削減は**ファイル数で 61 → 53（8 件）**である（3 件を 1 PR にした場合の union）。

### (2) CI 1 周の固定費

```
PR #567（#538）の head コミット ea62577 の check runs: 45 本
  内訳: success 41 / failure 4（#570 の継承した赤）
  実行時間の総和: 2505 秒 ≒ 41.8 分
```

### (3) 束ねた PR の「1 件だけ差し戻し」を実際にやってみた

**scratch のクローンで実験した**（作業ツリーは汚していない）。

- **異なる DTO を触る 2 コミット**（#533 と #541 の実装コミットを cherry-pick して束を作り、
  先頭の #533 を `git revert`）: **衝突 0・exit 0**。共有 3 ファイルはすべて auto-merge され、
  revert 後に `node scripts/check-contract-schema.js` が **exit 0** で通った。
- **同一 DTO を触る 2 コミット**（`DataSourceDto` へ疑似 A → 疑似 B の順で項目を足し、A を revert）:
  **`CONFLICT (content)` で exit 1**。baseline JSON は auto-merge されるがソースは衝突する。
- 衝突解決を誤った場合（**両方向とも機械が止める**）:
  - 過剰 revert（B の項目まで消す）→ `check-contract-schema.js` が
    `[破壊的] メンバーの削除` 2 件で **exit 1**
  - 過小 revert（A の項目が残る）→ 同スクリプトが
    `[非破壊] メンバーの追加` 1 件の baseline 差分で **exit 1**

### (4) `Closes` を複数書いたときの GitHub の挙動（推測しない）

本リポジトリの全 PR 本文を走査し、クロージングキーワードを持つ **96 件**を洗い出したうえで、
**複数書いた実例**を突き合わせた。

```
PR #422（merged 2026-07-30T16:18:23Z）の本文:
  Closes #420
  Closes #421
→ issue #420: closed_at 2026-07-30T16:18:25Z / state_reason=completed
→ issue #421: closed_at 2026-07-30T16:18:25Z / state_reason=completed
```

**マージの 2 秒後に両方が閉じている。** キーワードは**番号ごとに前置する**（1 つのキーワードで
複数番号を並べる形の実例は本リポジトリに無い）。

逆向きの実測もある。**PR #567（#538 の実装）は本文に「起点 issue は **#538**」としか書かず、
クロージングキーワードを 1 件も含まない。マージ済みだが #538 は本作業時点で `open` のままである。**
`.github/PULL_REQUEST_TEMPLATE.md` に issue リンクの欄は無く、
`scripts/check-commit-messages.js` も issue 番号を検査しない（件名の書式と ADR/IADR の実在のみ）。
**書き忘れても何も落ちない。**

### (5) マージ方式（束ねる判断に直接効く）

```
allow_squash_merge = True / allow_merge_commit = False / allow_rebase_merge = False
squash_merge_commit_title = PR_TITLE / delete_branch_on_merge = True
```

**スカッシュのみ**である。実測: PR #567 は 5 コミットだが develop には `ebfd410` の 1 コミットとして載り、
ブランチは削除されている（`git ls-remote --heads origin feat/SC-06-next-sync-at` が 0 件）。
一方 **`refs/pull/567/head` は生きており**（`ea62577f…` を取得できる）、
issue 単位のコミットは GitHub 上に残る。

### (6) 適用対象の実効表を issue の実状で裏取りした

GitHub API で 14 件の状態・ラベル・本文・コメントを読んだ。#572 コメントの表と食い違う点が
**3 つ**見つかった。詳細と根拠は [IADR-0139 決定 5](../adr/IADR-0139_domain-bundled-contract-prs.md) に置く。

- **#546 は B 群ではない**。B 群 13 件は **#532〜#544 の連番 13**で尽きる。#546 だけラベルが
  `infrastructure`（#532〜#544 の 13 件はすべて `ai-implement` / `implementation`。#545 は
  `documentation` / `question` ＝ #572 が D 群の例に挙げた件、#531 は同種だが PR #551 でマージ済み）で、
  本文は「**本件は 2026-08-05 の裁定の対象外です**」「Alertmanager の配備時期をお知らせください」＝
  **利用者への質問**である。条件「裁定が済んでいる」を満たさない。
  #572 の「残り 10 件」も #546 を**含めずに**ちょうど 10 になる（13 − 着手済み 3）。
- **#536 は「契約の追加のみ」ではない**。本文が
  「**取り込み側（IngestionService）の変更を伴う**」「**既存文書の再索引**が要る」
  「そのため #1・#2（契約の追加のみ）と**同じフェーズに束ねないこと**」と明記する
  （文中の裸番号は planning 側の連番であり本リポジトリの issue 番号ではない。#532 のコメントが
  同じ注意を残している）。
- **ABAC / 分析の 4 件は同一ドメインに閉じない**。触る資源が違う——#539 は `AnalysisRequest` への
  項目追加、#535 は SC-09 のポリシー検証口、#540 は属性値の照会口（いずれも新設）、#542 はタグ辞書で
  「値集合・使用件数・改名追随の 3 つで**分割できない**」。
  ただし **#540 と #542 は「読み取り口を 1 系統にする」裁定で結合している**
  （#540 本文「口が分かれると制限の掛け忘れが起きる」／#542 本文「読み取り口は権限内候補 API と
  1 系統にする」）。

**#538 / #533 / #541 の 3 件が単独で着地することは裏取りできた**（#538 は PR #567 でマージ済み、
#533 は PR #568・#541 は PR #569 がオープン。いずれもマージ前監査の指摘を反映済み）。

## 受け入れ基準（#575 の受け入れ基準に対応）

- [ ] 新 IADR（`IADR-0139`）が `docs/adr/` に存在し、**例外条件・棄却案・検出しないこと**を書いている
- [ ] IADR-0116 に **Amended の相互リンク**がある（`Superseded` にしない）
- [ ] `docs/adr/README.md` の索引に載っている
- [ ] 4 束（の判定結果）が **ADR / 仕様書側の表**として残っている（issue 側の更新は親が行う）
- [ ] `node scripts/check-doc-links.js` が exit 0

## テスト方針

文書のみの変更のため自動テストは追加しない。機械検査は次の 2 つで代替する。

| 検査 | コマンド | 期待 |
| --- | --- | --- |
| 相対リンクの実在 | `node scripts/check-doc-links.js` | exit 0 |
| コミット件名の書式・IADR の実在 | `node scripts/check-commit-messages.js --title "<件名>"` | exit 0 |

## 計画書との差異

なし（本作業は実装リポジトリ内の進行方式に閉じる。計画 ADR には触れない）。

## issue #575 の草案との差異

| # | 草案 | 本作業の結論 | 理由 |
| --- | --- | --- | --- |
| 1 | 例外条件は 5 項 | **6 項**（「契約の追加に閉じる」を追加） | #536 が取り込み側変更＋再索引を伴い、issue 本文自身が束ねるなと書いている |
| 2 | 実効は「4 束 ＋ 単独 1 件」 | **2 束 ＋ 単独 6 件 ＋ 対象外 1 件** | 上記「実測 (6)」の 3 点。**この差分は利用者確認事項**（下記「未決事項」） |
| 3 | クローズ主体は「`Closes` 複数 or 親のチェックリスト」 | **`Closes` を issue ごとに 1 行（必須）＋ 親 #454 のチェックリストは併用**（クローズ主体ではない） | PR #422 の実測で複数 `Closes` が機能することを確認。#567 の実測で「起点 issue は」表記では閉じないことを確認 |

## 未決事項

1. **実効表の差分（上表 #2）は利用者裁定の範囲に触れ得る。** 裁定が与えたのは「束ねてよい」という
   許可であり、**どの issue が条件を満たすか**の判定は実装側の作業である。ただし #572 コメントの表を
   そのまま使わない以上、**親が利用者へ確認するのが安全**である。ADR には根拠を全部書いた。
2. **`.github/PULL_REQUEST_TEMPLATE.md` に「クローズする issue」欄が無い。** 束ねる運用では
   `Closes` の書き忘れが直接「issue が閉じない」に直結する（#538 で実際に起きた）。
   欄の追加は #575 の受け入れ基準に無いため本作業では行わず、**フォローアップ issue の候補**とする。
3. **束の条件（同一ドメイン・裁定済み・非破壊・着手済みなし・1 コミット = 1 issue）に機械検査は無い。**
   本 ADR は規約であって検査器ではない。検査を作るなら別 issue（ADR の「検出しないこと」に明記した）。
4. **#537 の射程**（DTO への健全性項目の追加と「継続失敗のアラート」発報がどこまで同一 PR か）は
   着手前に確定させる必要がある。確定できなければ条件 B（実装中に判断が要らない）を満たさない。
