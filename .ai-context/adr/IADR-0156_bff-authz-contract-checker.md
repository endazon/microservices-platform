---
title: IADR-0156 BFF の実効ロールは openapi.yaml の x-roles と機械的に突き合わせる
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0044
  - IADR-0116
  - IADR-0128
  - IADR-0140
  - IADR-0141
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0156: BFF の実効ロールは `openapi.yaml` の `x-roles` と機械的に突き合わせる

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR**（契約の正しさ）。実装 issue: **#647** ／ 作業仕様書: [20260810_issue-647](../specs/20260810_issue-647_openapi-role-checker.md)
- 事故: **#629**（認可を狭めたが `openapi.yaml` が追随せず）→ **#640 の走査で偶然発見**

## コンテキストと課題

`BFF_bff-surface.md` は「個々のエンドポイントの…ステータスは `openapi.yaml` を正とする」と定める。
**その「正」が誤ったまま残る事故が 2 回起きた。** #629 は **3 巡の AI レビューを経ても捕まらなかった。**

`.claude/rules/traceability.md` は既に規則 4「行フィルタで絞らない。パスから引く」を持ち、
先例 #593 は *`openapi.yaml` が 2 段目の grep で落ちた*という**同じ機構**である。
**規則の不足ではなく不遵守**であり、`CLAUDE.md`「同型の事故が 2 回起きたら」に照らして
**3 本目の規約ではなく検査器で止める。**

## 決定

### 決定 1: 突き合わせる相手は**散文ではなく `x-roles`**（機械可読な宣言を 1 つ置く）

`summary` / `description` / `403` の散文からロール集合を推定する案は**捨てた**。実データに

```yaml
"403": { description: 管理者ロール以外（IADR-0128 決定 1。運用者も拒否される） }
```

があり、**「管理者」と「運用者」の両方を含みながら意味は admin のみ**である。
**語を数える判定はここで必ず誤る。**

よって OpenAPI 拡張フィールド **`x-roles`** を各 `/bff/` 操作へ置き、**それを正**とする。
散文は解説として残るが、**機械が見るのは `x-roles` 1 点**である（[IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)「参照点を 1 つに畳む」）。

**`x-roles` を持たない `/bff/` 操作は fail** にする —— 新しい端点が黙って素通りしないため。

### 決定 2: 実装側は **AND 合成の実効ロール**を出す

認可メタデータは AND 合成される（[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1）。群が admin ＋ operator で端点が
`AdminOnly` なら**実効は admin のみ**であり、`x-roles` にはこの**積**を書く。

**ポリシー名 → ロールの対応は `AuthExtensions.cs` から引く**（値を焼き付けない）。
`AdminOnly` の定義が変われば判定も自動で追随する。

### 決定 3: 認可は **`RequireAuthorization` だけでは足りない** —— ハンドラ内判定も 1 段たどる

`ConfigBffEndpoints` は **`RequireAuthorization` を意図的に付けず**、ハンドラ内で
`AuthorizeAsync(user, ConfigViewer)` を呼んで **404 で存在を秘匿**する（[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)。
`RequireAuthorization` だと無認証が 404 到達前に 401 で短絡し、存在が漏れる）。

**ミドルウェアを使っていないだけで、ロール制約は在る。**
よって**同一ファイルの private ヘルパを 1 段だけたどり**、`AuthorizeAsync(..., ポリシー)` を
実効ロールへ AND 合成する。

> **［経緯］この決定は PR #653 のレビュー 1 巡目（🔴）を受けて足した。**
> 当初は「`RequireAuthorization` が無い＝ロール制約なし」と判定し、`x-roles: []` を
> **「正」として `openapi.yaml` へ確定させるところだった**。
> **本 ADR が防ごうとしている事故を、検査器が検出できない形で新規に作り込む**ことになる。
> **値だけ直さず検査器を直した** —— 値を直すだけでは、次に同じ形が来たときまた人が気づくしかない。

**2 段以上の委譲は追わない**（冒頭コメントに開示済み）。追うなら別 issue とする。

### 決定 4: 実装の走査は **文単位**（`;` まで）で行い、群は**変数名**で辿る

実データで、素朴な実装なら壊れる 4 点を確認した:

| # | 実測 | 素朴な実装が壊れる理由 |
| --- | --- | --- |
| 1 | `/bff/documents` と `/bff/tags` は**同じパス接頭辞に群が 2 つ**（読み取り／書き込み） | パスでは群を決められない。**変数名で辿る** |
| 2 | 群の認可が `MapGroup` と**別行**（`ConversionBffEndpoints`） | 行単位の正規表現では取れない |
| 3 | 端点の認可が `.WithName(...)` の**後ろ**（`DashboardBffEndpoints` は群に認可が無い） | 「群に無ければ無認可」は誤り |
| 4 | 認可の形が **2 種**（`AdminOnly` ポリシー名 / `RequireRole` ラムダ） | 片方だけでは実効ロールを誤る |

**コメントは先に落とす**（`;` や `"` を含むコメントで文の切り出しが壊れるため）。
経路制約（`{id:guid}`）は openapi の表記（`{id}`）へ**実装側を寄せて**正規化する
——OpenAPI に ASP.NET の経路制約構文は無いため、逆向きには寄せられない。

### 決定 5: **allowlist を持たない**

偽陽性は**判定条件の側**で扱う。allowlist は事故を隠し、検査器が形骸化する
（[IADR-0155](./IADR-0155_doc-updated-staleness-checker.md) 決定 3 で学んだ「検査器が外される」形の別バージョンである）。
**allowlist を参照していないことを回帰テストで固定した。**

### 決定 6: CI 配線は `scripts-tests` への相乗り

`.github/workflows/` は GitHub App 権限で編集できない。[IADR-0140](./IADR-0140_cross-repo-issue-ref-checker.md) 決定 2 が確立し
#649 でも踏襲した相乗りに載せる。**新ジョブは作らない。**

## 根拠 / 代替案

- **散文の解析**（決定 1 の代替）: 上記のとおり実データで必ず誤る。
- **Roslyn による構文解析**（決定 3 の代替）: 正確だが `scripts/` は **Node 標準のみで動く**方針
  （`check-doc-links` と同じ）。C# ツールチェーンへの依存を CI の文書系ジョブへ持ち込まない。

## 影響

- `scripts/check-bff-authz-docs.js`（新設）と `scripts/scripts.repo.test.js` の自己試験。
- `docs/api/openapi.yaml` の `/bff/` **49 操作**へ `x-roles` を追加。
- **検出しないこと**（散文の誤り・後段サービスの認可・`RequireAuthorization` 以外の判定・
  `src/ai-stock-trading`）は検査器の冒頭コメントに開示した。

## フォローアップ

- **後段サービス（`*Endpoints.cs`）の認可**は本 ADR の射程外。[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) の多層防御のうち
  BFF 層だけを見る。広げるなら別 issue（[IADR-0116](./IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。
