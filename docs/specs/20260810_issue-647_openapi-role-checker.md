---
title: 作業仕様書 — openapi.yaml が宣言するロールと実装の RequireAuthorization を突き合わせる検査器（#647）
type: work-spec
status: draft
related_ids:
  - NFR
  - IADR-0044
  - IADR-0116
  - IADR-0128
  - IADR-0140
  - IADR-0155
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "./20260810_issue-649_doc-updated-checker.md"
  - "./20260809_issue-629_document-write-admin-only.md"
---

# 作業仕様書: 宣言ロールと実効ロールの突合検査器（#647）

## 起点

- **NFR**（契約の正しさ・トレーサビリティ）。起点 issue: **#647**
- 事故: **#629**（PR #645）で認可を狭めたのに `openapi.yaml` が追随せず、**#640 の走査で偶然発見**した
- **#629 は 3 巡の AI レビューを経ても捕まえられなかった**

`docs/api/BFF_bff-surface.md` 自身が「個々のエンドポイントの要求・応答・ステータスは
`openapi.yaml` を正とする」と明記している。**その「正」が誤ったまま残る**のが本件である。

### なぜ規約では止まらないか

`.claude/rules/traceability.md` は既に**規則 4「行フィルタで絞らない。パスから引く」**を持ち、
先例 **#593** は *`openapi.yaml` が 2 段目の grep で落ちた*という**まったく同じ機構**である。
**規則の不足ではなく不遵守**であり、`CLAUDE.md`「**同型の事故が 2 回起きたら**」に照らして
**規約ではなく検査器の側で止める**（3 本目の規約を足しても同じことが起きる）。

## 母集合（着手時に自分で引いた。issue 本文は転記していない）

```console
$ find src -path '*/Bff/*' -name '*BffEndpoints.cs' -not -path '*/ai-stock-trading/*'
```

→ **10 ファイル**（knowledge 8・platform 2）。**`src/ai-stock-trading` は別プロジェクトなので除外**。

### ★ 実測で分かった、素朴な実装では壊れる 4 点

| # | 実測 | 素朴な実装が壊れる理由 |
| --- | --- | --- |
| 1 | **同じパス接頭辞に群が 2 つある** —— `/bff/documents` は読み取り群（`g`）と書き込み群（`write`）、`/bff/tags` も `read` / `write` | **パスだけでは群を決められない。変数名で辿る必要がある** |
| 2 | **群の認可は `MapGroup` と別行**（`ConversionBffEndpoints.cs:33` の群に対し `:35` が `RequireAuthorization`） | 1 行正規表現では取れない。**`;` までを 1 文として読む** |
| 3 | **端点の認可は `.WithName(...)` の後ろに来ることがある**（`DashboardBffEndpoints.cs:77-80`。群 `:21` には認可が無く、**端点側だけに載っている**） | 「群に認可が無ければ無認可」と判定すると誤る |
| 4 | 認可の形が **2 種**ある —— `RequireAuthorization(PlatformAuthPolicies.AdminOnly)` と `RequireAuthorization(p => p.RequireRole(AdminRole, OperatorRole))` | 片方だけ見ると実効ロールを誤る |

### 実効ロールの定義（[[IADR-0128]] 決定 1）

認可メタデータは **AND 合成**される。群が `admin ＋ operator`、端点が `AdminOnly` なら**実効は admin のみ**。
本検査器は**この積を出す**。

## 判定式

| 実効ロール（実装） | 文書の主張 | 判定 |
| --- | --- | --- |
| admin のみ | admin ＋ operator | **fail**（#629 の事故そのもの） |
| admin ＋ operator | admin のみ | **fail**（逆向き。狭く書いて画面を作ると使えない機能ができる） |
| 一致 | — | pass |
| 文書がロールに言及していない | — | **pass**（notice）。「全端点にロールを書け」は別の規約であり本検査器の射程ではない |

**allowlist は作らない**（受け入れ基準）。偽陽性は**判定条件の側**で扱う。
allowlist は事故を隠し、[[IADR-0155]] 決定 3 で学んだ「検査器が外される」形の別バージョンになる。

## 実装方針

### 実装側の抽出は **`;` までを 1 文として読む**

行単位の正規表現ではなく、`MapGroup` / `Map<Verb>` から次の `;` までを 1 文として切り出し、
その中の `RequireAuthorization(...)` を拾う（実測 2・3 への対応）。
**変数名 → (パス接頭辞, 群ロール) の表**を作り、`<変数>.Map<Verb>("...")` を引くときに引き当てる（実測 1）。

### 文書側は **ロールを表す語**を拾う

`summary` / `description` / `403` の記述から `platform-admin` / `platform-operator` /
`管理者` / `運用者` / `AdminOnly` を拾う。**群のブロックコメントは openapi.yaml の対象外**
——今回落ちたのは `openapi.yaml` のパス直上コメントなので、**YAML のコメント行も読む**。

> **★ ここが本件の肝である。** #645 で落ちたのは `/bff/documents:` の**直上のブロックコメント**であり、
> `document` も `文書` も含まないため語では引けなかった。**構造（どのパスの直上か）で引く。**

### CI 配線は **`scripts-tests` への相乗り**

issue は「CI の `lint` 相当ジョブへ載せる」と書いているが、**`.github/workflows/` は
GitHub App 権限で編集できない**。[[IADR-0140]] 決定 2 が確立し、#649（PR #652）でも踏襲した
**`scripts/scripts.repo.test.js` からの相乗り**に載せる。**新ジョブは作らない。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 実装と文書の一致を機械的に確認できる | 実データで exit 0 |
| 2 | **文書を admin ＋ operator へ戻すと fail** | 変異試験（純関数へ同義入力＋実データ変異の両方） |
| 3 | **実装から `AdminOnly` を外しても fail** | 同上（両方向） |
| 4 | 実測 1〜4 の構造で誤らない | 4 点それぞれに固定テスト |
| 5 | 偽陽性を allowlist で隠していない | allowlist ファイルを持たないこと |

**変異試験は #649 の教訓に従い、対象になり得る母集合全体（BFF 10 ファイル）へ当ててから確定させる。**

## 申し送り

- **[[IADR-0044]]（多層防御）の後段側**（`*Endpoints.cs` のサービス実装）は**本 issue の射程外**。
  BFF と文書の突合に閉じる。後段まで広げるなら別 issue（[[IADR-0116]] 規約 4）。
- 判定式・除外の根拠は**実装時に IADR として起こす**（#649 のレビューで「同種の検査器はいずれも
  専用 IADR を持つ」と指摘され、[[IADR-0155]] を起こした先例に従う）。
