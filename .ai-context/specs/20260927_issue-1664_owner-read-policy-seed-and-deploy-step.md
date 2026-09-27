---
title: 所有者の読み取りのポリシーを dev seed に必須として入れ、本番の配備の手順に投入を書く（#1664）
type: spec
status: done
related_ids: [FR-05, FR-19, NFR-09, UC-05, UC-11, SC-09, ADR-0121, ADR-0036, ADR-0098, ADR-0062, ADR-0119, IADR-0133, IADR-0253, IADR-0384, IADR-0447, IADR-0450, IADR-0476]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 1・4・6・フォローアップ 1・6
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-01・D-02・D-05・D-08
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md 決定 1（共有先の分岐の前例）
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 実測 9（MSP#1242 の契機）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md（read 規則）
issue: "#1664"
---

# 仕様書: 所有者の読み取りのポリシーを dev seed に必須として入れ、本番の配備の手順に投入を書く（#1664）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-05**（ABAC）、FR-19（個人資料）、**NFR-09**（文書単位の認可）、UC-05 / UC-11、SC-09（ポリシーの口）
- 計画 ADR: **ADR-0121 決定 1**（所有者の read はポリシー 1 本・利用者の条件なし・文書の条件は `owner ∈ {${current_user}}` だけ・dev seed と本番の両方で必須・
  本番は配備の手順で投入。SC-09 が動的束縛に対応するまではシステム管理者がポリシーの API へ直接投入）、**決定 4 の 1 番目**、決定 6（3 点セット）、
  フォローアップ 1・6。ADR-0036 D-01・D-02・D-05・D-08、ADR-0098 決定 1（共有先の分岐の前例）、ADR-0062 実測 9（MSP#1242 の契機）
- 関連 IADR: IADR-0133（dev seed）、IADR-0253（1 ポリシー = 1 分岐・束縛は評価器の中だけ）、IADR-0447（共有先の分岐をポリシー 1 本で表した前例）、
  **IADR-0384**（`confidentiality` の不在を「無制限」と読まない。本件はその契機の到来にあたる）、IADR-0450（共有先の写しは所有者だけに返す）、
  IADR-0476（DocumentService の個人資料の判定）
- 起点 issue: #1664（planning#688 の裁定の実装）

## 目的・背景

- 認可サービスの `ResolveScope` はポリシー 1 件ごとに分岐を作り、組み込みの「所有者は読める」分岐を持たない（ADR-0121 実測 1）。
  dev seed には所有者の **write** しか無く、read は機密区分の階段と共有先の分岐だけである（同 実測 2）。
- そのため dev seed の構成では、所有者が共有していない自分の個人資料を BFF で開くと 404 になる（同 実測 4。推論）。
- 内容の ABAC（#1615）の配備の順序の 1 番目として、所有者の read ポリシーを dev seed に入れ、本番の配備の手順に投入を書く。
  **内容の ABAC は有効にしない**（本件は seed・手順・試験だけ）。

## 設計

1. `deploy/local/abac-seed/policies.json` に次のポリシーを足す（名前は `dev: 所有者は自分の文書を読める`）。
   `{"action":"read","userConditions":{},"documentConditions":{"owner":["${current_user}"]}}`。
   - `userConditions` は**空で明示する**（階段の回帰試験が read ポリシーの `userConditions.clearance` を無条件に読むため。共有先の分岐と同じ理由）。
   - 評価器・検証器・契約・消費側のコードは**変えない**（形は共有先の分岐と同じ単一キーの束縛で、全消費面が既に扱う）。
2. 本番の配備の手順: `docs/operations/operations.md` の §デプロイ に「所有者の読み取りのポリシーの投入」の節を足す
   （対象の API と本文、投入済みの確かめ方、消したときの影響、配備の順番、予約値と同名の利用者を IdP に作らないこと）。
   `deploy/local/abac-seed/README.md` の「本番環境へ同じ値を入れる意図はない」をこのポリシーについて改める。
3. 試験（ADR-0121 フォローアップ 6・issue のやること 3・4）。**seed を入れた構成**を次の鎖で試験に通す。
   - (a) 認可サービス: seed の `policies.json` を実ファイルから読み込み、`POST /authz/scope` を実際に呼んで得た応答が、
     期待値のファイル（`AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`）と一致することを固定する。
     期待値は 2 人（属性なしの利用者・`clearance=internal` の利用者）。
   - (b) 消費側（BFF・MCP の登録者・検索・DocumentService）は同じ期待値のファイルを読み、その応答をそのまま入力にして判定を固定する。
     期待値を 2 か所に書き写さない（片方が古くなると (b) が作り物になる）。
4. IADR: 新設（最大番号＋1）。判断（ポリシーの形・投入の手順・副作用の評価）を残す。IADR-0384 に「契機が来た」の日付つき追記を置く。

