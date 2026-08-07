---
title: レビュー用 allowedTools に diff が無く、拒否報告が許可済みツール名を指してしまう
type: plan-feedback
status: accepted
category: その他
related_ids: [NFR, IADR-0115]
source_repo: endazon/microservices-platform
source_ref: PR #437 (chore/NFR-kit-sync-review-honesty) / docs/specs/20260802_impl-handoff-kit-sync.md
author: Claude
created: 2026-08-02
---

# フィードバック: レビュー用 allowedTools に `diff` が無く、拒否報告が許可済みツール名を指してしまう

## 種別

その他（`impl-handoff-kit` の `repo-template` の不足・検査器の報告精度）。計画書（要求・UC・画面）の
記述に対する誤り指摘ではなく、**キットが配布する成果物**に対するフィードバックである。

## 起点となる計画書

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし（開発基盤・NFR）
- 関連 ADR: 本リポジトリの `docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md`
  （キットを正とする同期規約。`--allowedTools` はキットが単一情報源であり、実装リポ側で
  独自に足すと分類 B で許容した固有デルタの範囲を外れる。ゆえに本件は上流へ環流する）
- 計画書リンク: `tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`
  / `tools/impl-handoff-kit/repo-template/scripts/check-permission-denials.js`

## 現状（As-Is）

planning#156 / planning#157（`cc4a826`）を本リポジトリへ同期した PR #437 で、レビュー自体は成功した
（指摘 0 件・「🔍 実行できなかったこと」節も出力された）が、`Check permission denials` が
**7 件の拒否**を検出してジョブが赤になった。内訳は次のとおり。

```
Bash(git diff)（2 件） / Bash(git show)（2 件） / Bash(diff)（1 件）
/ Bash(git -C)（1 件） / Bash(rm -rf)（1 件）
```

- `Bash(rm -rf)` は**正しい拒否**である（レビュー用に書き込み手段は無く、設計どおり）。
- 残る 4 種はキット側に起因する。とくに **`Bash(git diff)` / `Bash(git show)` は
  `--allowedTools` に `Bash(git diff:*)` / `Bash(git show:*)` として含まれている**。
  すなわち報告は「許可済みのツールが拒否された」と読める形になっており、読み手を誤った対処へ導く。

原因は `check-permission-denials.js` の `labelOf()` にある。

```js
const head = cmd.split(/[|;&><]/)[0].trim();
const tokens = head.split(/\s+/).filter(Boolean).slice(0, 2);
```

- **複合コマンドは先頭セグメントだけでラベル付けされる**。`git show A:f | diff - B` や
  `git show X > /tmp/a` は、拒否された実体が後段（`diff` / リダイレクト）であっても
  `Bash(git show)` として報告される。
- **先頭 2 トークン固定の切り詰めが `git -C <dir> <subcommand>` 形で破綻する**。
  `git -C planning rev-parse` は `Bash(git -C)` になり、**対処に必要なサブコマンドが消える**。
  キットは `Bash(git -C planning log:*)` など 4 エントリを自ら配布しているため、この形は
  planning を submodule 参照する全リポジトリで日常的に現れる。

なお HOWTO 付録3 は「パイプは各コマンドが個別に判定される。報告に出るのは先頭コマンド名なので
原因が判りにくい形で毎回落ちる」と**この挙動を既に文書化している**。しかし planning#147 の趣旨は
「拒否報告をコマンド名まで出す」であり、`cat` / `head` / `tail` を許可リストへ足す対処は入った一方、
**報告側は変更されていない**。文書での注意喚起は、CI に人が居ない前提と噛み合わない。

`Bash(diff:*)` がレビュー用 `--allowedTools` に無い点も実務上の不足である。**キット同期 PR の
レビューは「キットのファイルと実装リポのファイルを突き合わせる」ことが中心作業**であり、
実際に本 PR のレビューもバイト一致の確認を行っている（`git show` での代替は可能だが、
2 ファイルの比較には素の `diff` が最も自然で、現に AI が選択して拒否された）。

## 問題点 / あるべき姿（To-Be）

1. **報告が対処に直結しない**。「`Bash(git diff)` が拒否された」を読んだ人は、既に許可済みの
   エントリを見て混乱するか、重複エントリを足して解決しない。planning#155 が塞ごうとした
   「拒否が見えない」問題は解けたが、**「見えたが何を直せばよいか判らない」**が残っている。
2. **レビューが赤になる**。指摘 0 件・他 18 チェック green の PR が、レビュー内容と無関係な
   理由だけで赤になる。これは planning#146 で塞いだ失敗モード（「成果物は正しいのに赤」）と同型で
   あり、`diff` の欠落が同じ結果を生んでいる。
3. あるべき姿: **拒否されたコマンドそのものを特定できるラベル**が出ること、および
   キット同期 PR のレビューに必要な読み取り専用コマンドが最初から許可されていること。

## 実装で判明した経緯

