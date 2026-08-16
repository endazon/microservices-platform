---
title: 作業仕様書 — 必読規約を減量し、キット traceability.md の追随を受け入れて分類 A へ戻す（#793）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0141
  - IADR-0171
  - IADR-0173
  - IADR-0175
  - IADR-0177
  - IADR-0178
  - IADR-0183
  - IADR-0188
  - IADR-0190
  - IADR-0192
  - IADR-0200
  - IADR-0201
  - IADR-0204
  - IADR-0205
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 必読規約の予算 51,200 バイト・§11 複数実装リポのパリティ維持)"
  - "../../planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md (追随対象のキット原文)"
  - "../../planning/tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json"
related_specs:
  - "../adr/IADR-0205_reading-budget-reduction-for-kit-catchup.md"
  - "../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md"
  - "../adr/IADR-0190_permanent-headroom-by-annexing-examples.md"
  - "../adr/IADR-0178_claude-md-defers-to-docs-readme.md"
  - "../adr/IADR-0173_annex-extraction-keeps-heading-stubs.md"
  - "20260816_issue-790_planning-pin-8cae89d-and-kit-rejudgement.md"
---

# 作業仕様書: 必読規約の減量とキット追随（#793）

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（必読規約の減量・キット同期＝文書統制のメタ作業）。
  **無採番の根拠は `.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の場合 2**
  ＝「**ID 列はあるが、その作業に当たる番号が無い場合**」である。計画側の `NFR-01`〜`NFR-27` は
  27 件とも稼働する製品の要件であり、**文書統制に当たる番号は 1 件も無い**
  （別紙 [`plan-id-range-history-annex.md`](../how-to/plan-id-range-history-annex.md) §4 の実測）。
  **場合 2 は環流しない**（[[IADR-0179]] 決定 2）。着手前に計画側の ID 列を実際に見て判断した。
- 起点 issue: **#793**（#790 が置いた「期限つき保留」の期限）
- 実装ADR: [[IADR-0205]]

## 2. 母集合を自分で引く（[[IADR-0141]] 決定 1）

### 2.1 issue 本文の数値は使わない

**issue 本文の数値（現在 50,193B / 超過 1,995B）は #790 の作業途中に固定されたもので古い。**
起票者コメントの表も「読む時点では古い可能性がある」と明記しているため、**転記せず自分で measure した**。

```
$ node -e "const fs=require('fs');const s=p=>fs.statSync(p).size;
  const kit='planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md';
  const c=s('CLAUDE.md'),t=s('.claude/rules/traceability.md'),r=s('.claude/rules/traceability.repo.md');
  const {BUDGET_BYTES}=require('./scripts/check-reading-budget.js');
  console.log('現在',c+t+r,'余白',BUDGET_BYTES-(c+t+r));
  console.log('would-be',c+s(kit)+r,'超過',c+s(kit)+r-BUDGET_BYTES);"
現在 50061 余白 1139
would-be 53063 超過 1863
```

基準コミット: develop `1cdbf1e3`。**内訳**: `CLAUDE.md` 23,077 / `.claude/rules/traceability.md` 21,590 /
`.claude/rules/traceability.repo.md` 5,394。キット原文 `traceability.md` = **24,592**（+3,002）。

### 2.2 削るべき量の式（自分で立てる）

```
削るべき量 = 超過分 + 余白下限
           = (CLAUDE.md + キット traceability.md + companion − 51,200) + 1,000
           = 1,863 + 1,000
           = 2,863 バイト以上
```

- 超過分 = **1,863B**（キット原文へ差し替えた「もしも」の合計 53,063 − 予算 51,200）
- 余白下限 = **1,000B**（[[IADR-0190]] 決定 4 のラチェット。`scripts.repo.test.js` の
  `#730: 必読の余白が確保した水準を割っていない` が固定している）

**上限も置く**（削りすぎの防止。規約の減量は不可逆に近い）。**着地後の余白は develop の現状
1,139B と同水準（およそ 1,100〜1,500B）に収める** —— したがって**削減量の目標帯は 2,963〜3,363B**
とし、これを超えて削らない。

### 2.3 削除候補の母集合（走査語・件数・除外理由）

