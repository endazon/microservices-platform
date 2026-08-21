---
title: 別紙 — Superseded な ADR を引用する書式の経緯と、機械検査を置けない理由の測定
type: how-to
status: fixed
created: 2026-08-11
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: []
adrs: [ADR-0003, ADR-0027, ADR-0048]
iadrs: [IADR-0166, IADR-0172, IADR-0173, IADR-0176, IADR-0191]
specs: []
issues: [#717, #803, planning#387]
-->

# 別紙: Superseded な ADR の引用 —— 経緯と測定

> **★ これは「参照時にだけ読む別紙」である。** 毎セッション読む必要は無い。
> **規約の入口は companion [`.claude/rules/traceability.repo.md`](../../.claude/rules/traceability.repo.md)
> 「Superseded / Deprecated な ADR を引用するときの書式」節**（2026-08-16 / #755 に `traceability.md` から移した。同ファイルはキット配布物へ戻した）であり、**規範（ID を付け替えない・旧 ID の隣に後継を置く・注記には起票 ID を
> 添える・母集合は live な権威文書とコードに限る・frontmatter とコードでの書き分け）はそちらに在る。**
>
> **本別紙が持つのは「なぜその規約があるのか」の経緯と、機械検査の可否の実測だけ**である
> （必読規約の減量にあたり、入口の見出しはスタブとして残し中身を別紙へ出す、という方針による）。

## 1. 機械検査を置いていない理由（#580 の測定）

入口の規範は「**機械検査は置いていない。よって本規約は人と AI が守るものであり、CI は守っていない**」である。
**その根拠が以下である。**

計画 ADR の `status` を読むには planning submodule が
必要だが、**PR で起動する決定的な検査ジョブ**（`ci.yml` の `doc-links` / `scripts-tests` /
`commit-messages` 等、`pr-title.yml`）は**どれも submodule を populate しない**ため、検査を作っても
常に skip され緑のまま素通りする。

### 例外は 2 本あるが、いずれもゲートではない

**例外は 2 本あるが、いずれもゲートではない**（#580 の測定・実測日 2026-08-07）。どちらも
`PLANNING_REPO_TOKEN` を使って `git submodule update --init --recursive` を実行する。

| ワークフロー | トリガ | PR 文脈で起動するか |
| --- | --- | --- |
| `claude-code-review.yml` | `on: pull_request`（`opened` / `synchronize`） | する |
| `claude-coding.yml` | `issue_comment` / `pull_request_review_comment` / `pull_request_review` / `issues` | する（PR へのコメント・レビューで起動する） |

ただしどちらも **AI 実行系であってマージを止める決定的ゲートではない**（前者は AI レビュー、
後者は `@claude` メンションでの対話実装）ので、これらに検査を載せても「PR で planning を読む
**検査**」にはならない。「**PR で planning は絶対に取れない**」と読み違えないこと——取れるジョブは
在るが、ゲートではない、が正しい。

> **★ 実効させたいなら**、`check-commit-messages.js` を走らせるジョブへ `submodules` ＋ `token` を
> 付ける必要がある。**入口の「起点 ID の種別」節にある同趣旨の注も同じ測定に由来する。**

> **［2026-08-21 追記 / 実装リポジトリの資料再編］上表の「例外は 2 本」は撤去済みで、現在は 0 本である。**
> 本リポジトリは planning submodule に依存しない。`claude-code-review.yml` /
> `claude-coding.yml` から planning submodule の fetch ステップと `PLANNING_REPO_TOKEN` の参照を
> 撤去した。**「PR 文脈で起動する 2 本の非ゲート例外」は存在しない**——結論（機械検査を置いていない
> 理由）はむしろ強化される（PR 文脈で planning を読む経路が一切無くなったため）。上の測定
> （実測日 2026-08-07）は撤去前の点時点記録として残す。

## 1b. 入口から移した補足（2026-08-16 / #755）

入口を companion へ畳むにあたり、規範ではない説明をここへ移した。

- **付け替えが偽の主張になる理由**: ID を後継へ付け替えると「この実装は後継の決定に従っている」と読まれ、実装の由来（当時なぜそう作ったか）と移行の進み具合の記録が同時に消える。
  実際に移行したときは、決まった文字列 `Superseded by <後継 ID>` を後継 ID へ一括置換する（先に付け替えると移行の実施と記録の一致を後から検証できない）。
- **注記に起票 ID を添える理由**（#580 / クロス監査 G-b）: 本文へ後から差し込んだ注記は原文と見分けが付かず、いつ誰が足したのか本文から辿れなくなる。
  **対象は「後から差し込んだ注記」に限る** —— ファイル新規作成時点の原文（例: `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/NetworkIsolationTests.cs` 冒頭の `// IADR-0017（Superseded by IADR-0026）`）は注記ではないので遡って起票 ID を足す必要は無い。
  **frontmatter を持たないファイル（コード・設定）は注記 ID だけでよい**（`updated:` を前進させる先が無い）。日付つき追記ブロックの形は #570 / #577 / #582 が採っている。
- **後継 ID を旧 ID の隣に置く理由**（#580 / クロス監査 G-c）: 番号順は崩れるが「この旧 ID の後継はこれ」という対応を読み手に伝えることを優先する。機械照合は順序非依存なので実害はない。
- **母集合を live な権威文書とコードに限る理由**: 確定済み（過去 PR の）`docs/specs/`（作業 / PR 単位の一時点記録）・`feedback/`（計画リポへ送った内容の写し）・`docs/superpowers/`（保管された旧計画）は書いた時点の記録であり、後から注記を足すのは記録の改竄にあたる。
  **［#717］「書き換えない」の対象は本文への後付け注記であり、frontmatter の状態欄（`status` / `dispatched:` 等）は対象外** —— キットが更新主体を定めている（記録を書き換えてよい境界は「本文か frontmatter の状態欄か」で切る）。
  - **［#803 追記 / 利用者裁定］凍結の射程は記録種ごとに違う。** 上の「一律に改竄」は改まった。
    `docs/specs/` は **`［YYYY-MM-DD 追記 / #NNN］` 書式での経過追記が可**（自分の計画がどうなったかを同じ場所で読めることに価値がある）、`feedback/` は**①（frontmatter の状態欄を本文で言い直した追記）だけ不可**、`docs/superpowers/` は**不可**である。
    **既存本文の書き換え・削除はどの記録でも不可。** 正本は、仕様書の `status` 語彙と記録の書き換え境界を定めた実装 ADR（決定 2）の 2026-08-17 追記である。
  - **［2026-08-21 追記 / 資料再編］上の 3 つのパスは移設・撤去された。** 作業仕様書は `.ai-context/specs/`、superpowers は `.ai-context/superpowers/` へ移り（資料再編の計画 ADR 決定 1）、環流記録 `feedback/` は撤去されて環流は計画リポジトリの GitHub issue へ一本化された（同 決定 5）。
    **射程の対応づけは変わらない**（作業仕様書＝書式つき経過追記が可、superpowers ＝不可）。現行の規範は `.claude/rules/traceability.repo.md` が持つ。

## 2. コードを対象外にしない理由

入口の規範は「**注記の起票 ID を添える対象は live な権威文書とコードの両方**」である。
**コードだけを外す案を退けた根拠が以下である。**

**コードを対象外にしない理由**:
母集合を切る基準は「**書いた時点の記録か否か**」であり（入口の「母集合」のとおり `docs/specs/` 等はそれで外れる）、
コードはその基準に当たらない。「`git blame` で辿れるから注記 ID は要らない」は `.md` にも等しく
当てはまるので、コードだけを外す根拠にならない。

## 関連

- 入口: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「残す箇所と書式」
- 減量の計画: 入口を残して中身を別紙へ出す ／ 別紙化の方式: 見出しをスタブとして入口に残す ／ 入口の総括: 分類は節単位ではなく塊単位で測る
- 同じ方式の別紙: [`commit-message-rules-annex.md`](./commit-message-rules-annex.md) ／
  [`changelog-overrides-annex.md`](./changelog-overrides-annex.md) ／
  [`cross-project-id-refs-annex.md`](./cross-project-id-refs-annex.md)
