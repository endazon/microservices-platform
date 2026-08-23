---
title: 作業仕様書 — #801 再オープン分（受け入れ基準 4）の機械的再確認と観測方法の申し送り
type: spec
status: done
related_ids:
  - NFR
  - FR-14
  - IADR-0033
  - IADR-0034
  - IADR-0056
  - IADR-0060
  - IADR-0209
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "./20260816_issue-801_frontend-tests-paths-templates.md"
  - "../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md"
---

# 作業仕様書: #801 再オープン分 — 受け入れ基準 4 の機械的再確認と観測方法の申し送り

## 起点となる計画書（トレーサビリティ）

先行仕様書 [`20260816_issue-801_frontend-tests-paths-templates.md`](./20260816_issue-801_frontend-tests-paths-templates.md)
と同一（`FR-14` / 無採番 `NFR` / `IADR-0033` `IADR-0034` `IADR-0056` `IADR-0060` `IADR-0209`）。
**本作業は新たな設計判断を伴わないため、新規 IADR は起こさない**（下記「対象外」参照）。

## 背景 — issue #801 の現況（PR #814 マージ後の再オープン）

PR #814（`Closes #801`）で受け入れ基準 1〜3 を実装し、issue は自動クローズされた。しかし
**波 7 末クロス監査**が受け入れ基準 4 未達のまま閉じていたことを指摘し、issue を再オープンした
（コメント 2026-08-16T12:57:49Z）。続く棚卸し再検証（コメント 2026-08-21T06:38:37Z）は
基準 1〜3 が引き続き満たされていることを実測で再確認し、**残るのは基準 4 だけ**と結論している。

> 基準 4:「雛形のテストが実際に走ることを、CI の実行結果で確認する（`frontend-tests` ジョブが
> `skipped` でないこと）」

**構造的な理由**（棚卸しコメントより）: 穴が塞がれてから今まで `templates/*/frontend` **だけ**を
変更する PR が存在しない。そのため「`frontend-tests` が起動し、かつその実行ログに雛形のテストが
現れる」という**単一の観測**がまだ成立していない。

## 本作業の目的・対象範囲

本セッションは **1 issue = 1 PR の原則を離れ、全 issue の作業を 1 本のブランチ・1 本の PR に
乗せる特殊運用**である。したがって「`templates/` だけを触る PR」を単独で作ることができず、
基準 4 が要求する**実 CI での観測**は本作業の中では成立させられない。

- 対象:
  1. **基準 1〜3 が現在も機械的に成立していることの再確認**（フレッシュな作業ツリーでの再実測。
     監査コメントの主張を鵜呑みにせず自分で確かめる）。
  2. **母集合の再確認** — 同型の「`paths:` 取りこぼし」が `frontend-tests.yml` / `frontend.yml`
     以外の場所（新設ワークフロー・新設 test include）に生じていないか。
  3. **基準 4 が未達である理由と、次にどう観測すべきかを明文化する**（統括側が issue へ申し送る
     ための一次資料として本仕様書を用いる）。
- 対象外:
  - **`.github/workflows/frontend-tests.yml` の `paths:` の変更**。PR #814 で足した
    `templates/*/frontend/**`（push / pull_request 両方）は既に存在し、是正の必要が無い
    （下記「検証の実測」で再確認済み）。
  - **`scripts/scripts.repo.test.js` の新規テスト追加**。既存の突合テスト（`NFR / #801: vitest の
    test.include が拾うパスは frontend-tests.yml の paths: にも載る`）が不変条件を汎用的に
    （`include` の全パターンを走査して）検査しており、穴が無い（下記で変異試験により再確認）。
  - **新規 IADR の起草**。設計は `IADR-0209` が正本のままであり、本作業は新しい決定を伴わない。
  - **`templates/` 配下の変更**。本 issue はテンプレートの内容ではなく CI 起動条件の話であり、
    テンプレート自体に変更は要らない。
  - **基準 4 の実観測そのもの**。理由は上記のとおり構造的に本作業の射程外であり、
    「何が未実施でどう観測すべきか」を申し送ることに留める（下記「未決事項」）。