**「なんとなく短くする」を禁じるため、3 つの軸で機械走査してから候補を選んだ。**
**軸を 1 本で終わらせない**（[[IADR-0141]] 決定 1 の規則 5）。

| 軸 | 走査 | 出た件数 |
| --- | --- | ---: |
| 1（複写の疑い） | `grep -n "正本\|単一情報源\|が正\|正である\|を参照" CLAUDE.md` | **13 行** |
| 2（腐りやすい記述） | `grep -n "更新日\|済み\|未実装\|未作成\|予定\|移行第\|段に分割\|現在\|20260\|2026-" CLAUDE.md` | **7 行** |
| 3（キット新版と companion の重複） | `grep -c -F "〔〕" / "死んだリンク" / "全ファイル"` を companion とキット原文の両方に当てる | **3 件**（うち 2 件が真の重複） |
| 4（塊の実測） | 節ごとのバイト数を `node` で算出（下表） | **16 節** |

**軸 4 の実測（`CLAUDE.md` = 23,077B）**:

```
   731 # CLAUDE.md — 実装作業リポジトリ          811 ## トレーサビリティ規約
   530 ## 目的                                  1487 ## 仕様書（docs/）
   725 ## 計画書の参照                            514 ## 補助成果物の自動生成
  1890 ## 実装の進め方（AI 活用の基本フロー）          919 ## 生成 AI の活用
  3416 ## 実装作業の進め方（計画リポの運用ガイド）      1042 ## 自動化・検証・安全
   392 ## Git 運用                               626 ## 禁止事項
   634 ## 技術スタック別ルール                     2175 ### C# / .NET
  6273 ### TypeScript / React                    913 ### CI（GitHub Actions）
```

#### 採用した候補（削る）

| # | 箇所 | 区分 | 理由（正本 / 腐り） |
| --- | --- | --- | --- |
| 1 | L43 節前文の経緯・**更新日 2026-08-15** | (c) 腐る | **更新日は正本が動くたびに腐る**。導線（リンク）だけ残す |
| 2 | L46 束ねの条件の詳細（6 条件・判定の単位・上限・射程） | (b) 複写 | 正本は**同リポの [[IADR-0116]] 規約 1 / [[IADR-0139]] 決定 1**。条件の中身は入口が持たない |
| 3 | L47・L48 監査・裁定の説明部 | (b) 複写 | 正本は運用ガイド §。規範の一文だけ残す |
| 4 | L50 予算の子箇条（596B） | (b) 複写 | 正本は運用ガイド §8 と `scripts/check-reading-budget.js`（**出典つきの複製を持ち、100% 超 fail / 90% 以上 warn を実際に強制している**）。入口が測り方まで持つ必要は無い |
| 5 | L51 人間の関与 3 点（225B） | (b) 複写 | 正本は運用ガイド。**AI が行動する規範ではない**（読まなくても成果物は壊れない。[[IADR-0173]] 決定 2 の反対側） |
| 6 | L52 パリティ §11 の手順詳細（555B） | (b) 複写 | 正本は運用ガイド §11。**「配布点は kit に一本化」だけは AI が行動する規範**なので残す |
| 7 | L33 手順 3 の仕様書列挙（475B） | (b) 複写 | **同ファイルの「仕様書（docs/）」節と `docs/README.md`** が正本。本文が「後述『仕様書』参照」と自分で言っている |
| 8 | L68–70 「ここへ複写しない」の説明 3 行 | (b) 複写 | 規範（複写しない）は残し、**理由の説明**を [[IADR-0141]] へ寄せる |
| 9 | L143 認証の移行進捗（第 3 段 / #439） | (c) 腐る | 正本は [[IADR-0121]] 決定 6（**段の進捗は同 IADR が正本**と L136 が既に宣言している） |
| 10 | L145 整形の対象範囲の列挙 | (b) 複写 | **本文自身が「対象範囲の単一情報源は `src/.prettierignore`」と言いながら列挙を複写している** |
| 11 | L139 UI/CSS・L140 i18n の説明部 | (b) 複写 | 正本は [[IADR-0121]] / [[IADR-0125]] と `src/packages/ui/README.md`。**禁止・強制の規範はすべて残す** |
| 12 | companion の `〔〕` 箇条 | (b) 複写 | **キット原文が同じ規範を持つ**（「関連番号を添える注記 `〔〕` の中も列挙として見る」「全角丸括弧 `（` は区切りとして扱わない」）。取り込んだ瞬間に二重になる |
| 13 | companion の型 4 の理由句「`.md` でも死んだリンクになる唯一の型」 | (b) 複写 | 同上（キット原文が「フルパス形式は `.md` でも自動リンクするため…死んだリンクになる」を持つ）。**固有値 `endazon` は残す** |

