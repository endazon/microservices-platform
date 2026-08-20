---
title: 作業仕様書 — openapi.yaml が宣言するロールと実装の RequireAuthorization を突き合わせる検査器（#647）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0044
  - IADR-0116
  - IADR-0128
  - IADR-0140
  - IADR-0155
  - IADR-0156
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
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

### 実効ロールの定義（[IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1）

認可メタデータは **AND 合成**される。群が `admin ＋ operator`、端点が `AdminOnly` なら**実効は admin のみ**。
本検査器は**この積を出す**。

## 判定式

| 実効ロール（実装） | 文書の主張 | 判定 |
| --- | --- | --- |
| admin のみ | admin ＋ operator | **fail**（#629 の事故そのもの） |
| admin ＋ operator | admin のみ | **fail**（逆向き。狭く書いて画面を作ると使えない機能ができる） |
| 一致 | — | pass |
| 文書に `x-roles` が無い | — | **fail**（**［実装時に変更］**。当初は pass（notice）としていたが、それでは**新しい端点が黙って素通りする**。§実装中に決めたこと 1） |

**allowlist は作らない**（受け入れ基準）。偽陽性は**判定条件の側**で扱う。
allowlist は事故を隠し、[IADR-0155](../adr/IADR-0155_doc-updated-staleness-checker.md) 決定 3 で学んだ「検査器が外される」形の別バージョンになる。

## 実装方針

### 実装側の抽出は **`;` までを 1 文として読む**

行単位の正規表現ではなく、`MapGroup` / `Map<Verb>` から次の `;` までを 1 文として切り出し、
その中の `RequireAuthorization(...)` を拾う（実測 2・3 への対応）。
**変数名 → (パス接頭辞, 群ロール) の表**を作り、`<変数>.Map<Verb>("...")` を引くときに引き当てる（実測 1）。

### ~~文書側は **ロールを表す語**を拾う~~ → **`x-roles` と突き合わせる**（実装時に変更）

> **［2026-08-10 変更 / [IADR-0156](../adr/IADR-0156_bff-authz-contract-checker.md) 決定 1］この節の方針は採らなかった。**
> 散文からロール集合を推定する案は、**実データで必ず誤ることが実装中に分かった**——
> `403` の記述に「**管理者ロール以外（…運用者も拒否される）**」があり、
> **両方の語を含みながら意味は admin のみ**である。詳細は §実装中に決めたこと 1。
> **機械可読な `x-roles` を 1 つ置き、それを正とする。**

当初の案（記録として残す）: `summary` / `description` / `403` から `platform-admin` /
`platform-operator` / `管理者` / `運用者` / `AdminOnly` を拾い、YAML のコメント行も読む。
**#645 で落ちたのが `/bff/documents:` の直上ブロックコメント**（`document` も `文書` も
含まない）だったため、構造で引こうとしたものである。
**散文の誤りを検出しないという限界は、この変更で受け入れた**（検査器の冒頭コメントに開示済み）。

### CI 配線は **`scripts-tests` への相乗り**