## 母集合の再確認（`.claude/rules/traceability.md` 規則 1〜8 ／ `traceability.repo.md` 規則 9・10）

先行仕様書が 5 軸で母集合を引いた実測をそのまま踏襲しつつ、**着手時点で状態が動いていないか**を
軸ごとに再走査した（規則 10: 是正のたびに引き直す。今回は「是正」ではなく「再確認」だが、
同じ理由で監査コメントの主張を鵜呑みにせず引き直した）。

| 軸 | 走査コマンド | 結果（本作業時点） | 先行仕様書時点との差 |
| --- | --- | --- | --- |
| A | `grep -ln "paths:" .github/workflows/*.yml` | `changelog` / `ci` / `codeql` / `copilot-setup-steps` / `frontend-tests` / `frontend` / `openapi` の **7 本** | `ci.yml` に文字列としての `paths:` がヒットするが、**該当箇所はいずれもコメント**（`ci.yml:139,419,702`）であり、`on:` トリガには `paths:` フィルタが無い（`push`/`pull_request` は無条件起動。下記で確認）ことを確認した。先行仕様書の「6 本」から数え直すと `ci.yml` が加わって 7 本だが、**トリガとして効く `paths:` を持つのは変わらず 6 本** |
| B | `git ls-files \| grep -iE "vitest.*config\|playwright.*config"` | `src/vitest.config.ts` / `src/platform/frontend/playwright.config.ts` の **2 件**（変化なし） | 差分なし |
| C | `git grep -n -I -E "test:coverage\|pnpm run test\b\|vitest run" -- '.github/workflows'` | `frontend-tests.yml`（`pnpm run test:coverage` を実行）と `frontend.yml`（同語を**コメントで**言及するのみ・実行は無し）の 2 本 | 差分なし。**`vitest` を実走するワークフローは `frontend-tests.yml` の 1 本のみ**であることを再確認 |
| D | `grep -n "templates" .github/workflows/*.yml` | `ci.yml`（バックエンド雛形。issue #830 で別途対応済み）・`codeql.yml`（除外の言及のみ）・`copilot-setup-steps.yml` / `security.yml`（除外の言及のみ）・`frontend.yml` / `frontend-tests.yml`（対象の `paths:`） | **新設ワークフローは無い**。`ci.yml` の雛形対応（#830 = バックエンド）と本件（#801 = フロントエンド）は既に分離済みで重複しない |
| E | 母集合が動いていないかの確認（`is-shallow-repository`） | `git rev-parse --is-shallow-repository` → `true` | **shallow clone のため `git log` を「◯件」の根拠に使わない**（CLAUDE.md / planning#410）。本表はいずれも `git log` ではなく `grep` / `ls-files`（作業ツリーの実データ走査）に基づく |

**結論**: 母集合はフロントエンドの `vitest` 実行と `templates/` の交点であり、それを守るのは
`frontend-tests.yml` の `paths:` と `scripts/scripts.repo.test.js` の当該テストの 2 点のみ。
**新たに埋めるべき穴は見つからなかった。**

## 検証の実測

### 1. 現状の `paths:` が `templates/*/frontend/**` を覆っていることの確認（静的）

```console
$ grep -n "templates/\*/frontend" .github/workflows/frontend-tests.yml
42:      - "templates/*/frontend/**"
64:      - "templates/*/frontend/**"
```

**`push`（42 行目）と `pull_request`（64 行目）の両方に存在する。**

### 2. 突合テスト（PR #814 で新設）を単体で再実行する

