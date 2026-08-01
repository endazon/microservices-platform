---
title: IADR-0115 impl-handoff-kit を足場の単一情報源とし、固有デルタを 4 種に限定する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0056, IADR-0058]
author: Claude
created: 2026-08-01
updated: 2026-08-01
plan_refs: []
---

# IADR-0115: impl-handoff-kit を足場の単一情報源とし、固有デルタを 4 種に限定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-01
- 決定者: 実装担当

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（保守性・運用性）。キット本体は
  `planning/tools/impl-handoff-kit/`（計画リポジトリの成果物）
- 関連する実装仕様書: [作業仕様書](../specs/20260801_impl-handoff-kit-sync.md)

## コンテキストと課題

本リポジトリの足場（`.claude/` / `.github/` / `scripts/` / `docs/` の雛形）は、計画リポジトリの
`tools/impl-handoff-kit/repo-template` から生成された。生成後は両者が独立に進化するため、放置すると
**双方向に乖離**する。実際、今回の棚卸しで次の 2 方向の乖離が同時に見つかった。

- **キットが進んでいた例**: `claude_args` の `--allowedTools` を 1 ツール 1 行で書くと、値が空白で
  argv へ分割され指定が**すべて無効**になる。キットは planning#95 でこれを是正済みだったが、
  本リポジトリは壊れた記法のまま運用しており、CI の AI 実装・レビューがビルド/テストを実行できない
  状態が続いていた。
- **本リポジトリが進んでいた例**: `check-doc-links.js` の submodule 未 populate 判定を
  `.gitmodules` 由来に一般化した改善（#283）がキットへ環流されていない。

乖離の検出も是正も人手に依存しており、「どちらを正とするか」「何を固有として残してよいか」の
基準が無いことが根本原因である。基準が無いと、同期のたびに 1 ファイルずつ判断が要り、結局
同期されない。

## 検討した選択肢

| | A. キットを正とし固有デルタを限定列挙（採用） | B. 本リポジトリを正とし、キットへ随時環流 | C. 現状維持（都度の目視差分） |
| --- | --- | --- | --- |
| 同期コスト | 低（分類 A は `diff` でバイト一致を機械判定できる） | 高（キット側の改善を毎回取りに行けない） | 高 |
| キット改善の享受 | 自動的に得られる | 得られない | 運任せ |
| 固有事情の表現 | 4 種に限定して明示 | 自由（ただし際限なく増える） | 自由 |
| 環流の動機 | 強い（固有デルタを増やせないため、汎用改善はキットへ出すしかない） | 弱い | 無し |

## 決定

**`impl-handoff-kit/repo-template` を本リポジトリの足場の単一情報源とする。**

1. `repo-template` の各ファイルを 3 分類し、分類ごとに扱いを固定する。
   - **A. キット完全一致**: キットの内容で上書きし、`diff` でバイト一致を保つ。
   - **B. キット＋固有デルタ**: キット本文を土台に、固有部分のみを再適用する。
   - **C. 本リポの中身そのもの**: 雛形から書き起こした実体（ADR 索引・運用/セキュリティ仕様書等）。同期しない。
2. **分類 B で許容する固有デルタは次の 4 種のみ**とする。これ以外の独自記述は同期時に削除する。
   1. リポジトリ構成（ユニット第一構成 `src/*/{backend,frontend}`・submodule 取得ステップ。[[IADR-0056]] / [[IADR-0060]]）
   2. 技術スタック（.NET 10 / React + Vite / npm workspaces）とその CI 配線
   3. 本リポにしか存在しない成果物・スクリプト（`images.yml` / `check-unit-dependencies.js` 等）
   4. Dependabot が更新した **GitHub Actions のバージョン**（キットより新しい側を常に採る）
3. **汎用的な改善は本リポジトリに留めず、`/plan-feedback` でキットへ環流する。** 固有デルタを
   増やせない制約が、環流を怠らせないための強制力として働く。
4. **参照は「文書 → 設定」の向きに置く。** 分類 A のファイルへ「背景は `docs/specs/...` を参照」の
   ようなポインタコメントを書かない。同期のたびに消えるためである。背景へ辿れるようにするのは
   **仕様書側の責務**とし、仕様書の `related_specs` / 本文から対象の設定ファイルへリンクする
   （例: `docs/specs/20260712_issue-260_dependabot-submodule-token-fix.md` →
   `.github/dependabot.yml`）。この向きなら同期で失われない。