#### 除外した候補（削らない）と理由

**黙って除外しない**（[[IADR-0141]] 決定 1 の規則 6）。

| 候補 | 除外理由 |
| --- | --- |
| `### TypeScript / React` の禁止・強制（ESLint / 検査器が止める旨） | **規範である。** 別紙・正本へ出すと「読まなかっただけで CI に落ちる」（[[IADR-0173]] 決定 2） |
| `### C# / .NET`（2,175B） | 全行が置換点の規範（版・CPM・slnx・サービス境界）。**複写でも腐りでもない** |
| `## 実装の進め方` の手順 1・2・4〜9 | 毎セッションの行動規範。手順 3 だけが同ファイル内の複写 |
| `## 禁止事項`・`## Git 運用` | 規範のみ。すでに最小 |
| `.claude/rules/traceability.md` | **キット配布物。1 バイトも触らない**（本 issue の目的はキット原文でのバイト一致） |
| companion の `走査基準: planning`・`FR-01..22`・`AST`・`endazon`・規則 9・10 | **本リポ固有の規範**で、キットは持ち得ない。`scripts.repo.test.js` が文言で固定してもいる |
| 新しい別紙（`docs/how-to/*-annex.md`）の作成 | **今回は 1 本も作らない。** 削る対象はすべて**正本が既に在る複写**であり、別紙を作ると 3 箇所目になる（[[IADR-0141]] / [[IADR-0178]] 決定 1）。**畳み先はすべて既存の正本** |

## 3. 受け入れ基準

1. `.claude/rules/traceability.md` が**キット原文とバイト一致**（`cmp` が無出力）
2. `node scripts/check-reading-budget.js` が **exit 0**、Claude Code の余白が **1,000B 以上**
3. 余白が過大でない（**削りすぎていない**。§2.2 の目標帯 1,100〜1,500B）
4. `node scripts/check-kit-sync.js --require-planning` が exit 0 で、`traceability.md` の分類が **A**
5. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が全件通る
6. companion の文言を 1 つ壊すと `scripts.repo.test.js` が**落ちる**（変異試験）
7. 規範が 1 つも失われていない（削除は複写・経緯・進捗に限る）

## 4. #790 が置いたラチェットの扱い（判断 3）

`#790: traceability.md の追随保留は「予算を超える」実測が支えている` は、**分類 B に居るときだけ**
「キット版を取り込むと予算を超える」ことを検査する（`if (!deferred) return;` で分類 A なら素通り）。

**分類 A へ戻した瞬間、この試験は 1 行目で return して何も検査しなくなる。**

- **消さない。** 消すと、将来また保留（分類 B へ戻す）したときに**根拠を検査する装置ごと失われる**。
  [[IADR-0204]] 決定 1 が定めた「保留の根拠そのものを機械が検査する」という規律は、
  `traceability.md` だけの話ではなく**キット追随一般の規律**である。
- **代わりに、A に戻った状態で意味を持つ性質を足す**: **分類 A なら、キット原文へ差し替えた合計が
  予算内であること**（＝いま成立している事実そのもの）。これは `deferred === false` の側の
  表明であり、**将来の加筆で予算を割ったときに「追随を維持できない」ことを先に鳴らす**。
  結果として**どちらの分類に居ても空振りしない**試験になる。

## 5. キット側への環流（判断 2・**本 issue の完了条件にしない**）

**キットが配る `traceability.md` 1 本だけで予算 51,200B の 48.0%（24,592B）を占める。**
配布先はこの上に固有規約（本リポなら `CLAUDE.md` 23,077B ＋ companion）を載せるため、
**配布物が育つたびに配布先が減量を強いられる**構造になっている（本 issue がその 1 回目）。

