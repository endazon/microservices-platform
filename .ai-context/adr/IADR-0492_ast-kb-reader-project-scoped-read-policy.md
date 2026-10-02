---
title: IADR-0492 AST の KB の読み手は書き手と別の機密クライアント（ロールなし・profile あり）にし、その service-account の projects=ai-stock-trading に project=ai-stock-trading の文書だけを許す read のポリシー 1 本で読ませる
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-05, FR-09, NFR-09, ADR-0004, ADR-0080, ADR-0085, ADR-0088, ADR-0119, ADR-0121, ADR-0124, IADR-0075, IADR-0133, IADR-0253, IADR-0373, IADR-0420, IADR-0456, IADR-0485]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0085_project-attribute-scope-and-non-axis.md 決定 2（本 IADR が例外を置く）・決定 3（静的ポリシー対）
  - planning:projects/microservices-platform/07_adr/ADR-0080_set-valued-user-attributes-and-match-semantics.md 決定 2（集合値の交差）
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1（属性は IdP から引き直す）
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md 決定 2 (2)（射程の確認）
related_specs:
  - ../specs/20261002_issue-1696_ast-kb-read-policy.md
---

# IADR-0492: AST の KB の読み手の主体とポリシー（#1696）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-02
- 決定者: 利用者裁定（#1696 のコメント 2026-10-02・案 B）を claude が実装の形へ落とした

## 起点・関連

- 起点 issue: #1696（AST#1078 の調査で見つけた基盤側の前提）。対の AST 側: AST#1078（AST/IADR-0485 が検索の資格情報を分ける）
- 関連する計画書 ID: FR-03・FR-05（検索と ABAC）、FR-09（ポリシー）、NFR-09、AST/FR-08・AST/FR-04（取引判断の RAG）
- 関連する計画 ADR: ADR-0085 決定 2（`project` を判定軸へ加えない）・決定 3（将来載せるなら静的ポリシー対を先に検討）、
  ADR-0080 決定 2（集合値の利用者属性は交差で判定）、ADR-0088 決定 1（判定の属性は IdP から引き直す）、
  AST/ADR-0032 決定 2 (2)（MCP のサービスアカウントの `projects` に `ai-stock-trading` を入れない）
- 関連する実装 ADR: [[IADR-0075]]（書き手は `platform-operator` だけ）、[[IADR-0420]]（機械の主体の識別）、[[IADR-0373]]（MCP 経路の一律除外）、
  [[IADR-0133]]（dev seed）、[[IADR-0253]]（1 ポリシー = 1 分岐）、[[IADR-0485]]（対になる秘密）、[[IADR-0456]]（AST の app-secrets）
- 基点コミット: `origin/develop` `b4eb21ce`

## コンテキストと課題

AST の取引判断が RAG で KB を検索すると、実環境では 0 件になる（AST#1078）。#1696 は原因を 2 つと見た。

1. **主体が解決されない。** 検索サービスは `Identity.Name`（`preferred_username`）をキーに `/authz/scope` を引き、
   認可サービスは IdP の名簿をその名前で引く。AST が使う `ai-stock-trading-kb-writer` は `defaultClientScopes: ["roles"]` だけで
   `profile` を持たないので、トークンに `preferred_username` が載らない（`MachinePrincipal` の腕 B の形）。
2. **ABAC の属性とポリシーが無い。** 書き手の service-account は `platform-operator` だけを持ち、属性が無い。
   dev seed の read ポリシーは、AST の文書（`owner=system` または書き手・`confidentiality=internal`・`project=ai-stock-trading`）を
   読めるどの段にも当たらない（書き手は自分が owner の写しだけを所有者の分岐で読める。#1615）。

裁定（案 B）: `project=ai-stock-trading` に限る read の専用ポリシーを作る。`clearance=internal` は与えない。読み手のクライアントを書き手と分け、
最小権限で読み手は書けない。機械の主体の userId は読み手のクライアントに `profile` を付けて `preferred_username` で解決する。

### 決めることは 3 つ

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | ポリシーの利用者の条件（誰にマッチさせるか） | (a) `projects`（計画の利用者属性「参加プロジェクト」）／(b) 新しい属性キー（例 `kb_reader`）／(c) 条件なし |
| 2 | 読み手の主体の形 | ロール・既定スコープ・属性 |
| 3 | 秘密の供給 | AST の app-secrets のどのキーで運ぶか |

## 決定

### 決定 1 — 利用者の条件は `projects ∋ ai-stock-trading`、文書の条件は `project ∈ {ai-stock-trading}`。read のポリシー 1 本

```json
{ "name": "dev: AST の KB の読み手は AST の文書を読める", "action": "read",
  "userConditions": { "projects": ["ai-stock-trading"] },
  "documentConditions": { "project": ["ai-stock-trading"] } }
```