## 許可が広がる方向の評価（着手前）

seed を足すと、利用者条件が空なので **IdP に居る全利用者**に所有者の分岐（`owner ∈ {本人}`）が 1 本足される。`granted` は変わらない
（共有先の分岐も利用者条件が空で、既に全員 `granted=true`）。変わるのは分岐が 1 本増えることと、据え置きの `AllowedFilters` に
`owner: ["${current_user}"]`（**束縛前のリテラル**）が加わることである。

| 面 | 変化 | 広がり | 根拠 |
| --- | --- | --- | --- |
| BFF 詳細・本文・版（`IsReadable`） | 所有者は自分の個人資料・自分の組織文書を読める | **計画の意図どおり**（read 規則の所有者の分岐）。他人の文書へは届かない | 分岐は `owner ∈ {本人}` の単一キー。`PrivateNoteVisibility` は owner 分岐を裁量と数える |
| BFF の共有先の写し（`GrantedAsOwner`） | 所有者に自分の資料の `sharedWith` が返る | 計画の意図どおり（IADR-0450: 所有者にだけ返す）。共有された相手には返らない | 束縛値が本人なので他人の資料では一致しない |
| BFF SC-05 一覧（`IsManageable`） | 自分の組織文書が一覧に出る。個人資料は出ない | 計画の意図どおり | 個人資料の一律除外は不変 |
| 検索（`ScopeNarrowing`・`InMemoryVectorStore`・`QdrantVectorStore`） | 自分の文書が検索に出る | 同上。利用者が `owner` を絞り込みに指定しても、他人の値との交差は空で分岐ごと落ちる | `ResolveBranches` |
| グラフ・Wiki（`AbacNodeFilter`・`AbacPageFilter`） | 自分のノード・ページが見える | 同上（**本 PR は GraphService を触らない**） | 同じ述語 |
| DocumentService（`DocumentReadAccess`） | 変化なし | なし | 所有者は分岐より前にコードで判定済み。分岐はグループ共有の段でしか見ない（owner 分岐は本人の資料にしか一致しない） |
| MCP の登録者の配れる区分（`RegistrarScopeReading`） | owner 分岐は数えない | **なし**（IADR-0384 で是正済み。ADR-0062 実測 9 の再顕在化が無いことを試験で固定する） | 単一キー `confidentiality` の分岐だけを数える |
| `${current_user}` の束縛の失敗 | 空文字・`anonymous`・名簿に居ない名前は分岐を得ない | なし | `ScopeUserAttributeSource` が名簿に居ない名前を `NotFound`（`granted=false`）にする。Keycloak の実装は空白を引かない |
| 予約値の owner（`system`・`anonymous`） | その名前の利用者が IdP に**居れば**、その予約値の文書が読める | **残余リスク**（IdP の運用で閉じる。下記） | dev realm にその名前は居ない（試験で固定）。本番は手順書で禁じる |
| 据え置きの `AllowedFilters` | `owner: ["${current_user}"]`（リテラル）が加わる | **狭まる側**（束縛前の値はどの文書にも一致しない。IADR-0253 決定 2） | 分岐を運ぶ発行者（本評価器）では分岐が常に 1 本以上あり、フォールバックは使われない |

- **サービスアカウントが owner の文書**: そのサービスアカウント自身だけが読める（ADR-0119・ADR-0121 の意図。AST の KB の書き手）。
  個人資料は機械の主体へ一律に返さない判定（DocumentService・MCP の実行経路の除外）が別に掛かっており、本件で変わらない。
- **予約値と同名の利用者**: `DataSource.UnresolvedOwner = "system"`（取り込みで所有者を解決できなかった印・AST の古い写し）と、
  未認証の要求の身元 `anonymous` は、IdP にその名前の利用者が居なければ誰にも束縛されない。**居ると、その利用者が予約値の文書を読める**
  （write のポリシーでは既に書けるので、read で新たに開く面は読み取りである）。realm は自己登録・利用者名の変更を禁じ（`registrationAllowed=false`・
  `editUsernameAllowed=false`）、SC-17 に作成の口は無いので、作れるのは Keycloak の管理者だけである。**コードでの防御は足さない**
  （起こり得るのは IdP の管理操作だけで、それは手順書で閉じる）。dev realm に居ないことは試験で固定する。
- **結論**: 計画の意図の外へ許可が広がる副作用は見つからなかった。止める理由は無い。

## 受け入れ基準

- AC-1: `policies.json` に、action=read・利用者条件が空・文書条件が `owner ∈ {${current_user}}` だけのポリシーがちょうど 1 本ある（scripts と C# の両方で固定）。
  階段の回帰試験は通る。dev realm に `system`・`anonymous` という利用者が居ない。
