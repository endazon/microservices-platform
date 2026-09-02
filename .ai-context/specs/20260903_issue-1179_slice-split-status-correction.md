---
title: 「操作単位のスライス分割はまだ行っていない」を実測に合わせて反転させる（issue #1179）
type: spec
status: draft
created: 2026-09-03
updated: 2026-09-03
author: claude
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-application-structure.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_slice-composition-point.md
related_ids:
  - NFR
  - ADR-0065
  - ADR-0068
  - IADR-0282
  - IADR-0319
  - IADR-0334
---

# 仕様書: スライス分割の現況記述を実測へ追随させる

## 起点となる計画書（トレーサビリティ）

- 非機能要求（NFR）: 文書統制（実装の現況記述が実測と一致していること）
- 計画 ADR: `ADR-0065`（バックエンド適用構造・決定 2「1 ユースケースのファイルは操作フォルダへ束ねる」）、
  `ADR-0068`（スライスの合成点・決定 2「複数操作が使うものは集約直下に残す」）
- 実装 ADR: `IADR-0282`（単一プロジェクト VSA 構成。決定 4 が「移送波は器の移送まで」と定めていた）、
  `IADR-0319`（段は使う操作を数えて決める。#1062）、`IADR-0334`（テストの鏡写し先。#1063）

## 事象

`docs/tech/tech-requirements.md` 142〜143 行が

> なお**操作単位のスライス分割（`Features/<集約>/<操作>/` の 3 分割）はまだ行っていない** ——
> 器の移送までが移送波の射程であり、端点は集約フォルダ直下に 1 枚のまま置かれている。

と現況として述べている。これは **`IADR-0282` 決定 4 の時点（2026-08-28）の記述**であり、
その後 #1062 / #1065 / #1066 / #1093 / #1094 で 3 段への分割が実施されたため、**実測と逆**になっている。

## 母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 9・10）

🔴 **issue 本文の 2 行を転記せず、誤りの側の語で追跡下を走査して引き直した。**

### 走査の条件

- 走査語（誤りの側 ＋ 表記ゆれ）:
  `スライス分割` / `まだ行っていない` / `まだ行われていない` / `まだ実施していない` /
  `集約フォルダ直下` / `集約直下` / `3 分割` / `3分割` / `三分割` / `1 枚のまま` / `1枚のまま` /
  `操作単位の分割` / `未分割` / `分割していない` / `分割されていない` /
  `器の移送まで` / `移送波の射程` / `端点は集約` / `操作単位の(分割|スライス)` / `3 段への分割`
- 走査範囲: 追跡下の全ファイル（`docs/` / `src/**/README.md` / `src/**`（コード注釈含む） /
  `scripts/` / `docs/templates/` / `.github/`）。作業ツリーは clean であり追跡下と一致する。

### 除外と、その理由

| 除外先 | 理由 |
| --- | --- |
| `.ai-context/adr/` `.ai-context/specs/` `.ai-context/superpowers/` | **凍結記録**。当時の判断をそのまま残す（`CLAUDE.md` 「本文プロズを後から書き換えない」）。本件では**陽性対照**として使う |
| `CHANGELOG.md` | 生成物。手で書き足さない（`CLAUDE.md` 補助成果物の自動生成） |
| `src/ai-stock-trading/**` | 別リポジトリの submodule。本リポから変更できない |

### 走査結果

**live 文書での該当は 2 ファイル 4 行**である。

| ファイル | 行 | 内容 | 判定 |
| --- | --- | --- | --- |
| `docs/tech/tech-requirements.md` | 142-143 | 「スライス分割はまだ行っていない」「集約フォルダ直下に 1 枚のまま」 | **是正対象**（issue が挙げた 2 行） |
| `src/README.md` | 99-101 | 同じ主張（`IADR-0282` 決定 4 を出典に引く）＋「太いエンドポイントのハンドラ化…も別作業」 | **是正対象**（issue の宣言領域に無かった。走査で増えた） |

**誤検出（同語だが別主張。是正しない）**:

| ファイル | 行 | なぜ別主張か |
| --- | --- | --- |
| `docs/how-to/session-handoff.md` | 127 | 「#455 も 3 軸 ○ になったが、まだ分割していない」＝ **issue の子分割**の話 |
| `scripts/chunk-budget-baseline.json` | 132 | **バンドルのチャンク分割**の話 |
| `src/knowledge/frontend/src/features/sc07-conversions/components/ConversionJobsPage.tsx` | 47 | 「画面へ出す作業はまだ行っていない」＝ 別作業の話 |
| `src/**/Features/**/*Endpoints.cs` のコメント 6 件 ＋ `AuthzContracts.cs` | — | 「複数操作が使うため**集約直下に残す**」＝ **ADR-0068 決定 2 の正しい適用**であり、誤りではない |

### 陽性対照（「0 件」を「無い」と読み違えないための対）

**凍結記録の側には同じ主張がそのまま残っていること**を確認した。走査語が実際に当たることの証拠である。

- `.ai-context/specs/20260828_wave45-vsa-migration.md:142-143`
  「**操作単位のスライス分割**（…の 3 分割）はしていない。`IADR-0282` 決定 4 が「器の移送まで」と定めており、
  端点は集約フォルダ直下に 1 枚のまま。」