- **(a) を採る。** 計画 07_abac-attribute-model の利用者属性 `projects`（参加プロジェクト）は、文書の `project` と対になる語彙として既に在り、
  集合値キーとして交差で判定する（ADR-0080 決定 2。`UserAttributeEncoding`）。ADR-0085 決定 3 は「将来判定軸へ載せる場合も、まず
  ADR-0080 決定 2 の交差意味論による**静的ポリシー対**を検討する（新しい語彙が要らない）」と書いており、本決定はその形そのものである。
- **(b) を採らない。** 計画に無い属性キーを実装が発明することになる（`AbacEvaluator` が束縛変数の語彙を足さないのと同じ理由）。
- **(c) を採らない。** 利用者の条件が空のポリシーは全利用者にマッチする（`AbacEvaluator.MatchesUserConditions`）——全員に AST の文書を開く。
- 🔴 **ADR-0085 決定 2（`project` を判定軸へ加えない）の例外である。射程はこの 1 本に限る。** 同決定の懸念は「`project` を allow ポリシーの
  文書条件へ載せた瞬間、`project` を持たない文書が全滅する」であり、それは**そのポリシーにしかマッチしない主体**にだけ及ぶ。
  利用者の条件を `projects` に限ったので、`projects` を持たない主体（人の利用者・他のサービスアカウント全部）の範囲は 1 つも変わらない
  （`AstKbReaderPolicySeedTests` T-5・`Fixtures/owner-read-seed-scopes.json` の既存 3 主体が不変）。読み手にとっては「AST の文書以外が見えない」が
  まさに裁定が求めた形である。**計画側への記録は §残余 1。**
- **`projects` を属性辞書へ足さない。** 足すと SC-17（`UserAssignmentValidation`）から人へ `projects=ai-stock-trading` を配れるようになり、
  裁定が予定していない経路で AST の文書の読み取りが広がる。辞書に無いキーでもポリシーは保存できる（`AbacValidation.ValidateConditions` は
  未定義キーを許容する）。`attributes.json` の注記「辞書に無ければ ValidatePolicy が projects を条件に持つポリシーを作らせない」は誤りだったので直した
  （規則 10）。
- **action は read だけ。** write / analyze を作らない（読み手は書けない）。
- **個人資料は届かない。** 属性の分岐（束縛を持たない分岐）は個人資料を除く（`PrivateNoteVisibility`）ので、`project=ai-stock-trading` を名乗る
  他人の個人資料は読めない（`AstKbReaderSearchScopeTests`）。
- **`confidentiality` で絞らない。** 裁定は `project` だけを指定した。AST の文書の機密区分は AST 側が文書ごとに補完する（`HttpKnowledgeBaseWriter`）。読み手は AST の文書であれば区分を問わず読む。

### 決定 2 — 読み手は書き手と別の機密クライアント `ai-stock-trading-kb-reader`。ロールなし・既定スコープは `profile` だけ・属性は `projects` だけ

| 項目 | 値 | 理由 |
| --- | --- | --- |
| grant | client_credentials のみ（標準フロー・直接付与は閉） | 人が名乗る口を開けない（`check-realm-constraints.js` 検査 7） |
| 既定スコープ | `profile` だけ | `preferred_username = service-account-ai-stock-trading-kb-reader` が載り、検索サービスの `Identity.Name` → 認可サービスの `FindByUsernameAsync` で引ける（裁定）。`roles` は載せない（ロールを持たないので要らない） |
| realm ロール・client ロール | **無し** | `POST /documents` ほか書き込みの群は `platform-admin` / `platform-operator` を要求するので 403。本文の投入・タグの反映は所有者の束縛で判定され、読み手は文書を作れないので何も所有しない。`platform-service` も無いので東西の gRPC 端点（`ServiceCaller`）へも届かない。`/search` は認証だけを要求する（`SearchEndpoints`） |
| 属性 | `projects = ["ai-stock-trading"]` だけ | 決定 1 のポリシーにマッチさせる。`clearance` は与えない（裁定。与えると階段にマッチし基盤全体の `internal` が読める） |

- **AST/ADR-0032 決定 2 (2)（サービスアカウントの `projects` に `ai-stock-trading` を入れない）に当たらないことを確かめた。** 同決定の対象は
  「**MCP の**サービスアカウント」（外部エージェント経路）であり、読み手は MCP クライアントではない（`McpSubjectResolver` の登録簿に載らない）。
  MCP 側の割当禁止（`ToolPublicationConfigValidator.ValidateServiceAccountAttributes`）と一律除外（`ServiceAccountDocumentFilter`）は変えない。
- **機械の主体の識別（[[IADR-0420]]）とは矛盾しない。** 読み手は腕 A（`service-account-` 接頭辞）で機械と読まれる。
- **realm の写し（AST 専用レルム）には置かない。** 書き手と同じく基盤レルム専用（`check-realm-copy-drift.js` の `ONE_SIDED_CLIENTS` に理由つきで宣言）。