- AC-2: seed を読み込んだ認可サービスの `/authz/scope` の応答が期待値のファイルと一致する（属性なしの利用者・`clearance=internal` の利用者）。
  所有者の分岐は本人の名前だけに束縛され、プレースホルダを残さず、条件の空な分岐（全件許可）は 1 本も無い。
- AC-3: 名簿に居ない名前（`anonymous`・`system`・空文字）は `granted=false`（束縛の失敗は許可へ倒れない）。
- AC-4（フォローアップ 6）: 期待値の応答を入力にした BFF で、所有者は共有していない自分の個人資料を詳細・本文・版のいずれでも開ける（200）。
  陰性対照: 他人（`clearance=internal` の利用者）は開けない（404）。所有者も他人の個人資料は開けない。属性なしの所有者は `owner=system` の組織文書を開けない。
- AC-5（やること 4・MSP#1242）: 期待値の応答を入力にした MCP の登録者の解決で、属性なしの利用者は区分を 1 つも配れず、無制限にもならない。
  `clearance=internal` の利用者は `public`・`internal` だけを配れ、無制限にならない。
- AC-6: 期待値の応答を入力にした検索で、所有者の分岐は本人の文書だけを通し、`owner` を他人に絞った要求でも他人の文書は出ない。
- AC-7: 期待値の応答を入力にした DocumentService の判定で、所有者の分岐は他人の個人資料の読み取りを許さない。
- AC-8: 本番の手順（`docs/operations/operations.md`）に、API と本文・確かめ方・消したときの影響・配備の順番・予約値の注意がある。
- AC-9: 変異（ポリシーの条件キーを `owner` 以外にする・束縛しない／空に倒す 等）で試験が赤になる。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n -l -e 'policies.json' -e 'abac-seed'`（全追跡ファイル。コード・scripts・deploy・docs・`.ai-context`）
- `git grep -n -e 'AllowedFilters' -e '\.Filters\b' -- '*.cs'`（スコープの消費側。試験を除く）
- `git grep -n -e '${current_user}' -e 'CurrentUserPlaceholder'`（束縛）
- `git grep -n -e '"anonymous"' -e 'NameClaimType' -e 'UnresolvedOwner'`（束縛される値の出どころと予約値）
- 誤りになる側の文字列: `git grep -n -E '所有者ベースは .?write|confidentiality だけ(を文書条件|を見る)|read ポリシーは 4 本|本番環境へ同じ値を入れる意図はない'`

### 結果

- seed と投入: `deploy/local/abac-seed/{policies.json,README.md}`（変える）、`scripts/seed-abac-policies.js`（名前で冪等に投入する。**変えない** —— 名前が新しいので既存クラスタにも再実行で入る）。
- seed を検査するもの: `scripts/scripts.repo.test.js`（階段の回帰。所有者の read の形と realm の予約値の試験を足す）。
- 評価器・束縛: `AbacEvaluator.cs`、`ScopeUserAttributeSource.cs`、`ResolveScope/{Endpoint,GrpcService}.cs`、`KeycloakIdentityAdminClient.FindByUsernameAsync`（**変えない**）。
- 消費側（変えない。試験で固定するもの / 評価だけするもの）: BFF `DocumentBffEndpoints.cs`・`BffScopeResolver.cs`（試験）、
  McpServer `RegistrarScopeReading.cs`（試験）、Retrieval `ScopeNarrowing.cs`・`InMemoryVectorStore.cs`（試験）・`QdrantVectorStore.cs`・`HybridSearchService.cs`・
  `AttributeValues/Endpoint.cs`（同じ分岐を渡すだけ）、DocumentService `DocumentReadAccess.cs`（試験）・`GrpcDocumentReadScopeSource.cs`、
  Wiki `AbacPageFilter.cs`（評価のみ）、Graph `AbacNodeFilter.cs`（評価のみ）、`PrivateNoteVisibility.cs`。
- 文書: `docs/operations/operations.md`（節を足す）、`docs/tests/FR-05_abac-access-control.md`（T-24〜）、`deploy/local/search-seed/documents.json` の注記
  （「confidentiality だけを文書条件に持つ」は共有先の分岐の時点で既に古い。所有者・共有先の分岐が seed 文書を負の対照へ見せない理由に直す）、
  `scripts/scripts.repo.test.js:10728` の注記（同じ）、`AbacEvaluatorTests.cs` の「現 seed の read ポリシーは 4 本とも階段なので今日は発現しない」（古くなる）、
  `IADR-0384`（日付つき追記）、`scripts/test-spec-coverage-baseline.json`（`--update`）。

### 除外したもの

- **GraphService**（`AbacNodeFilter.cs` とその試験）: 並行の作業（#1663・#1611 段 3）が触っている。分岐の読み方は BFF・検索と同じ述語で、評価だけ行う。
- `.ai-context/specs/` の過去の作業仕様書（例 `20260805_issue-517_abac-dev-seed.md`）: point-in-time の記録で書き換えない。
- `IADR-0384` の本文 68 行目（「所有者ベースは write の 1 本だけ」）: 凍結記録。日付つき追記で「契機が来た」を足す。
- 内容の ABAC（#1615）・検知と通知（ADR-0121 決定 2）・AST の古い写しの削除（決定 3）・コード判定の寄せ（決定 5）: 決定 4 の 2〜4 番目。本件では行わない。
- SC-09 の画面（動的束縛の入力。ADR-0036 フォローアップ 5）: 本件では行わない（手順書は API への直接投入を書く）。

## 配備の順番

- 本件は ADR-0121 決定 4 の **1 番目**である。コードの挙動は変わらない（seed のデータと手順と試験だけ）。
- dev: `node scripts/seed-abac-policies.js --live`（冪等。新しい名前のポリシーだけが入る）。
- 本番: **システム管理者**が手順書に従い、ポリシーの API（SC-09 と同じ口）へ直接投入する。投入しない限り本番の挙動は変わらない。

## 検証

- `dotnet build`（knowledge・platform の slnx 全体）
- AuthorizationService.Tests・Platform.Bff.Tests・DocumentService.Tests・RetrievalService.Tests・Platform.Shared.Infrastructure.Tests・McpServer.Tests 全件
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`check-trace-blocks`・`check-test-spec-coverage`・`check-test-traceability`・`check-cross-repo-refs`・
  `check-plan-id-qualification`・`gen-knowledge-graph --check`・`check-commit-messages --range=origin/develop..HEAD`
- 変異 2 件以上（コミット済みの状態で当て、`git show HEAD:<path> > <path>` で戻す）

### 結果（2026-09-27・ローカル）

- `dotnet build src/platform/backend/backend.slnx` / `src/knowledge/backend/backend.slnx`: 0 エラー（knowledge の警告 1 件は既存の `IngestToSearchQdrantTests` の CS0618 で本件と無関係）
- AuthorizationService.Tests 498・McpServer.Tests 239・Platform.Shared.Infrastructure.Tests 484・Platform.Bff.Tests 789（スキップ 1 は既存）・
  DocumentService.Tests 827・RetrievalService.Tests 452: 全件合格
- `dotnet format <slnx> --verify-no-changes`: 両ユニット exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 843 tests passed
- `check-trace-blocks` / `check-test-spec-coverage`（`--update` で床の対 411 件）/ `check-test-traceability` / `check-cross-repo-refs` /
  `check-plan-id-qualification` / `gen-knowledge-graph --check` / `check-commit-messages --range=origin/develop..HEAD`: OK
- **計画 ADR のレンジ**: コミット件名が ADR-0121 を引くため、当初は宣言 `ADR-0001..0119` を `0001..0121` へ引き直した（計画リポの `origin/main` = `3c7949f` の
  `gen-plan-ranges.js --check` の実測「宣言 [1, 121] / 実物 [1, 121]・欠番なし」）。push 前に develop へ入った #1669 が**同じ実測で同じ引き直し**を済ませていたので、
  rebase で本 PR の引き直し（入口の宣言・別紙の記録）は取り下げ、develop の版を採った。当初の母集合に無かった追随である。
- 変異（コミット済みの状態で当て、`git show HEAD:<path> > <path>` で戻した。`//MUT` の残りは 0 件）:
  - M1 seed の所有者のポリシーの条件キーを `owner` → `author`: scripts 赤（「所有者の read ポリシーが 0 本」）、`OwnerReadPolicySeedTests` 5 件赤（形・期待値の一致・束縛）
  - M2 名簿に居ない名前（`NotFound`）も評価へ進める（束縛の失敗を許可へ倒す）: `名簿に居ない名前は所有者の分岐を得ない` 3 件赤
  - M3 `PrivateNoteVisibility` が `owner` を裁量の分岐と数えない: BFF `所有者は共有していない自分の個人資料を開ける` 4 件赤（seed 前の実測 4 の 404 の再現）
  - M4 期待値のファイルの alice の所有者の分岐に `system` を足す（期待値の漂流）: `Seedを入れた端点の応答は期待値のファイルと一致する(alice)` 赤
  - M5 検索の絞り込みで交差が空の分岐を落とさない: `ownerを他人に絞っても他人の文書は出ない` 2 件赤
  - M6 MCP の登録者の読み方を旧実装相当（区分の軸の無い分岐を無制限）へ: seed の 2 件を含む 4 件赤