同一の走査語・同一の走査で live 側 2 件・凍結側 1 件が当たっている。したがって
「live 側の残存が是正後 0 件」は**走査が空振りしたのではなく、実際に 0 件**である。

## 実測（自分で取り直した。2026-09-03・base `66c316b7`）

```console
$ find src/platform/backend/Services src/knowledge/backend/Services -path '*/Features/*/*/Endpoint.cs' | wc -l
110
$ find src/platform/backend/Services src/knowledge/backend/Services -mindepth 4 -maxdepth 4 -type d -path '*/Features/*' | wc -l
155
$ find src/platform/backend/Services src/knowledge/backend/Services -mindepth 4 -maxdepth 4 -path '*/Features/*' -name '*.cs' ! -name '*Endpoints.cs' | wc -l
15
$ find src/platform/backend/Services src/knowledge/backend/Services -path '*/Features/*Endpoints.cs' | wc -l
24
```

- **3 段目（`Features/<集約>/<操作>/Endpoint.cs`）は 110 件**、操作フォルダは 155 件。
  3 段目の `.cs` は計 203 件で、内訳は `Endpoint.cs` 110 / `Command.cs` 11 / `Query.cs` 3 / `Handler.cs` 2 ＋
  テストとコンシューマ等。**`Command` / `Handler` までの 3 分割は一部にとどまる**（端点の 3 段化は完了）。
- **集約フォルダ（`Features/<集約>/`）直下に残る `.cs` は 15 件で、`Endpoint.cs` は 0 件**である。
  15 件は DTO 束・ストア・port・ホステッドサービス・共有ヘルパ（`AuthzContracts.cs` /
  `NotificationStore.cs` / `PrivateNoteUsage.cs` 等）＝ **ADR-0068 決定 2 が「集約直下に残す」と定めたもの**。
- **`*Endpoints.cs`（登録表）は 24 件**。うち 22 件が集約直下、2 件は操作フォルダの中
  （`LlmGateway/Features/Embeddings/Embed/EmbeddingEndpoints.cs` /
  `NotificationService/Features/Notifications/Accept/NotificationIngressEndpoints.cs`）。
  🔴 **issue 本文の `-maxdepth 6` は 3 段目の 2 件も拾っていた**ため「集約直下 24 件」は
  正しくは **22 件**である（結論は変わらない）。
- 登録表の中身を実見して確認した（`FeedbackEndpoints.cs` / `AuthzEndpoints.cs`）。いずれも
  `MapGroup` とタグ付け ＋ 各操作の `Map` 呼び出し ＋ 複数操作が共有する変換ヘルパだけであり、
  **端点の処理は 1 つも残っていない**。

**結論**: 「端点は集約フォルダ直下に 1 枚のまま」は**偽**である。端点は 3 段目へ降り、
集約直下に残るのは**複数操作が使う共有物と登録表**（ADR-0068 決定 2 の対象外）だけである。

🔴 **件数は本仕様書にだけ書き、`docs/` 本文には書かない**（腐る導出値。#1179 の指示）。

## やること

1. `docs/tech/tech-requirements.md` 142-143 行を「3 段への分割は完了、集約直下に残るのは
   複数操作が使う共有物と登録表」の向きへ書き換える。
   **表示テキストに計画 ID / IADR を書かず trace ブロックへ**（ADR-0048 決定 4）。
   `iadrs:` へ `IADR-0319` / `IADR-0334` を、`issues:` へ `#1062` `#1093` `#1179` を足す。`updated:` を前進させる。
2. `src/README.md` 99-101 行を同じ向きへ書き換える。**`src/` は `docs/` 配下ではないため
   本文に IADR を書いてよい**（trace ブロック規約の射程は `docs/` 配下のみ）。
3. `Command` / `Handler` までの 3 分割が一部にとどまることは**残った差分として明記する**
   （「完了」と書いて全部が終わったように読ませない）。

## やらないこと

- 件数を `docs/` 本文へ書くこと（腐る）。
- 凍結記録（`.ai-context/`）の書き換え。陽性対照であり、当時の判断として正しい。
- 誤検出 4 件の書き換え。別主張である。
- コード側の変更。本件は文書の追随のみ。
- 実装ADR の起草。**新しい実装判断は無い**（既決の `ADR-0065` / `ADR-0068` / `IADR-0319` の適用結果を写すだけ）。

## 受け入れ基準（Given-When-Then）

- [ ] Given `docs/tech/tech-requirements.md` / When 読む / Then スライス分割の現況が実測と一致し、
      表示テキストに計画 ID / IADR / 件数が無い
- [ ] Given `src/README.md` / When 読む / Then 同上（IADR は本文可）
- [ ] Given 誤りの側の語での走査 / When live 文書を見る / Then 同じ主張の残存が 0 件
      （陽性対照: `.ai-context/specs/20260828_wave45-vsa-migration.md` には残る）
- [ ] Given `node scripts/check-doc-updated.js` / `check-trace-blocks.js` / `check-doc-links.js` /
      `check-doc-type-vocabulary.js` / `gen-knowledge-graph.js --check` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / When 実行 / Then 緑

## 影響範囲（並列判定に使う宣言ファイル領域）

- `docs/tech/tech-requirements.md`
- `src/README.md`（走査で増えた。issue の宣言領域には無かった）
- `.ai-context/specs/20260903_issue-1179_slice-split-status-correction.md`（本書）
