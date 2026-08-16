---
title: 作業仕様書 — 波 6 末クロス監査の 🔴 2 件を是正する（見送り条件が解消したのに再判定されなかった）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0139
  - IADR-0141
  - IADR-0201
  - IADR-0204
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§2 束ねの射程)"
related_specs:
  - "20260816_issue-791_bundle-limit-four.md"
  - "20260816_issue-790_planning-pin-8cae89d-and-kit-rejudgement.md"
  - "../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md"
---

# 作業仕様書: 波 6 末クロス監査の 🔴 2 件の是正

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **`NFR`**（運用文書の統制＝メタ作業。計画側の `NFR-01`〜`NFR-27` は稼働する製品の要件であり、
  **当たる番号が無い**。`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**。
  **環流しない**）
- 起点 issue: **無し。** 波 6 末クロス監査（`adr-guardian`）の 🔴 指摘が起点である。
  **issue を起こしていない** —— 是正が 2 行 ＋ 索引 1 セルで、起票と実装が同一セッション内に閉じるため。
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1 の趣旨（レビュー単位を
  分ける）は PR 単位で満たしている。**これは先例を作る判断ではなく、次に同じ規模なら同じ扱いでよい。**
- 関連 ADR: [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 決定 1（束ねの上限）／
  [IADR-0204](../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md)（`scripts.test.js` の分類）／
  [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)（正本を 1 箇所へ畳む）／
  [IADR-0201](../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md)（索引の追随先）

## 2. 何が起きたか —— **検出は機能した。壊れたのは「見送りの再判定」である**

波 6 の 2 本の PR が、`docs/how-to/session-handoff.md` の記述が古くなることを**自分で検出し、自分で記録した**。

| PR | 記録した場所 | 記録した内容 |
| --- | --- | --- |
| #794（#791） | 作業仕様書 §母集合 フォローアップ 1 | 「`session-handoff.md:31` に旧値。**OPEN PR #789 と交差**するので見送る。**#789 のマージ後に追随 issue を起こす**」 |
| #795（#790） | `IADR-0204` 影響節 | 「`session-handoff.md` の『`scripts.test.js` は変更禁止（分類 A）』は古くなった。**本 PR では触れない（並行 PR #789 と交差）**。#793 か次の追随で直す」 |

**規則 9・10（母集合の引き直し）は正しく働いた。** 両方とも走査で見つけ、理由を書いて見送っている。

**壊れたのは次の一点である** —— **#789 は同じ波の中（`bd90c799`）でマージされ、交差の理由はその時点で消滅した。
しかし「見送り条件が解消したか」を誰も再判定しなかった。**

```console
$ git log --oneline d7d6cd8..develop
1cdbf1e3 docs(NFR,IADR-0191): … (#800)
bd90c799 docs(NFR): blocked 判定を別環境で測り直し… (#789)   ← 交差相手。同じ range でマージ済み
ce6302d8 feat(FR-05,UC-04,SC-06): … (#798)
3818c080 chore(NFR): … (#795)                                ← 見送りを記録した側
0e906f7f docs(IADR-0139): … (#794)                           ← 見送りを記録した側
dca76ced fix(FR-14): … (#777)
```

**「あとで直す」と書いた時点で、その条件を誰が・いつ再判定するかは決まっていない。**
本リポには blocked 判定について「**棚卸しごとに再検証する**」規約があるが（`CLAUDE.md`）、
**PR 内の「交差により見送り」には同じ規律が無い。**

## 3. 母集合（**誤りの側の文字列で走査した**）

```console
$ grep -rn "実効上限 2 件\|名目 3 件\|上限は 2 件" --include=*.md . | grep -v "^./planning/"
docs/how-to/session-handoff.md:31:…（資源単位・実効上限 2 件）…
docs/adr/IADR-0116_…:146:> - **束の上限は名目 3 件・実効 2 件**である。…    ← 旧条文の残置（正しい）
docs/adr/IADR-0139_…:267:…**名目 3 件・実効 2 件**…                        ← 旧条文の残置（正しい）
docs/specs/20260816_issue-791_bundle-limit-four.md（複数）                  ← 確定済み仕様書（書き換えない）

$ grep -rn "scripts.test.js は変更禁止\|scripts/scripts.test.js.*分類 A" --include=*.md . | grep -v "^./planning/"
docs/how-to/session-handoff.md:160:- **`scripts/scripts.test.js` は変更禁止**（[[IADR-0115]] 分類 A・…）。

$ grep -n "IADR-0201" docs/adr/README.md
257:| [IADR-0201](…) | …`traceability.md` は companion 分離で分類 A へ。… | Accepted |
```

**是正対象は 3 箇所**（`session-handoff.md` の 2 行 ＋ `docs/adr/README.md` の 1 セル）。

**除外したものと理由**:

| 除外 | 理由 |
| --- | --- |
| `IADR-0116:146` / `IADR-0139:267` の「名目 3 件・実効 2 件」 | **旧条文の意図的な残置**。日付つき追記で後継値を併記済みで、これが正しい形である（`.claude/rules/traceability.repo.md` §Superseded の書式） |
| `docs/specs/20260816_issue-791_*.md` の同表記 | **確定済みの作業仕様書は書き換えない**（同規約）。当時の記録として正しい |
| `scripts/scripts.repo.test.js:5275` のコメント（「キット配布物 traceability.md（分類 A）」） | **`scripts/` は #793 の作業領域と交差する**。#793 の作業仕様書へ引き継ぐ（**今度は交差相手が in-flight なので、見送りではなく担当者へ直接送った**） |
| `CLAUDE.md:43` の上流ガイド更新日／`.claude/rules/traceability.repo.md:24` の凍結の射程 | 同上（#793 の領域）。**担当エージェントへ実測つきで送付済み** |
| `IADR-0121` 決定 2 の workspace グロブ列挙／`package.json` の prettier グロブ | **確定済み ADR の決定そのものを動かす判断**が要る。本 PR の射程を超えるため別 issue（#802） |
| `docs/specs/` への本文追記の可否（`IADR-0166` 決定 2 の射程） | **裁定が要る論点**。別 issue（#803） |

## 4. 是正の方針 —— **値を写すのではなく、写すのをやめる**

`session-handoff.md:31` を「上限 4 件」へ直すのは**採らない**。同じ行が既に
「**正は `CLAUDE.md` の該当行と [[IADR-0139]]**」と書いており、**正本を指しているのに値も持っている**
という [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 違反の形になっている。
**数を落とし、正本を指すだけにする。**

これは #795 が `IADR-0204` で採った結論と同じである（would-be 予算値が 3 回動いたので値を書くのをやめた）。
**本 range だけで、同じ型の是正が 2 度目である。**

`session-handoff.md:160` は**値ではなく事実の記述**なので、正しい事実へ書き直す。ただし
**「分類 B だから何を足してもよい」と読まれないよう、固有デルタは 1 か所だけであることを明記**する。

## 5. 検証

| 検査 | 結果 |
| --- | --- |
| `node scripts/scripts.test.js` | 記録は PR 本文 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 同上（**`scripts.repo.test.js` を単体で叩かない** —— 沈黙の exit 0 になる。#797） |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-adr-numbering` / `check-reading-budget` | 同上 |
| `check-doc-updated` / `check-commit-messages` | **コミット後**に実行（HEAD を読むため。[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)） |
| 索引セルの文字数 | 200 字以内（`adr-index-title-baseline.json` のラチェット） |

**必読規約は 1 バイトも触っていない**（`CLAUDE.md` / `.claude/rules/` は #793 の領域）。

## 6. 残す論点 —— **「見送り」に期限が無い**

本件の根本は誤記ではなく、**「交差により見送る」と書いた記録に、再判定の担い手も期限も無いこと**である。

- `blocked` 判定には「**棚卸しごとに再検証**」の規約がある（`CLAUDE.md`）
- **PR 内の「交差により見送り」には無い**

本 PR では**機械化しない**。理由は 2 つある。

1. **同型の事故として数えると 1 回目である**（`CLAUDE.md`「同型の事故が 2 回起きたら検査器を置く」）。
   本件は 2 箇所だが**単一の原因（#789 との交差）から出た 1 つの事故**であり、2 回とは数えない
2. **機械化の形が自明でない。** 「見送り」は散文にしか現れず、`grep` で拾える語彙が定まっていない。
   検査器を急いで置くと、語彙を固定した瞬間にそれ以外の書き方が素通りする（#800 で実測した
   「述語が `追記` の語を要求するため後付け注記 20 行が掛からない」と同じ型）

**記録に留める** —— 本節がその記録である。**2 回目が起きたら、この節を根拠に検査器を置く。**
そのとき見るべきは「PR 本文・作業仕様書・ADR の影響節に『交差』『見送り』『あとで』が現れ、
かつ交差相手の PR が closed になっている」組み合わせである。