issue は「CI の `lint` 相当ジョブへ載せる」と書いているが、**`.github/workflows/` は
GitHub App 権限で編集できない**。[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 2 が確立し、#649（PR #652）でも踏襲した
**`scripts/scripts.repo.test.js` からの相乗り**に載せる。**新ジョブは作らない。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 実装と文書の一致を機械的に確認できる | 実データで exit 0 |
| 2 | **文書を admin ＋ operator へ戻すと fail** | 変異試験（純関数へ同義入力＋実データ変異の両方） |
| 3 | **実装から `AdminOnly` を外しても fail** | 同上（両方向） |
| 4 | 実測 1〜4 の構造で誤らない | 4 点それぞれに固定テスト |
| 5 | 偽陽性を allowlist で隠していない | allowlist ファイルを持たないこと |
| 6 | **ハンドラ内の認可も実効ロールに数える** | `DenyAsync` 型のヘルパ・直接の `AuthorizeAsync`・`/bff/admin/config` の実データ（レビュー 1 巡目の 🔴） |

**変異試験は #649 の教訓に従い、対象になり得る母集合全体（BFF 10 ファイル）へ当ててから確定させる。**

## 実装中に決めたこと（仕様書からの差分）

### 1. ★ 散文との突合を捨て、`x-roles` を置いた

仕様書は「`summary` / `description` / `403` の記述が主張するロールを取る」と書いていたが、
**実装中に実データで破綻が確認できたので方針を変えた**（[IADR-0156](../adr/IADR-0156_bff-authz-contract-checker.md) 決定 1）:

```yaml
"403": { description: 管理者ロール以外（IADR-0128 決定 1。運用者も拒否される） }
```

**「管理者」と「運用者」の両方を含みながら、意味は admin のみ**である。
**語を数える判定はここで必ず誤る。** 機械可読な `x-roles` を 1 つ置き、それを正とした。

**散文の誤りは検出しない**——この限界は検査器の冒頭コメントに開示してある。

### 2. 実測どおり 4 点すべてが必要だった

母集合で挙げた 4 点は**全部実際に効いた**。抽出結果（49 端点）で確認できる:

| 実測 | 抽出結果での現れ方 |
| --- | --- |
| 1 群が 2 つ | `/bff/documents` は GET 系が「制約なし」・POST 系が admin、`/bff/tags` は GET が admin+operator・書き込みが admin |
| 2 群の認可が別行 | `/bff/conversion/jobs` が admin+operator と解けている |
| 3 端点側だけに認可 | `/bff/dashboard/summary` が admin+operator と解けている（群には認可が無い） |
| 4 形が 2 種 | `AdminOnly` と `RequireRole` の両方を解いている |

### 3. ★★ `/bff/admin/config` を「制約なし」と誤って記録しかけた（レビュー 1 巡目の 🔴）

**この節には当初「認可が無いのは意図どおりだった」と書いていた。誤りだった。**

`ConfigBffEndpoints` は確かに `RequireAuthorization` を付けていないが、**ハンドラ内の
`DenyAsync` が `AuthorizeAsync(user, ConfigViewer)` を呼び、失敗時に 404 を返している。**
`ConfigViewer` は `RequireRole(AdminRole, OperatorRole)` なので、**実効ロールは
admin ＋ operator であって「制約なし」ではない。**

**なぜ誤ったか**: ファイル冒頭のコメント（「`RequireAuthorization` を付けると無認証が
404 到達前に 401 で短絡し存在が漏れるため、**認可はハンドラ内で判定する**」）を読んで
「ミドルウェアを使っていない」までは掴んだのに、**`DenyAsync` の本体を開かなかった。**
コメントは「ハンドラ内で判定する」と明言していたので、**読めば分かる場所に書いてあった。**

**これは #646 の教訓（隣のコメントではなく実体を開く）と同じ型の誤りである。**
本セッションで何度も引用しておきながら、自分で踏んだ。

**しかも害が二重だった** —— 誤った値を、よりによって**「正」として `openapi.yaml` へ確定させる**
ところだった。**本 issue が防ごうとしている事故（契約が実装と食い違う）を、検査器が
検出できない形で新規に作り込む**ことになる。

**是正は値ではなく検査器側で行った**（[IADR-0156](../adr/IADR-0156_bff-authz-contract-checker.md) 決定 3）——
**同一ファイルの private ヘルパを 1 段たどり、`AuthorizeAsync(..., ポリシー)` も実効ロールへ
AND 合成する**。値だけ直すと、次に同じ形が来たときまた人が気づくしかない。
是正後、検査器は当該 3 口を**自力で不一致として検出**した（＝盲点が塞がった証跡）。

### 4. 経路制約の正規化が要った

実装は `{id:guid}`、OpenAPI は `{id}`。**実装側を OpenAPI へ寄せる**
——OpenAPI に ASP.NET の経路制約構文は無く、逆向きには寄せられないためである。

### 5. 変異試験（**両方向**・実データ）

| 変異 | 結果 |
| --- | --- |
| `openapi.yaml` の `/bff/documents` POST を admin ＋ operator へ戻す（**#629 の事故の再現**） | **exit 1**・不一致 **1 件のみ** |
| 実装から `BffDocumentDelete` の `AdminOnly` を外す | **exit 1**・不一致 **1 件のみ** |
| 両方戻す | **exit 0** |

**#647 の受け入れ基準（両方向で fail すること）を実データで満たしている。**

## 申し送り

- **[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)（多層防御）の後段側**（`*Endpoints.cs` のサービス実装）は**本 issue の射程外**。
  BFF と文書の突合に閉じる。後段まで広げるなら別 issue（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。
- 判定式・除外の根拠は**実装時に IADR として起こす**（#649 のレビューで「同種の検査器はいずれも
  専用 IADR を持つ」と指摘され、[IADR-0155](../adr/IADR-0155_doc-updated-staleness-checker.md) を起こした先例に従う）。