### 決定 3 — 秘密は `ai-stock-trading/app-secrets` の `kb-reader-auth-client-id` / `kb-reader-auth-client-secret` で運ぶ

- 既存の 4 組（`service-` / `kb-` / `llm-` / `discord-owner-auth-client-*`）と同じ置き場・同じ扱い（対になる秘密。SC-22 の `notWritable`。
  [[IADR-0485]]・[[IADR-0456]]）。**書き手の `kb-auth-client-*` を共用しない**（共用すると読み手が書ける資格情報を持つ＝裁定の最小権限に反する）。
- dev の種は `deploy/local/vault/eso/bootstrap.sh` が KV の無いときだけ作り、在る KV には無いキーだけを足す（既存 4 組と同じ）。
  値は realm の宣言と同値（`SecretItemBootstrapSeedTests` が固定）。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 読み手は AST の文書だけを読む | **ある（開発環境）**: realm の宣言（属性 `projects` だけ）＋ dev seed のポリシー 1 本。試験 `AstKbReaderPolicySeedTests` / `AstKbReaderSearchScopeTests` / `scripts.repo.test.js` | 本番は**投入するまで読み手は 0 件**（deny-by-default。広がる向きには倒れない）。投入手順は `docs/operations/operations.md` |
| 読み手は書けない | **ある**: ロールを持たない（書き込みの群は 403）・所有する文書が無い | — |
| 他の主体の範囲を変えない | **ある**: 利用者の条件を `projects` に限る・`projects` を辞書へ足さない・realm で `projects=ai-stock-trading` を持つのは読み手だけ（`scripts.repo.test.js`） | 稼働中の realm で管理コンソールから `projects` を付けることは宣言の検査の外（運用の注意） |
| 実データで参考情報が 1 件以上載る | 🔴 **未実測**（PoC で確かめる。§PoC での反映） | — |

## 結果

- 良い影響: AST の取引判断の検索が、AST の文書（収集記事・確定報告書）を引けるようになる（AST 側の BaseUrl・資格情報の配線と合わせて）。
  読み手が漏れても書けず、基盤の他の文書も読めない。
- 悪い影響 / トレードオフ: ADR-0085 決定 2 の例外が 1 本できる。`projects` が初めて判定に使われる（ポリシー 1 本だけ）。
  SC-17 から読み手の属性を差し替えると `projects` が消え（辞書に無いので付け直せない）、読み手の検索は 0 件に戻る（開発環境は realm の再適用で戻る）。

## 残余

1. **計画側への記録**: ADR-0085 決定 2 に「AST の KB の読み手の静的ポリシー対 1 本を例外とする（#1696 の裁定）」が書かれていない。
   次に ADR-0085 を読む者が本ポリシーを違反と読む恐れがある。planning への環流（部分改定または追記の依頼）が要る。［2026-10-02 追記 / #1696］planning#712 として起票済み（2026-10-02）。
2. **本番の NetworkPolicy**（AST 名前空間からの ingress）は別件（裁定のとおり）。本番で読み手を使うにはその許可が要る。
3. **本番への投入**: 本番のポリシーは配備の手順で管理者が投入する。消えたことを知らせる計器は置いていない（所有者の読み取りと違い、
   消えても読み手が 0 件になるだけで他の主体へ影響しないため。必要になったら別件）。
4. **実データでの受け入れ（参考情報 1 件以上）は PoC で確かめる**（試験はポリシーと絞り込みの形までを固定する）。
5. ［2026-10-02 追記 / #1696・独立監査］**読み手のポリシーに機密区分の上限が無い。** 書き込みロールを持つ利用者は誰でも任意の文書へ `project=ai-stock-trading` を足せる（`ValidateRestrictedProjectRetained` が止めるのは除去であって付与ではない）。足された文書は区分を問わず（`confidential` / `restricted` を含む。個人資料は除く）AST の読み手へ届き、LLM のプロンプトへ載る。ラベルは「AST の読み手へ開く」の意味を兼ねるようになった。裁定は `project` だけを指定したので裁定違反ではない。計画側の扱いは planning#712 で追う。［2026-10-02 追記 / #1696・AI レビュー］**歯止め: 本番への投入（残余 3）は planning#712 の裁定が出るまで保留する。** dev の seed（PoC の受け入れ）は対象外。裁定が機密区分の上限を求めれば、ポリシーの文書の条件へ `confidentiality` を足す改定を別に起こす（本 PR では裁定の指定どおり `project` だけで絞る）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [`../specs/20261002_issue-1696_ast-kb-read-policy.md`](../specs/20261002_issue-1696_ast-kb-read-policy.md)