5. 同期は `/sync-plan` で planning submodule を最新化した直後に行い、作業仕様書と本 IADR を起点とする。

## 理由

- 分類 A を「バイト一致」と定義したことで、**乖離の検出が機械化**される（人の判断が要るのは分類 B のみ）。
- 固有デルタを 4 種に限定したのは、実際に必要だった逸脱がこの 4 種に収まったためである。
  「なんとなく書き足した説明文」「本リポの spec へのポインタ」といった逸脱は、キットの改善を
  取り込むたびに衝突を生むだけで、キット側に書けるなら書くべきものだった。
- 選択肢 B を採らないのは、キットが**複数の実装リポジトリの共有資産**であり、片方向の環流では
  他リポジトリで得られた知見（planning#95 の記法不具合など）を取り逃すためである。

## 結果

- 良い影響:
  - 壊れていた `--allowedTools` 記法が是正され、CI の AI 実装・レビューがビルド/テストを実走できる。
  - `check-ai-workflow-config.js` が CI に入り、同型の設定不備がマージ前に止まる。
  - コミット件名の ADR/IADR 実在性検査、作業仕様書のブランチ差分判定など、キット側の安全弁を得る。
- 悪い影響・トレードオフ:
  - キットが古い Actions バージョンを持つため、同期のたびに固有デルタ 4（バージョン）を
    再適用する手間が残る。キット側の Dependabot 対象化を計画へ環流済み。
  - 分類 B の判断は依然として人（AI）が行う。分類の誤りは同期時にレビューで検出するしかない。
