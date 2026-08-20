---
title: 作業仕様書 — 計画側の裁定が反映されたことを検知する経路を作る（#589）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0119
  - IADR-0142
  - IADR-0170
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - planning:projects/microservices-platform/07_adr/README.md
---

# 作業仕様書: 計画 pin の鮮度検知（#589）

## 起点

- **NFR**（着手ゲートの検知）。起点 issue: **#589**（出所は #572 の施策 7。親 #454）
- 制約: **[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)**（着手条件の**一部**が「前提 ADR が `Accepted`」。**全部ではない**）
  ／ **[IADR-0142](../adr/IADR-0142_fr19-20-scoped-release-by-overturn-range.md)**（**IADR-0119 決定 2 の FR-19 / FR-20 を部分改定**。
  「前提検証の完了」は着手条件から外れ、**範囲基準**へ変わった）／ IADR-0115 分類 A
- 実装 ADR: **[IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md)**
- 先例: **#507**（ワークフローを触らず既存呼び出し口へ相乗りする経路）

> **★ 件数・SHA の基準時点は develop（2026-08-11 実測）である。**

## ★★ 母集合 —— 3 軸で引いた

### 軸 a: 規約・計画書の現状

**★ 今まさに乖離している。フィクスチャを作らなくても実データで検出を確かめられる。**

```console
$ git submodule status planning
 2cf0795… planning (2cf0795)
$ git -C planning log --oneline -1 origin/HEAD
14aed71 docs: 00_vision / 01_problems を fixed へ確定する (#308) (#310)
$ git -C planning log --oneline 2cf0795..14aed71 | wc -l
3
```

**着手可否に効く差分（実測）:**

| 種類 | 実体 |
| --- | --- |
| **ADR の `status` 変化** | `ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md`: **`Proposed` → `Accepted`**（1 件） |
| 要求の変更 | `02_requirements/01_requirements.md` |
| 画面の変更 | （この差分には無し） |
| その他 | `00_vision` / `01_problems` が fixed へ、環流 6 件、`tools/impl-handoff-kit/` |