**環流の起票案**（起票そのものは人間が判断する。**他リポの裁定待ちを本 issue の完了条件にしない**）:

- 宛先: `project-planning`（`tools/impl-handoff-kit/`）
- 件名案: **キット `traceability.md` に予算配分を置き、経緯・実測を別紙へ出す**
- 主張:
  1. **予算 51,200B の内訳を運用ガイド §8 が定めていない。** 配布物が何バイトまで占めてよいかの
     取り決めが無いため、**キットの加筆はすべて配布先の減量債務**になる。**配布物の上限
     （例: 予算の 1/3）を §8 に置くことを提案する。**
  2. キットの `traceability.md` は本リポと同じ手法（[[IADR-0173]] の別紙化・[[IADR-0178]] の
     正本へ畳む）で減量できる。**実測の経緯（「10.8 KB」「22 件」「planning#350 の 27 行」等）は
     規範ではなく、参照時にだけ読む別紙へ出せる。**
  3. 本リポの実測（本 issue の削減内訳）を証跡として添える。

## 6. 影響範囲

| ファイル | 変更 |
| --- | --- |
| `CLAUDE.md` | 減量（複写・経緯・進捗の削除） |
| `.claude/rules/traceability.md` | **キット原文で上書き**（バイト一致） |
| `.claude/rules/traceability.repo.md` | キットと二重になった 2 箇所を削除 |
| `scripts/kit-sync-classification.json` | `traceability.md` を B（X） → **A** へ |
| `scripts/scripts.repo.test.js` | #790 ラチェットの追随、companion 削除分の参照の付け替え |
| `docs/adr/IADR-0205_*.md` | 新規（減量方針・ラチェットの扱い・環流案） |
| `docs/adr/README.md` | 索引 1 行（セルは 200 字以内） |

## 7. 実測（着地）

### 7.1 減量と是正を分けて記録する

**同じファイルを触るので混ざりやすい。バイト数の出どころを分けて書く。**

| 対象 | 減量（複写・経緯・進捗の削除） | 是正（腐りの修正） | 差引 |
| --- | ---: | ---: | ---: |
| `CLAUDE.md` | **−3,144** | 0（是正 1 件は削除と同一操作＝下表 ※） | **−3,144** |
| `.claude/rules/traceability.repo.md` | **−330** | **＋493** | **＋163** |
| **合計** | **−3,474** | **＋493** | **−2,981** |

※ **上流ガイドの「更新日 2026-08-15」は削除で是正した**（§7.3 の判断 1）。バイトは減量側に含まれる。

**削るべき量 2,863B に対し、実効 2,981B。** 目標帯（2,963〜3,363B）の下端に収まっている。

```
$ node scripts/check-reading-budget.js
  warn  Claude Code: 50,082 バイト（予算 51,200 の 97.8%）
          CLAUDE.md  19,933
          .claude/rules/traceability.md  24,592
          .claude/rules/traceability.repo.md  5,557
EXIT=0
```

**余白 1,118B**（下限 1,000B を満たし、develop の 1,139B と同水準＝**削りすぎていない**）。

### 7.2 キット追随

```
$ cmp planning/tools/impl-handoff-kit/repo-template/.claude/rules/traceability.md .claude/rules/traceability.md
（無出力＝バイト一致）
$ node scripts/check-kit-sync.js --require-planning
[check-kit-sync] OK: キット 115 件を分類表と突合しました（A 77 件はバイト一致 / B 26 件は固有デルタ / C 4 件は同期しない / 対象外 8 件）。
```

**分類 B（X・26 件）→ A（77 件）へ 1 件移動した。** `traceability.md` は **A**。

### 7.3 折り込んだ是正 3 件（波末クロス監査・ADR 監査の指摘）

**いずれも「導出値または他文書の写しが腐った」型で、削除候補を探す走査で同時に見つかる位置にあった。**