- フォローアップ:
  - `feedback/20260801_impl-handoff-kit-gaps.md` の 6 件（planning#96 として起票済み）は
    **planning#98（`12cc9b8`）で全件キットへ反映され、同 pin へ再同期済み**。
    これにより固有デルタが 8 ファイル分解消し、本決定の「環流の強制力」が実際に機能することを確認した。
  - 再同期の副産物として、`scripts/commit-allowlist.json` の 5 件がすべて**本リポジトリの履歴に
    存在しない SHA**（他リポジトリからの引き継ぎ）であることをキットの新テストが検出した。
    全履歴に非準拠コミットが 0 件であることを確認のうえ、allowlist を空へ戻した。
  - 指摘 7（`copilot-setup-steps.yml` の雛形除外漏れ）は独立 issue
    [planning#104](https://github.com/endazon/project-planning/issues/104) として起票し、
    planning#105 で反映されたため当該デルタも解消した。
  - 第 3 ラウンド（pin `35b830a`）で、本リポジトリの `gen-changelog.js` が
    `TypeError: overrides.find is not a function` で**完全に壊れていた**ことが判明した。第 2 ラウンドで
    取り込んだ `applyOverride(c, overrides = OVERRIDES)` に対し、呼び出し側が `.map(applyOverride)` の
    ままで `map` の `index` が第 2 引数を上書きしていた。planning#105 の修正で解消。
    この回帰が PR CI をすり抜けた原因＝**`scripts.test.js` がどの CI ジョブからも実行されていない**ことを
    [planning#108](https://github.com/endazon/project-planning/issues/108) として環流し、
    本リポジトリは先行して `ci.yml` に `scripts-tests` ジョブを追加した。
    分類 A の「バイト一致」だけでは**呼び出し側の形**までは守れないという、本決定の限界の実例でもある。
  - 第 4 ラウンド（pin `7701d25`）で planning#108 が反映され、先行追加していた `scripts-tests` ジョブと
    `scripts/README.md` の「検査（CI）」節もキット準拠へ戻した。あわせて、雛形ソリューションのトラップが
    `codeql.example.yml` だけ未対応であることを
    [planning#111](https://github.com/endazon/project-planning/issues/111) として環流した
    （`autobuild` は `find` の除外で直せないため、本リポジトリの明示ビルドは固有デルタとして維持する）。
  - 第 5 ラウンド（pin `c72dbf2`）で planning#111 が反映され、あわせて planning#112 由来の
    **固有テストの受け口**（`scripts/scripts.local.test.js` を自動読み込みする companion 方式）が入った。
    本リポジトリ固有のテスト 48 件を companion へ移した結果、**`scripts/scripts.test.js` が
    キットとバイト一致（分類 A）になった**——同期のたびに手作業でスプライスしていたファイルが
    上書きコピー 1 回で済むようになった。分類 B を分類 A へ引き上げられる余地は他にもあり得るため、
    以後の同期でもキット側の「受け口」の有無を確認する。
  - 同 companion 方式は、ファイルが消えても exit 0 のまま件数だけが減る（実測 101 → 53 件）ため、
    [planning#114](https://github.com/endazon/project-planning/issues/114) として環流した。
  - 第 6 ラウンド（pin `30a4b78`）で planning#114 が反映され、companion は
    `scripts.repo.test.js` へ改名された（`.local` は「コミットしない」の目印と衝突するため。planning#115）。
    `ci.yml` で `REQUIRE_REPO_TESTS: "1"` を有効化し、消失時 exit 1 を実測で確認した。
    その opt-in を忘れた状態が無言である点を
    [planning#117](https://github.com/endazon/project-planning/issues/117) として環流した。
  - 第 7 ラウンド（pin `cff9b6c`）で planning#117 が反映された。この回では
    `Bash(git -C planning …:*)` が CI で誤答すると判断して planning#123 を起票したが、
    **前提が誤っており取り下げた**——レビュー用ワークフローには `actions/checkout` とは別に
    submodule 取得の専用ステップがあり、`git -C planning` は正しく動く。`actions/checkout` の引数
    だけを見て後続ステップを確認しなかったのが原因である。AI レビューが同ジョブ内で実行して
    反証したことで判明した。**環流は「動かないはず」ではなく「動く経路を確認した」上で出す**こと。
  - 第 8 ラウンド（pin `25b4291`）で planning#121 が反映され、`.claude/settings.json` が
    キットとバイト一致に戻った（分類 A の回復）。あわせて「複製漏れは機械検出する」という
    キットのヘッダ記述が**部分的なドリフトを検出しない**ことを実測で確認し、
    [planning#126](https://github.com/endazon/project-planning/issues/126) として環流した。
  - 第 9 ラウンド（pin `3325903`）で planning#126 が反映され、`toolchainDrift` が新設された。
    今回は**陽性対照**（人為的にドリフトを作って ERROR を確認し、復元して合格を確認）を取ってから
    受け入れた——planning#123 の誤報告以降、「動く／動かない」の主張は実際に動かして確かめている。
    その過程で `setup-*` 非対称時の誤検知を発見し
    [planning#130](https://github.com/endazon/project-planning/issues/130) として環流した。
  - 第 10 ラウンド（pin `4d3eb6b`）で planning#130 が反映され、**環流した 15 件がすべて決着した**
    （14 件が反映、1 件は前提誤りで取り下げ）。起票した planning issue は全件クローズ済み。
  - 第 11 ラウンド（pin `168f53d`）で planning#135 を反映し、陽性対照（`claude_args` のキー名を
    1 文字変える）で新しい warn が効くことを確認した。その warn が **exit 0 かつ GitHub Actions の
    annotation を出さない**ため CI のどこにも現れない点を
    [planning#136](https://github.com/endazon/project-planning/issues/136) として環流した。
  - 11 ラウンドの実績として、**固有デルタはリポジトリ構成・技術スタックに起因するものへ収束した**。
    本決定の「固有デルタを増やせない制約が環流の強制力になる」という狙いは機能している。
    とくに `scripts.test.js` は、キット側に companion の受け口ができたことで分類 B → 分類 A へ
    引き上げられた——**環流はデルタを減らすだけでなく、分類そのものを引き上げる**。
  - 計画側に実装 → 計画の逆方向同期（planning#133 の `/sync-impl`）が新設された。同ツールは
    「記録 1 件 ↔ 環流 1 件」で到達を判定するため、`feedback/` の記録に多数の指摘を集約すると
    個々の未決着が見えなくなる。以後キット側の不足は**記録を分けて起こす**。
  - 本同期は Issue #434（`claude_args` の記法誤りで AI 実装・レビューが検証を実行できない・最優先）の
    是正を運ぶ経路でもある。`.github/workflows/` は GitHub App 権限で編集できないため、キット同期の
    PR がこの 2 ファイルを運べるのは `workflow` スコープを持つローカル push だけであり、
    **足場の同期がバグ修正の配送経路を兼ねる**構造になっている。
  - 次回以降の同期でも、本 IADR の 3 分類と固有デルタ 4 種を基準として用いる。

## 関連

- Supersedes: なし
- Superseded by: なし