### ★ 軸 b: **`#589` を全数で引く**

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#589'
docs/how-to/session-handoff.md                        … F 群の一覧（実装指示なし）
docs/specs/20260807_issue-599_planning-pin-fr22.md    … pin 更新の記録
docs/specs/20260808_planning-pin-adr-0006-alerting.md … pin 更新の記録
```

**実装指示は無い。** 3 件とも「未了の課題」または過去の pin 更新の記録である。

### 軸 c: 対象の実データ —— **既にある部品**

| 部品 | 所在 | 使えるか |
| --- | --- | --- |
| populate 判定の作法 | `check-doc-links.js:57` `planningPopulated()`（`planning/projects` の実在で判定） | **そのまま踏襲する** |
| `--require-planning` の作法 | 同 `:288`（未 populate なら fail） | **踏襲する** |
| **トークン付きで planning を populate する夜間ジョブ** | `.github/workflows/doc-links-planning.yml` | **★ 相乗り先になる** |
| **失敗 → issue 起票の導線** | 同ジョブの `Notify on failure`（既存 open issue があれば comment、無ければ 1 件だけ create） | **★ 型をそのまま使える** |
| SessionStart hook | `.claude/settings.json:142-148` → `scripts/setup.sh` | **呼び出し口として使える** |

## ★★ #589 の前提のうち、誤っているもの

### **誤り: 「`.github/workflows/` は GitHub App 権限で編集できない」**

#589 は「**CI 結線の制約**」としてこう書き、**案 1〜3 をその制約の下で並べている。**
**実測すると、`.github/workflows/` は繰り返し変更されマージされている。**

```console
$ git log --oneline -3 -- .github/workflows/
4dbd501 chore(NFR): 2 回持ち越された負債を回収する（… frontend-tests.yml の paths） (#671)
ce96eb8 chore(NFR): フロントエンドに prettier の format ゲートを新設し CI へ結線する (#619)
44a3141 feat(NFR,IADR-0134,IADR-0147): manualChunks の規則構成を守る機械検査を新設し CI へ結線する (#618)
```

> **★ これは #583 / [IADR-0169](../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md) で
> 直したばかりの前提と同じものである。** IADR-0140 決定 4 も「編集不可だから対象外」で
> 1 件を残していた。**同じ誤った前提が 2 つの issue の設計を歪めていた。**

**したがって #589 が挙げた案 1〜3 に加え、案 4 が成立する。**

### 案の再評価

| 案 | 内容 | 判定 |
| --- | --- | --- |
| 案 1 | pin の古さ（日数・コミット数）だけを見る | **不採用**。「重要な裁定か否か」を判別できず、**pin が古いだけで鳴り続ける**（狼少年になる） |
| 案 2 | pin を進める PR のときだけ中身を検査 | **不採用**。**検知したい場面（pin が古いまま放置）で走らない** |
| 案 3 | セッション開始時（`setup.sh`）に警告 | **採用（従）**。ローカルで確実に目に入る |
| **案 4** | **既存の夜間 populate ジョブへ相乗りし、乖離を検知したら issue を起票する** | **採用（主）** |

## ★ 判断

### 判断 1: **主は案 4 —— 夜間ジョブから issue を起票する**

**#589 は「判定は警告であって失敗ではない（赤にすると pin が古い間ずっと CI が止まる）」と書いた。正しい。**
**だが夜間ジョブで「警告」を出すだけなら、それは誰も読まないログである** ——
`doc-links-planning.yml` 自身が「**導線が無いと『誰も見ない赤』が常態化する**」と警告している。

**通知手段を「赤」ではなく「issue」にする。**

- **CI は落とさない**（`continue-on-error` 相当。pin が古い間ずっと止まることはない）
- **乖離を検知したら issue を起票する**。既に open の検知 issue があれば**起票せず comment**（既存の型）
- **★ #548 / #560 / #589 はいずれも人が気づいて起票した issue である。** 3 回とも同じ手作業をしている。
  **自動化するのは「検知」ではなく、その手作業そのものである。**

### 判断 2: **従は案 3 —— `setup.sh` から警告を出す**

**ネットワーク・認証に依存するため、必ず fail-open にする**（タイムアウト付き・失敗しても `exit 0`）。
**セットアップを壊さない**ことを、pin 検査より優先する。

### 判断 3: **「着手可否に効く差分」だけを鳴らす。全部の差分では鳴らさない**

**鳴りすぎると読まれなくなる。** 検知の条件を絞る:

| 鳴らす | 鳴らさない |
| --- | --- |
| `07_adr/ADR-*.md` の **`status:` の変化**（とくに `Proposed` → `Accepted`） | 本文だけの変更 |
| ★ ただし**「Accepted になった」は「着手できる」ではない** —— IADR-0119 は両者が別だと明記し、**一括りにした誤りが実際に起きた**と記録している。検知器は**事実だけを言い、判断はしない** | |
| ★ **対象は `projects/microservices-platform/` に限る** —— 計画リポには AST・mondriq も同居する（[IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md) 決定 5） | 他プロジェクトの ADR・要求 |
| `02_requirements/*.md` / `05_screens/*.md` の変更 | `draft/` `tools/` `INDEX.md` `README.md` |

**実測の差分（軸 a）はこの条件で 2 件鳴る**（ADR-0023 の status 変化・要求の変更）。

### 判断 4: **検知できない場面を明記する。黙って緑を返さない**

| 場面 | 挙動 |
| --- | --- |
| planning が未 populate（PR CI） | **検査せず、その旨を出して exit 0**（fail-open）。**「検査した」と書かない** |
| planning の既定ブランチを fetch できない | 同上 |
| pin == HEAD | **「乖離なし」と出す**（偽陽性 0 の側の実測） |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#589） | 確かめ方 |
| --- | --- | --- |
| 1 | pin が古く ADR の `status` が変化している状態を**再現して検出できることを実測** | **実データで確かめる**（軸 a。`2cf0795` → `14aed71` に ADR-0023 の `Proposed` → `Accepted` が実在する） |
| 2 | pin が最新のときに**警告が出ない**（偽陽性） | pin == HEAD を与えて 0 件になることを確かめる |
| 3 | 検知結果が**セッション開始時または CI のいずれかで実際に人の目に入る** | 判断 1（夜間 → issue）＋ 判断 2（`setup.sh`）。**★ ワークフローの実走は確かめられない**（後述） |
| 4 | どの案を採ったか根拠が実装 ADR に残っている | **IADR-0170**（本 PR で新設）。**案 1〜3 の前提が誤っていたことも書く** |

### 変異試験

| # | 壊す門 | 期待 |
| --- | --- | --- |
| M1 | ADR の `status` 変化の検出 | 実データで 1 件出ることを固定する |
| M2 | 「着手可否に効く」の絞り込み（判断 3） | `draft/` だけの差分では鳴らないことを固定する |
| M3 | populate 判定（判断 4） | 未 populate で**緑を返しつつ「検査していない」と出る**ことを固定する |
| M4 | 0 件走査の門 | 比較対象を 1 件も拾えないときに黙って緑を返さない |
| **M5** | **気付き導線が「最初の 1 回」で壊れないか** | `--jq` から `// empty` を外すと、全ワークフロー走査の回帰テストがファイル名を名指しして落ちる |
| **M6** | **その門自体に穴が無いか** | **二重引用符の欠陥形**（`--jq ".[0].number"`）へ変えても落ちる。初版の門は単一引用符しか見ておらず、この形を取り逃していた |

## ★ 限界（確かめられないこと）

- **夜間ワークフローの実走は確かめられない。** GitHub Actions をこの環境から起動できない。
  **確かめたのは「スクリプト側が実データで正しく鳴ること」までである。**
  ワークフロー側は既存 `doc-links-planning.yml` と同じ型（populate ＋ `gh issue`）を踏襲した、とだけ書く。
  **「配線した」と「配線が働いている」を読み分けられるように書く。**

## 射程外

- **pin を実際に進めること** —— 検知の issue であって、更新は別作業（#586 / #599 の型）。
  **本 PR では pin を動かさない。**
- 計画側の文書そのものの変更 —— `planning/` は **pin 更新のみ**。