| # | 事象 | 判断と根拠 |
| --- | --- | --- |
| 1 | `CLAUDE.md:43` の上流ガイド「更新日 2026-08-15」（実体は 2026-08-16） | **日付を書かない形にした。** 上流が更新されるたびに腐る導出値である。**「どの版か」は日付では担保できない**（日付は pin と独立に動く）ため、**参照の書き方**——「射程の正は上流ガイド §2」——で権威を示す形に寄せた（#791 が移した権威はその文で保持している）。**併せて planning#294 / planning#298 の設立経緯も落とした**（正本の履歴であって入口の規範ではない） |
| 2 | companion `:7` と別紙 `:66` の「5 世代分」（実体は 8 世代） | **両方から数を消した。** 「8 へ直す」は別紙に 1 世代足すたびにまた腐る。**回帰試験も「数が一致するか」から「数を持たないか」へ変えた** —— 一致検査は**揃って古い**状態を緑で通し、実際に通っていた |
| 3 | companion `:24` の凍結の射程が旧いまま（PR #800 / 裁定 planning#369 に未追随） | **`feedback/` についてだけ**射程①への限定と②③（トリアージ結果・裁定の記録／元を消さない自己是正）の「残す」を書いた。**[[IADR-0191]] の 2026-08-16 追記は明文として `feedback/` についてのみ**であり、`docs/specs/` へ及ぶとは書いていない。**したがって `docs/specs/`・`docs/superpowers/` は無条件の「書き換えない」を維持し、両者を区別して書いた**（射程を勝手に広げない）。`docs/specs/` 側は別途の裁定待ちとして射程外 |

### 7.4 「数を持つ記述」の全走査（規則 10 の機械化）

**是正 2 を受けて、必読 2 ファイルの導出値を全走査した。**

```
$ grep -noE "[0-9]+ (世代|件|種|本|点|箇所|つ|プロジェクト|ファイル|行|回)(分|目)?" CLAUDE.md .claude/rules/traceability.repo.md
$ grep -noE "（必須 [0-9]+ / 任意 [0-9]+）|規則 [0-9]+〜[0-9]+|[0-9]+ 段|FR-01\.\.[0-9]+|規則 [0-9]+・[0-9]+" CLAUDE.md .claude/rules/traceability.repo.md
```

| 記述 | 導出値か | 判断 |
| --- | --- | --- |
| 「5 世代分」 | **はい**（別紙の記録数） | **消した**（是正 2） |
| 「更新日 2026-08-15」 | **はい**（上流の frontmatter） | **消した**（是正 1） |
| companion「規則 1〜8 / 9・10」 | **はい**（キットの規則表） | **実測で一致**（キット原文の規則表は 8 行）。**残す** —— キットの規則番号は規約そのもの（本文が「規則 7」と番号で呼ぶ）であり、数を消すと参照ができない |
| companion `FR-01..22` / `AST` の `FR-01..20` | いいえ（**走査の結果そのもの**） | **残す。** pin を併記して「いつの実測か」が読める形になっている |
| `CLAUDE.md`「必須 10 / 任意 9」 | **はい**（`docs/README.md` の表） | **残す。** `#697` の回帰試験が `docs/README.md` 側の行数を下限で固定しており、**乖離したら CI が鳴る**（機械が守っている数は入口に置いてよい） |
| `CLAUDE.md`「Shared/ の 3 プロジェクト」 | いいえ（`IADR-0117` が定めた**許可の数**） | **残す**（実体は 2 だが、これは「まだ作っていない」であって腐りではない） |
| `CLAUDE.md`「同型の事故が 2 回」「1 本ずつ」 | いいえ（**閾値・規範**） | 残す |

### 7.5 検証コマンドの結果

| コマンド | exit | 備考 |
| --- | ---: | --- |
| `node scripts/scripts.test.js` | 0 | |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 0 | **621 件**（companion 形式で 1 件も走らない沈黙を避けるため単体では叩かない） |
| `node scripts/check-kit-sync.js --require-planning` | 0 | **`traceability.md` は分類 A** |
| `node scripts/check-reading-budget.js` | 0 | 余白 1,118B（warn 帯だが exit 0） |
| `node scripts/check-doc-links.js` | 0 | 643 件 |
| `node scripts/check-cross-repo-refs.js` | 0 | 1,653 件 |
| `node scripts/check-plan-id-qualification.js` | 0 | 1,353 件 |
| `node scripts/check-doc-type-vocabulary.js` | 0 | 617 件 |
| `node scripts/check-adr-numbering.js` | 0 | 重複・欠番なし |

