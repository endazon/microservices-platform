---
title: 自動 CHANGELOG PR が必須 check 不起動で恒久 BLOCKED になる件の PAT をシークレットの正本へ記す
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs: []
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

## 🔴 起点 ID —— issue の件名が引く `ADR-0007` / `ADR-0008` は誤帰属である（引き継がない）

issue #1237 の件名は `chore(NFR,ADR-0007,ADR-0008)` だが、**この 2 つは本件と無関係である。**
計画側の実体を見て確かめた。

```console
$ ls ../project-planning/projects/microservices-platform/07_adr/ | grep -E "ADR-000[78]"
ADR-0007_cicd-gitops-argocd.md
ADR-0008_runtime-kubernetes-k3s.md

$ sed -n '/^## 決定/,/^## 理由/p' ADR-0007_cicd-gitops-argocd.md
GitOps を採用する。各サービスを Helm チャートで定義し、ArgoCD が宣言的にデプロイ・同期・
ロールバックする。コンテナは Harbor レジストリで管理する。
```

- `ADR-0007` は **GitOps（ArgoCD + Helm + Harbor によるコンテナ配布）** であり、
  GitHub Actions のワークフロー間トークン権限とは別の対象である
- `ADR-0008` は **k3s 実行基盤**である。`ADR-0008_branch-strategy.md` という文書は**実在しない**
  （本リポジトリ内の 30 件超の記録はすべて `ADR-0008` を「k3s 実行基盤」として引いている）

```console
$ grep -rln "ブランチ戦略" ../project-planning/projects/microservices-platform/07_adr/
（0 件）
$ grep -rln "必須チェック|GitHub Actions" ../project-planning/.../07_adr/
ADR-0031_frontend-stack.md  ADR-0055_git-hooks-single-source-of-truth.md  README.md
→ いずれも本件（自動 PR のトークン権限）を扱っていない
```

**したがって起点は `NFR`（無採番）1 つとする。**
`traceability.md` §起点 ID の種別 が無採番 `NFR` を許す 2 つの場合のうち、**ケース 2 に当たる** ——
「ID 列はあるが、その作業に当たる番号が無い場合」であり、**規約整備・文書統制といったメタ作業が典型**である。
同規約は **ケース 2 では環流しない**とも定めている（計画側の非機能要件は稼働する製品の要件であり、
工程の管理は別の軸だから）。したがって本件で planning へ ID 付与を求めることはしない。

🔴 **無理に近い番号を付けない**という同規約の戒めがそのまま当たる ——
実在しない対応づけを作ると、**監査が「その ADR の実装」として数えてしまい、無採番より劣化する。**

同じ topic（必須チェックの不起動と BLOCKED の記録）を扱う先行記録
[`IADR-0182`](../adr/IADR-0182_required-check-contexts-and-blocked-record.md) も
`related_ids` に `NFR` だけを置いており、計画 ADR を引いていない。**同じ形に揃える。**

## 母集合（規則 9。記憶で挙げず、PAT のシークレット名で全追跡ファイルを走査した）

基点 `origin/develop` `3663b2ba`（`git rev-parse --is-shallow-repository` = `false`）。

```console
$ git grep -n "AUTOMATION_PR_TOKEN" 3663b2ba
3663b2ba:.ai-context/specs/20260801_impl-handoff-kit-sync.md:85
3663b2ba:.github/workflows/changelog.yml:52
3663b2ba:.github/workflows/changelog.yml:61
→ 3 行 / 2 ファイル
```

**陽性対照**（走査器が生きていることの確認）:
同じ `git grep -n` は `CLAUDE_CODE_OAUTH_TOKEN` を `AI_SETUP.md` 内で複数行ヒットさせる。

### 3 行の判定と除外理由

| 行 | 判定 | 理由 |
| --- | --- | --- |
| `.github/workflows/changelog.yml:52` | **触らない** | 回避策 (a) を説明するコメント。既に正しい |
| `.github/workflows/changelog.yml:61` | **触らない** | 実際の参照。フォールバックも実装済み。**触ると陰性対照が壊れる** |
| `.ai-context/specs/20260801_impl-handoff-kit-sync.md:85` | **触らない** | **確定済みの凍結記録**（`.ai-context/README.md`）。当時のキット同期の記録であり、本文プロズを後から書き換えない |

→ **追記先は `AI_SETUP.md` 1 か所。**
規則 10（是正で新たに誤りになる自分の記述が無いか引き直す）を適用した結果、**新たに誤りになる記述は無い**。

## 対象範囲

- 対象: `AI_SETUP.md` §3「共通（どのプロファイルでも実施）」へ自動 PR 用 PAT の項を足す
- 対象外:
  - `.github/workflows/changelog.yml`（既に正しい。フォールバックも実装済み）
  - `.ai-context/specs/20260801_impl-handoff-kit-sync.md`（凍結記録）
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
      PR 作成自体は壊れない（`changelog.yml:61` のフォールバック式を**変えていない**ことで示す）

## テスト方針

機械検査は置かない。`check-ai-workflow-config.js` はツール許可の 3 系統同期を見るものであり、
シークレットの在否は見ない。**在否を見る検査器を足すと「未登録＝赤」が常態化する** ——
上流ガイド §4 の「CI の赤を常態化させない」に反する（赤が常態化すると、毎回「本物の失敗か」の
切り分け費用が発生し、監査ゲートの意味が失われる）。

## 計画書との差異

- 差異: **あり。** issue #1237 の件名が引く `ADR-0007` / `ADR-0008` は本件と主題が一致しない
  （内容・対応: 上記「起点 ID」節のとおり `NFR` 単独へ改めた。**計画書自体に誤りは無く、
  誤っていたのは実装側の引用**なので planning への環流は行わない）

## 未決事項

- **回避策 (a) が採れない場合**（利用者が PAT を発行しない判断をした場合）、
  (b)（CHANGELOG 更新を PR にせず develop へ直接 push）の実装可否は
  **ブランチ保護の例外をどう置くかという運用の裁定**である。本 PR では判断しない。