- 作業: `docs/specs/20260802_impl-handoff-kit-sync.md`（planning `9cd3499` → `cc4a826` の同期）
- PR #437 の `claude-review` ジョブ（run 30714233170）が 7 件の拒否で exit 1。
- レビュー本文自体は正常に投稿され、planning#156 で入れた「検証の誠実性」節も期待どおり機能した
  （`STRICT_AI_WORKFLOW_CONFIG=1 …` を「未検証」と明記し、代替検証の内容と限界まで書いていた）。
  **本件は planning#156 の効果を否定するものではなく、その次に現れた層の問題である。**
- 実際に打たれたコマンド文字列はジョブログに出ないため（アクションがツール入力を出力しない）、
  「どの複合コマンドだったか」は特定できていない。ただし `labelOf()` の実装から、
  **複合コマンドが先頭セグメントでラベル付けされること自体は確定**である。

## 提案（計画への反映案）

- 反映先候補: **`impl-handoff-kit` の修正**（要求更新・新 ADR ではない）

1. **`labelOf()` を複合コマンド対応にする**（`check-permission-denials.js`）
   - `|` `;` `&&` で分割した**全セグメント**をラベル化し、`Bash(git show) → Bash(diff)` のように
     連結して出す、あるいは「複合コマンド（N 段）」と明示したうえで各段を列挙する。
   - 引数を出さない方針は維持できる（各段とも先頭トークンのみでよい）。
2. **`git -C <dir>` 形の切り詰めを是正する**（同上）
   - 2 番目のトークンが `-C` の場合は 4 トークン（`git -C planning log`）まで採る。
     許可リストのエントリ（`Bash(git -C planning log:*)`）と同じ粒度に揃うため、
     「どのエントリを足せばよいか」がそのまま読める。
3. **レビュー用 `--allowedTools` に `Bash(diff:*)` を追加する**（`claude-code-review.example.yml`）
   - 読み取り専用であり、書き込み系は含まない。キット同期 PR のレビューでの中心作業に必要。
   - あわせて、実装用（`claude-coding.example.yml`）との非対称が新たに生じないか確認する
     （`check-ai-workflow-config.js` のドリフト検査はスタック別実行ツールのみを見るため、
     この差は検出されない）。
4. （任意）**リダイレクトを含むコマンドの扱いを決める**
   - `> /tmp/a` はレビュー用では常に拒否される。プロンプト側で「レビューでは出力を
     ファイルへ書かない」と明示するか、報告側で「リダイレクトのため拒否」と判る形にする。

## 影響範囲

- 影響先: キットを利用する全実装リポジトリの `claude-code-review` ジョブ。1 と 2 は報告文言のみの
  変更で、判定ロジック（exit コード）には影響しない。3 は許可の追加であり、既存の判定を緩める方向
  だが読み取り専用に限定される。
- 本リポジトリ側の暫定対応は**行わない**。`--allowedTools` はキットが単一情報源（IADR-0115）で
  あり、実装リポで先に足すと次の同期で毎回手動マージが要る。上流の修正を待って同期する。
- 関連: planning#146（読み取り系 git の欠落）・planning#147（拒否報告をコマンド名まで出す）・
  planning#155 / planning#157（検証の誠実性・残り 2 系統の拒否）。本件はその系列の続きである。

## 計画側の対応（2026-08-02・受理）

計画リポジトリ planning#160 として起票し、planning#159（`65adb87`）で**提案 1〜3 がすべて反映された**。
本リポジトリへは同日の同期で取り込み済み。

- **提案 1（複合コマンド対応）**: `labelOf()` が `|` `;` `&&` `\n` で分割した**全セグメント**を
  ラベル化し、`Bash(git show | diff)` の形で出すようになった。リダイレクト以降はファイル名として
  除外し、`for` / `while` 等のシェルキーワードは読み飛ばす。表示は 4 セグメントで打ち切り（`…`）。
- **提案 2（`git -C <dir>` の粒度）**: `tokens[0] === 'git' && tokens[1] === '-C'` の場合に
  4 トークンまで採り、`Bash(git -C planning log)` と許可リストのエントリと同じ粒度になった。
  あわせて 2 トークン目がフラグ（`-` 始まり）のときは採らない（`head -5` → `Bash(head)`）。
- **提案 3（`Bash(diff:*)` 追加）**: レビュー用に `cmp` / `diff` を追加。さらに**実装用にも
  `head` / `tail` / `cmp` / `diff` / `git ls-tree` / `git submodule status` / `git fetch` を揃え**、
  提案 3 で指摘した「ドリフト検査が読み取り専用ツールの非対称を検出しない」点にコメントで注記された。
- **提案 4（リダイレクトの扱い）**: プロンプト側で対応。「出力をファイルへリダイレクトしない」
  「2 ファイルの比較は一時ファイルを作らず `git show <ref>:<path> | diff - <path>` で行う」
  「シェルのループ・複合形は先頭トークンが `for` 等になるため許可リストで表現できず必ず拒否される」
  を明記。