`scripts/scripts.repo.test.js` は companion であり、素の `node scripts/scripts.repo.test.js` は
1 件も検査しない（#797 / IADR-0208 のガード）。正しい入口は `node scripts/scripts.test.js` だが、
**本セッションでは他 issue 担当エージェントが並行して別ファイル
（`.ai-context/adr/IADR-0247*`・`src/knowledge/backend/**`）を編集中**であり、フルスイート実行は
無関係な一時的赤（`check-doc-status-vocabulary` の据え置き超過など、他エージェントの作業中の
ドキュメント状態に起因）を拾う。**#801 の不変条件だけを隔離して検証する**ため、`ok()` を
「テスト名に `#801` を含むものだけ実行する」フィルタでラップし、同じ companion ファイルを
`require` して直接呼び出した（テスト本体のロジックには一切手を加えていない）。

```console
$ node -e "
const assert = require('assert');
const results = [];
function ok(name, fn) {
  if (!name.includes('#801')) return;
  try { fn(); results.push({name, pass:true}); }
  catch (e) { results.push({name, pass:false, err: e.message}); }
}
require('./scripts/scripts.repo.test.js')({ ok, assert });
for (const r of results) console.log(r.pass ? 'PASS' : 'FAIL', '-', r.name, r.err || '');
console.log('total:', results.length);
"
PASS - NFR / #801: vitest の test.include が拾うパスは frontend-tests.yml の paths: にも載る
total: 1
```

**現状のワークツリーで PASS。**

### 3. 変異試験（受け入れ基準 2 の再実測）

`frontend-tests.yml` の `push` / `pull_request` の**両方**から `templates/*/frontend/**` の 1 行を
機械的に除去し、同じ隔離実行で fail することを確認した。

```console
$ cp .github/workflows/frontend-tests.yml /tmp/.../frontend-tests.yml.bak
$ python3 -c "
s = open('.github/workflows/frontend-tests.yml').read()
s2 = s.replace('      - \"templates/*/frontend/**\"\n', '', 2)
assert s2 != s
open('.github/workflows/frontend-tests.yml', 'w').write(s2)
"
$ grep -c "templates/\*/frontend" .github/workflows/frontend-tests.yml
1   # 理由コメント中の言及 1 件のみが残り、paths: の実エントリは 0 件になった
$ node -e "...(同じ隔離実行)..."
FAIL - NFR / #801: vitest の test.include が拾うパスは frontend-tests.yml の paths: にも載る
  vitest が収集するのにテストを走らせる CI が起動しない（#801）。frontend-tests.yml の push / pull_request の**両方**の paths: へ足すこと:
    frontend-tests.yml: push.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）
    frontend-tests.yml: pull_request.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）
total: 1
```

**期待どおり fail した**（push / pull_request の両方が個別に検出されている）。直後に復元した。

```console
$ cp /tmp/.../frontend-tests.yml.bak .github/workflows/frontend-tests.yml
$ diff /tmp/.../frontend-tests.yml.bak .github/workflows/frontend-tests.yml && echo RESTORED_OK
RESTORED_OK
$ git status --short .github/workflows/frontend-tests.yml
(出力なし——差分ゼロ)
```

**バイト単位で復元されたことを確認した。**（先行仕様書の M1c と同一の変異・同一の結果であり、
時間が経っても不変条件が壊れていないことの再確認である。）

### 4. 受け入れ基準 3（`include` へ新パターンを足しても働くこと）の再確認

先行仕様書の M2 実測（`'../docs/**/*.{test,spec}.{ts,tsx}'` を `test.include` へ追加すると
同じ検査が fail する）をコードで再確認した。検査ロジック（`scripts/scripts.repo.test.js` 該当節）は
`test.include` の**全パターンをループで走査**しており、`templates` の 1 パターンをハードコードして
いない（`includes` 配列を for ループで回し、各 glob について `frontend-tests.yml` の全 `paths:` と
突合する実装であることをコードリーディングで確認した。行番号は変動するため本文には固定番号を
書かない —— `scripts/scripts.repo.test.js` 内 `NFR / #801` の節を参照)。**新規に変異を作って
再実測するまでもなく、実装の形自体がパターン非依存であることを読み取れる。**

