---
title: claude-code-action を SHA で固定し、2 か所の版の一致を機械で検査する
type: spec
status: done
related_ids: [NFR, ADR-0007, ADR-0008, IADR-0141, IADR-0130, IADR-0232]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 浮動タグは他人の都合で門を落とす（#1352）

## 起点

- NFR（運用保守。**無採番** —— 工程の統制であり、計画側の非機能要件表に当たる番号が無い）
- 計画 ADR: `ADR-0007`（CI/CD）
- issue: #1352

## 現状（実測）

**すべての PR で必須チェック `claude-review` が落ちる**（2026-09-08 22:28 JST 以降）。

| 時刻（JST） | PR | `@v1` が解決した SHA | 結果 |
| --- | --- | --- | --- |
| 17:05 | #1344 | `9c5ddab2e6d17b83ea679153b31f1d5f023cf636` | ✅ success（判定を投稿） |
| 22:28 | #1350 | `0d0e0876d3eaa933f45dc692f7a4312c83caf36f` | 🔴 failure |
| 22:38 | #1351 | `0d0e0876d3eaa933f45dc692f7a4312c83caf36f` | 🔴 failure |

新しい版の実ログ:

```
ReferenceError: Claude Code native binary not found at /home/runner/.local/bin/claude.
  errorClass: "executable_not_found"
```

Bun のセットアップまでは success で、**その後の CLI 導入だけが行われていない**。
再実行しても同じ場所で落ちる（実測 3 回）。**PR の内容とは無関係**である。

> 🔴 **`check-review-verdict.js` は正しく働いた。** action は「Claude encountered an error」と
> コメントしただけで、判定なしのまま終わろうとした。この門が無ければ
> **「レビュー済み」として緑で通っていた**（[[IADR-0130]] の「0 件で緑にしない」と同型）。

## 母集合（規則 1・2・9）

**誤りの側**＝「第三者 action を浮動参照している箇所」で全ワークフローを走査した。

```
grep -rn "anthropics/claude-code-action" .github/workflows/
  → claude-code-review.yml:116   @v1
  → claude-coding.yml:111        @v1
```

**2 件。どちらも浮動タグである。**

### 除外したものと理由（規則 6）

- **`actions/checkout@v7` / `actions/setup-node@v7` / `actions/setup-dotnet@v6`**:
  GitHub 公式（first-party）であり、本 issue の射程（第三者 action の浮動参照）に含めない。
  **今回の事故はここでは起きていない**ので、射程を広げない（1 回目・記録に留める）。
- **`oven-sh/setup-bun@0c5077e…`**: `claude-code-action` の**内部**が既に SHA で固定している
  （我々の配線ではない）。

## 決定

### 決定 1: 🔴 **SHA で固定する。浮動タグへ戻さない**

`anthropics/claude-code-action@9c5ddab2e6d17b83ea679153b31f1d5f023cf636`（実測で緑だった版）。
第三者 action を SHA で固定するのは、供給網の観点でも推奨作法である。

**版を上げるときは PR で実測して緑を確認してから SHA を進める。**
上げ方を同じ場所（ワークフローのコメント）に書いた。

### 決定 2: 🔴 **2 か所は同じ SHA を指す**

片方だけ動かすと「**レビューは通るのに `@claude` は動かない**」という、
切り分けの難しい状態になる（どちらも同じ action だと気付きにくい）。

### 決定 3: 🔴 **一致と固定を機械で検査する**（`check-ai-workflow-config.js`）

**この検査器は「ジョブは成功するのに検証を実行できない」設定不備を止めるために在る**（既存の目的）。
浮動タグはまさにその型であり、置き場として正しい。既存の
`toolchainDrift` / `genericBashDrift`（2 ファイル間の突き合わせ）と同型に足した。

- 40 桁の SHA でなければ違反（浮動タグ・ブランチ名・短縮 SHA を通さない）
- 参照が 2 種類以上あれば違反
- **参照が 1 件も無ければ何も言わない**（配布先のリポジトリで fail-open。既存の設計に合わせる）
- 走査は **`applicable` で絞らず全ワークフロー**から引く ——
  `claude_args` を持たない配線が将来足されたときに、黙って射程から外れないため

🔴 **門は緩めていない。** `check-review-verdict.js` はそのままである。

## 変異試験（自己試験に対で入れた）

| # | 変異 | 検知 |
| --- | --- | --- |
| M-1 | 片方を `@v1`（浮動）へ戻す | ✅ 「浮動参照」＋「2 種類ある」で 2 件 |
| M-2 | 2 か所を**違う SHA** にする | ✅ 「2 種類ある」 |
| M-3 | 参照を 1 件も持たない | ✅ 何も言わない（fail-open が保たれる） |
| M-4 | 末尾コメント付きの SHA | ✅ 誤検知しない（`# v1（…）` を SHA の一部と読まない） |

M-1 は**実ファイルでも実走**した（`claude-coding.yml` を `@v1` へ戻すと実データ検査が exit 1）。

自己試験 30 → **35 件**。

## 🔴 レビュー指摘で見つかった「同じ形の残骸」（規則 10 の実演）

AI レビューが 🟡 2 件を挙げた。**どちらも本 PR が問題視しているのと同じ「写しが腐る」型**であり、
**是正のたびに新たに誤りになる自分の記述を引き直す**（規則 10）が働いていなかった実例である。

| 指摘 | 実体 | 是正 |
| --- | --- | --- |
| 🟡 `scripts/action-versions.json:19` | `"anthropics/claude-code-action": 1` はメジャー番号での突合を前提としており、SHA 固定後は**どのワークフローとも一致しない** → `check-action-versions.js` が「表が古い可能性」と WARN（fail-open なので CI は緑のまま） | `$exempt` へ移し、**なぜメジャーで測れないのか**を理由として書いた（`github/codeql-action` と同じ扱い） |
| 🟡 `docs/ai-workflow.md:102` | 生きた文書の表が `@v1` のまま。**次に読む人へ浮動タグへ戻す動機を与える** | SHA 固定である旨と、機械検査の所在を書いた。trace ブロックへ `#1352` を足した |

🟢 の指摘（同じファイルを 2 度読んでいる）も直した —— 走査ループで読んだ本文を溜めて渡す形にした。

🔴 **私の母集合の引き方が足りなかった。** 「`anthropics/claude-code-action` を**参照している**箇所」で
`.github/workflows/` だけを走査し、**その参照の版を前提にしている箇所**（表・手順書）を引かなかった。
規則 10 が言う「是正前の語で引いても捕まらない」がそのまま起きている。

## 受け入れ基準

- [x] 2 か所とも SHA で固定されている
- [x] 版が割れたら機械が止める
- [x] 浮動参照に戻したら機械が止める
- [x] `check-review-verdict.js`（判定なしを緑にしない門）は**緩めていない**

## 🔴 実装 ADR を置いていない理由（記録）

決定 1〜3 は IADR に値するが、**採番が直列化している** ——
`IADR-0417`（#1350）と `IADR-0418`（#1346）が未マージであり、
本 PR で `IADR-0419` を足すと `check-adr-numbering` が「欠番」で落ちる。
**本 PR は他の全 PR を解放するために先に出す必要がある**ため、決定は本仕様書と issue に置いた。
番号が空くのを待って IADR へ昇格させるかは、後続の棚卸しで判断する。

## 変えていないもの

- `claude_args` / `--allowedTools` / モデル選択・プロンプト。**1 バイトも触っていない。**
- `check-review-verdict.js` とその配線。
- action の入力・権限・トリガ条件。