### 7.6 変異試験（宣言だけでは不合格）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | companion の `走査基準: planning` を言い換える | fail | `companion に固有規範「走査基準: planning」が無い` |
| M2 | companion の凍結境界の規範文言を言い換える | fail | `書き換え境界の規範「「書き換えない」の対象は本文への後付け注記である」が入口から消えた` |
| M3 | companion に「5 世代分」を戻す | fail | `入口に世代数「5 世代分」が戻っている。…数を書かずに参照だけ残すこと（#793）` |
| M4 | `CLAUDE.md` へ 1,200B 加筆 | fail | `必読合計が予算を超えた（51293B / 上限 51200B・超過 93B）`。`check-reading-budget.js` も exit 1 |
| M5 | **キット原文だけが 1,200B 育った状況**（`statSync` を差し替えて実行） | **分類 A 側のラチェットが fail** | `分類 A（キット原文と同期）なのに、キット原文込みで予算を超える（51282B > 51200B）` |
| M6 | 分類を B（X）へ戻す | **分類 B 側のラチェットが fail** | `キット版を取り込んでも予算内（50082B <= 51200B）である。保留の根拠が消えたので…分類 A へ戻すこと（#793）` |

**M5 / M6 が、ラチェットが分類のどちら側でも空振りしないことの実測である**（M4 は #724 の予算試験が先に鳴るため、A 側の分岐を単独では確かめられない。だから M5 を別に置いた）。

## 8. 射程外

- キット `traceability.md` の減量そのもの（§5 の環流。他リポの裁定が要る）
- 運用ガイド §8 への予算配分の追記（同上）
- `AGENTS.md`（別枠。予算の合算をしない。[[IADR-0200]]）

## 8. ★［2026-08-16 追記］「隣接クローン」を落としたことの波及（AI レビュー 🟡）

**§7 の逸脱 3（`## 計画書の参照` から「隣接クローン」の選択肢を落とした）は、整合の確認先が足りていなかった。**
`check-kit-sync.js` のコメントだけを見て「誤りにならない」と判断したが、**同じ事実を述べる他の文書を走査していない**
—— 規則 10 の破れである。

### 母集合（誤りの側の文字列で全走査）

```console
$ grep -rn "隣接クローン" --include=*.md . | grep -v "^./planning/" | grep -v "^./docs/specs/"
AI_SETUP.md:43              AGENTS.md:8              feedback/README.md:16
docs/ai-workflow.md:35      docs/ai-workflow.md:210  .claude/commands/sync-plan.md:15
docs/adr/IADR-0201:98       docs/adr/IADR-0202:36    docs/adr/IADR-0193:90
```

**分類で扱いが割れる**（`kit-sync-classification.json` で実測）:

| ファイル | 分類 | 扱い |
| --- | --- | --- |
| `AGENTS.md` / `AI_SETUP.md` / `docs/ai-workflow.md` / `feedback/README.md` | **B** | **事実（submodule 構成）へ揃えた** |
| `.claude/commands/sync-plan.md` | **A（キット配布物）** | **編集しない。** かつ「隣接クローンの場合: …」は**条件分岐**であって「本リポがどちらか」を主張していないため矛盾しない |
| `docs/adr/IADR-0201` / `IADR-0202` / `IADR-0193` | — | **別の話題**（`check-kit-sync.js` 等が隣接クローンを探索できるという**検査器の能力**の記述）。対象外 |

### なぜ「CLAUDE.md へ戻す」を採らなかったか

**実体が submodule ただ 1 つだから**である（`.gitmodules` で実測）。選択肢として書くと、**隣接クローンを設定した人が
`check-planning-pin-freshness` 等の submodule 前提の検査器を壊す**。`AGENTS.md` にだけ「キットは隣接クローンにも
対応するが本リポは採らない」と一文残し、**キット配布物との関係が読めるようにした**（Codex / Cursor の入口であり、
`sync-plan.md` の条件分岐に出会うのはこちら側のため）。

**必読規約への影響は 0 バイト**（`CLAUDE.md` は変更していない）。