## 受け入れ基準（#801 逐語）と充足状況

| # | 基準 | 判定 | 根拠 |
| --- | --- | --- | --- |
| 1 | `templates/*/frontend/**` だけを触る変更で `frontend-tests.yml` が起動する | ○ | 上記 検証 1（`paths:` 静的確認）。両トリガに存在 |
| 2 | 変異試験で実測する | ○ | 上記 検証 3（実際に変異を入れて fail を確認、復元も確認） |
| 3 | `include` に新パターンを足しても同じ検査が働く | ○ | 上記 検証 4（実装がパターン非依存であることをコードリーディングで確認。先行仕様書 M2 で実変異も実測済み） |
| 4 | 雛形のテストが実際に走ることを CI の実行結果で確認する（`frontend-tests` が `skipped` でない） | **未達（本作業の射程外）** | 下記「未決事項」 |

## 未決事項 — 基準 4 の観測方法（統括側への申し送り）

**足りないのは 1 本の観測であり、コードや設定の変更ではない。**

- **何が未実施か**: `templates/*/frontend/**` **だけ**を変更する差分が実際に GitHub 上の PR として
  作られ、その PR に対して `frontend-tests` ワークフローが `skipped` にならず起動したことを、
  Actions の実行結果（run の `conclusion` とジョブ一覧）で確認する、という観測がまだ行われていない。
- **なぜ本作業で行えないか**: 本セッションは「全 issue の作業を 1 本のブランチ・1 本の PR に載せる」
  運用であり、`templates/` 以外のファイル（他 issue の担当領域）も同じ PR に含まれる。したがって
  この PR 自体は「`templates/*/frontend/**` だけを触る PR」にならず、基準 4 が要求する条件を
  満たす母体になり得ない。
- **どう観測すべきか（具体手順）**:
  1. 本 PR のマージ後、`templates/unit-template/frontend` 配下**のみ**を変更する独立した差分
     （例: 既存テスト `SamplePage.test.tsx` へのコメント 1 行追加のような無害な変更、または
     次に雛形を更新する自然な作業）を単独 PR として作る。
  2. その PR の GitHub Actions 実行一覧で `Frontend Tests` ワークフロー（`frontend-tests.yml`）の
     run を開き、`test` ジョブの `conclusion` が `skipped` **ではない**こと（`success` か `failure` か
     を問わない —— 起動したかどうかが基準。#524 の先例どおり）を確認する。
  3. 可能なら、そのジョブの `Unit tests with coverage` ステップのログに
     `templates/unit-template/frontend/src/features/sample/components/SamplePage.test.tsx` が
     収集・実行されたことを示す行（vitest の `✓ ../templates/...test.tsx` 形式の出力）が
     現れることも合わせて確認する（棚卸しコメントが指摘した「カバレッジ表からは雛形のパスが
     読み取れない」問題を避けるため、**カバレッジサマリではなくテスト実行ログ本体**を見る）。
  4. 観測できたら、その run の URL・実行日時・ジョブ結論を issue #801 へコメントし、
     受け入れ基準 4 を `[x]` にしてクローズする。
- **観測が得られるまで、本 issue は「基準 4 のみ未達」の状態で open のまま残すべきである**
  （PR #814 が基準 4 を残したまま自動クローズし、再オープンに至った経緯を繰り返さないため）。

## 計画書との差異

- 差異: なし。CI の起動条件・検査の再確認のみで、機能・計画書に反する実装は無い。

## 変更したファイル

- なし（本仕様書の新規作成のみ）。`.github/workflows/frontend-tests.yml` は検証目的で一時的に
  変異させたが、バイト単位で原状復元済み（上記「検証の実測」3 参照。`git status` 上も差分なし）。
