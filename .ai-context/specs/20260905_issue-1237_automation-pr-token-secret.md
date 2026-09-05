---
title: 自動 CHANGELOG PR が必須 check 不起動で恒久 BLOCKED になる件の PAT をシークレットの正本へ記す
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - ADR-0008
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007_ci-cd-pipeline.md
  - planning:projects/microservices-platform/07_adr/ADR-0008_branch-strategy.md
---

# 作業仕様書: 自動 PR 用 PAT をシークレットの正本へ記す（#1237）

## 背景

`GITHUB_TOKEN` で作成した PR では GitHub の仕様によりワークフローが自動起動しない（再帰実行の防止）。
必須チェックを設定している本リポジトリでは、自動生成の CHANGELOG 更新 PR が
`statusCheckRollup` 0 件のまま **恒久的に `BLOCKED`** になる。
`.github/workflows/changelog.yml:45-56` の「【既知の制約・重要】」がこの事象と回避策 3 つを自ら書いている。

回避策 (a)（`token:` に fine-grained PAT を設定する）は**ワークフロー側が既に実装済み**である ——
`changelog.yml:61` が PAT を先に見て未設定なら `GITHUB_TOKEN` へ落ちる `||` の形で書いており、
**シークレットを登録するだけで有効になる**。

🔴 **シークレットの登録は AI にはできない**（値を扱えず、リポジトリ設定も変えられない）。
本作業の射程は **AI 側でやれること 1 つ**、すなわち
「`AI_SETUP.md`（シークレットの正本）へ本 PAT の用途・必要権限・未設定時の挙動を追記する」だけである。

## 母集合（規則 9。PAT のシークレット名で全追跡ファイルを走査した）

基点 `origin/develop` `3663b2ba`（`git rev-parse --is-shallow-repository` = `false`）。

```console
$ git grep -n "AUTOMATION_PR_TOKEN"
.github/workflows/changelog.yml:52   ← 回避策 (a) の説明コメント
.github/workflows/changelog.yml:61   ← 実際の参照（フォールバック付き）
→ 2 行 2 箇所。いずれも同一ファイル

$ git grep -ln "シークレット" -- ':!.ai-context' ':!CHANGELOG.md' ':!src/ai-stock-trading'
AI_SETUP.md            ← 正本（CLAUDE.md 冒頭が「シークレットの正本」と宣言している）
.claude/settings.json  ← 機密の deny 規則。シークレットの登録先の一覧ではない
docs/ai-workflow.md    ← 運用の正本。シークレットの一覧は持たない
（ほか）
```

**陽性対照**（「2 箇所しか無い」を「無い」と読む前に走査器が生きていることを確かめた）:
同じ `git grep -n` は `CLAUDE_CODE_OAUTH_TOKEN` を `AI_SETUP.md` 内で複数行ヒットさせる。

→ **追記先は `AI_SETUP.md` の §3 共通 1 か所。**
`changelog.yml` のコメントは既に正しいので触らない
（規則 10: 是正で新たに誤りになる自分の記述が無いか引き直した結果、無い）。

## 対象範囲

- 対象: `AI_SETUP.md` §3「共通（どのプロファイルでも実施）」へ自動 PR 用 PAT の項を足す
- 対象外:
  - `.github/workflows/changelog.yml`（既に正しい。フォールバックも実装済み）
  - 回避策 (b)（develop へ直接 push）／(c)（毎回手動承認）の実装 —— (a) が採れない場合の**運用の裁定**が要る
  - シークレットの登録そのもの —— **利用者の手が要る**

## 設計

`AI_SETUP.md` §3「共通（どのプロファイルでも実施）」は、**プロファイル（AI のライセンス）に依らず
必要な設定**を並べた節である。本 PAT は AI のライセンスと無関係で、
**ブランチ保護と自動 PR の噛み合わせ**の問題なので、プロファイル別の節ではなくここに置く。

記す内容は 4 つ —— **用途 / 必要権限 / 未設定時の挙動 / 登録しない場合の代替**。
🔴 **値そのものは書かない**（`AI_SETUP.md` は登録手順の正本であって値の置き場ではない）。

## 受け入れ基準

- [x] `AI_SETUP.md` に本 PAT の記述がある（用途・必要権限・未設定時の挙動）
- [ ] Given PAT のシークレットが登録されている / When develop へ push して CHANGELOG PR が作られる /
      Then 必須 8 check が起動し、**保護を迂回せずに**マージできる
      → 🔴 **利用者がシークレットを登録するまで測れない。本 PR では未達のまま残す。**
- [x] Given 陰性対照 / When シークレットが無い / Then 従来どおり `GITHUB_TOKEN` へフォールバックし、
      PR 作成自体は壊れない（`changelog.yml:61` のフォールバック式を変えていないことで示す）

## テスト方針

機械検査は置かない。`check-ai-workflow-config.js` はツール許可の 3 系統同期を見るものであり、
シークレットの在否は見ない。**在否を見る検査器を足すと「未登録＝赤」が常態化する** ——
上流ガイド §4 の「CI の赤を常態化させない」に反する（赤が常態化すると、毎回「本物の失敗か」の
切り分け費用が発生し、監査ゲートの意味が失われる）。

## 計画書との差異

- 差異: なし

## 未決事項

- **回避策 (a) が採れない場合**（利用者が PAT を発行しない判断をした場合）、
  (b)（CHANGELOG 更新を PR にせず develop へ直接 push）の実装可否は
  **ブランチ保護の例外をどう置くかという運用の裁定**である。本 PR では判断しない。
