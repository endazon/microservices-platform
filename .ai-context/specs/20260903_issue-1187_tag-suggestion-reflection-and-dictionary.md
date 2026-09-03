---
title: 作業仕様書 — タグ提案の承認を文書のタグへ反映し、認可を write または管理者ロールの選言にし、辞書の値域を生成段・承認段の両方で強制する
type: spec
status: done
related_ids:
  - FR-17
  - FR-18
  - UC-10
  - SC-03
  - SC-05
  - SC-09
  - SC-21
  - ADR-0033
  - ADR-0034
  - ADR-0036
  - ADR-0043
  - ADR-0059
  - ADR-0063
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "ADR-0063 決定 1（タグ提案の承認は文書のタグへ反映するところまでを要求とする）"
  - "ADR-0063 決定 2（反映できる値は SC-09 のタグ辞書に定義済みのタグに限る。辞書外は生成しない。生成されてしまったら承認できず却下のみ）"
  - "ADR-0063 決定 3（認可主体は承認者本人。①その文書への write または ②SC-05 の管理者経路のロール。サービスが代わりに書く形は採らない）"
  - "ADR-0063 決定 4（承認と却下は同じ権限に従う）"
  - "ADR-0063 決定 5（暫定表示は資格の有無で 2 つに分け、『準備中』の側は反映の実装をもって消す）"
  - "ADR-0063 §結果 フォローアップ 3（MSP#1014 は反映経路を開ける作業と同時に行う）"
  - "planning#495（環流。裁定 2026-08-29） / planning#304（取り込み経路はタグを生成しない）"
  - "05_screens §SC-03 AI 提案の承認欄（タグ提案は SC-09 のタグ辞書に定義済みの値のみ） / §SC-05 既定タグ辞書に整合の射程（経路を問わない）"
related_adrs:
  - IADR-0361
  - IADR-0300
  - IADR-0272
  - IADR-0266
  - IADR-0152
  - IADR-0153
  - IADR-0299
  - IADR-0323
  - IADR-0349
  - IADR-0044
issue: "#1187 / #1014"
---

# 作業仕様書: タグ提案の承認の反映経路・認可の選言・辞書の値域強制

## 起点

- **#1187**（planning#495 / ADR-0063 の受け皿）—— タグ提案の承認を文書のタグへ反映する経路を開け、
  承認・却下の認可を「①文書への `write` **または** ②SC-05 の管理者経路のロール」にする。
- **#1014**（同一 PR で閉じる）—— AI のタグ提案がタグ辞書の値域に収まることをどの層でも強制していない。
  ADR-0063 §結果 フォローアップ 3 が「反映経路を開ける作業と同時に行うこと」と名指ししている。

裁定の所在（いずれも降りている。新たな裁定依頼は要らない）:

| 論点 | 所在 |
| --- | --- |
| 反映まで実装するか | ADR-0063 決定 1（Accepted / 2026-08-29） |
| 値域を辞書に限るか・どの段で | ADR-0063 決定 2（**生成しない**・**生成されてしまったら承認できず却下のみ**＝両段） |
| 認可の主体 | ADR-0063 決定 3（①②の選言。代理書き込み不可） |
| 却下の対称性 | ADR-0063 決定 4 |
| 暫定表示の 2 分割 | ADR-0063 決定 5 |
| 取り込み経路がタグを生成するか（#1014 案 D） | planning#304（採らない） |

## 母集合（着手前に自分で引いた。issue 本文の実測は転記していない）

起点 `develop` `d06cf387`（origin/develop を merge 済み）。`git rev-parse --is-shallow-repository` → **`false`**。

### 走査 1 — 提案の生成 → 承認 → 反映の経路（`TagValue`）

```console
$ git grep -n "TagValue" -- src ':!src/platform/frontend/src/lib/api/generated' | grep -v Tests/
Domain/AiSuggestion.cs:48,124                       ★ 変更なし（値域の検査はここでは行わないと明記済み）
Domain/Ports/ISuggestionLlmClient.cs:29             変更なし
Features/AiSuggestions/AiSuggestionEndpoints.cs:89  ★ 変更（DTO へ CanDecide を足す）
Features/AiSuggestions/Generate/AiSuggestionGenerator.cs:124,154  ★ 変更（生成段の辞書照合）
Infrastructure/ExternalServices/LlmGatewaySuggestionClient.cs:86,98  変更なし
Infrastructure/Persistence/**（マイグレーション・Designer）      変更なし（列は増えない）
Shared/Knowledge.Contracts/Dtos/EdgeTypeDictionaryDto.cs:83       ★ 変更（AiSuggestionDto）
```

**承認段（`Approve/Endpoint.cs`）は `TagValue` に触れていない** —— 経路が無いことの実測である。
同ファイル `:60` の「反映の経路は #918 で決める」は #918 / #915 とも CLOSED（issue #1187 §事象 1）。

### 走査 2 — 辞書照合が 0 箇所である現状（陽性対照つき）

```console
$ git grep -n "db.Tags\|TagDictionary\|ITagDictionary" -- src/knowledge/backend/Services/GraphService | wc -l   → 0
$ git grep -n "db.EdgeTypes" -- src/knowledge/backend/Services/GraphService/Features/AiSuggestions/Generate/AiSuggestionGenerator.cs
AiSuggestionGenerator.cs:87   var types = await db.EdgeTypes ...    ← 陽性対照（辺の型辞書は同じ関数で強制されている）
```

**同じ関数の中で辺の型辞書（`:87` が引き、`:116-147` が未定義型を既定型へ倒す）は強制されている**ので、
0 件は走査の不備ではなく「タグ側にだけ無い」の実測である。辞書の権威は DocumentService の
`TagResolver.ToIdsAsync`（`Create` / `Update` / `UpdateMetadata` / `DocumentNormalizedConsumer` の 4 箇所が使う）。

### 走査 3 — 認可の判定点（`IsSourceWritableAsync`）

`AiSuggestionEndpoints.cs`（定義）/ `Approve/Endpoint.cs` / `Reject/Endpoint.cs`（呼び出し）/
`Bff/Knowledge.Bff.Endpoints/GraphBffEndpoints.cs`（**コメントで言及**。BFF は前段ゲートを置かない
[[IADR-0300]] 決定 2 のまま。変更しない）。

### 走査 4 — 暫定表示（決定 5 で消す側の語）

```console
$ git grep -ln "反映経路が未実装\|反映経路は未実装\|反映経路が実装されていない\|タグの反映経路" -- . ':!.ai-context'
docs/api/openapi.yaml                                             ★ 追随
docs/screens/SC-03_document-detail.md                             ★ 追随
docs/screens/SC-21_ai-suggestion-list.md                          ★ 追随
src/knowledge/frontend/.../AiSuggestionPanel.tsx                  ★ 変更
src/knowledge/frontend/.../DocumentDetailPage.test.tsx            ★ 変更
src/platform/frontend/src/lib/api/generated/graph/graph.ts        ★ 再生成（orval）
src/platform/frontend/src/locales/{en,ja}/messages.{po,ts}        ★ 再生成（pnpm run i18n）＋ en 訳
```

`docs/tests/SC-03_*.md`（行 14「承認は押せない」）/ `docs/tests/SC-21_*.md`（§満たしていない受け入れ基準）/
`docs/tests/FR-18_*.md`（ケース表）も**同じ事実を別の語で持つ**ので追随する（規則 9: 誤りの側の語で引いた
うえで、同じ事実を語る文書を確かめた）。

### 走査 5 — 新規の名前が衝突しないこと（陰性・陽性対照）

```console
$ git grep -il "IDocumentTagWriter\|ITagDictionaryReader" | wc -l   → 0
$ git grep -il "canDecide" | wc -l                                   → 0
$ git grep -il "IKnowledgeHealthReporter" | wc -l                    → 9   （陽性対照: 同型のポート名は非 0 で出る）
```

### 除外の理由

| 除外したもの | 理由 |
| --- | --- |
| `.ai-context/specs/**` `.ai-context/adr/IADR-0300` ほか確定済み記録 | 凍結記録は書き換えない（`traceability.repo.md`）。IADR-0300 決定 4 は本作業で事実上失効するが、後継 IADR（[[IADR-0361]]）側に Supersedes を書き、旧記録は触らない |
| `Domain/KnowledgeHealth*` `Features/KnowledgeHealth/**` `GraphDocuments/Sync/**` DashboardService | #1186 の宣言領域 |
| McpServer / AuthorizationService `Features/Users` | #1185 の宣言領域 |
| frontend Dockerfile / edge | #1135 の宣言領域 |
| `GraphBffEndpoints.cs` | 承認・却下は透過中継のままで足りる（DTO は Knowledge.Contracts 経由で流れる）。前段ゲートを置かない判断（IADR-0300 決定 2）は変わらない |
| `docs/api/BFF_bff-surface.md` | `/bff/graph/*` 群をもともと落としている（IADR-0300 フォローアップ 4。本作業の外） |
| `Knowledge.Contracts` の既存 DTO・イベント | 足す側だけ（`AiSuggestionDto` の既定値つき末尾追加・新 record 2 つ）。既存メンバーは変えない |

## 設計

### 1. 反映経路 —— GraphService → DocumentService `POST /documents/{id}/tags`（承認者の資格で）

- **DocumentService に「文書へタグを 1 つ足す」口を新設する**: `POST /documents/{id}/tags`
  本文 `AddDocumentTagRequest(Name)`（Knowledge.Contracts）。応答は `DocumentDto`。
  - 認可（ADR-0063 決定 3 と同じ選言を**最終防衛線として再判定**する。[[IADR-0044]]）:
    ①`DocumentBodyIntake.CanWrite(doc.Attributes, 主体)`（所有者の動的束縛）**または**
    ②`platform-admin` ロール。どちらも満たさなければ **404**（存在秘匿。`PutBody` と同じ。403 にしない）。
  - 辞書外の名前は **400**（`UnknownTagsProblem`。`TagResolver.ToIdsAsync` が権威）。
  - **冪等**: 既に付いていれば版を進めず・イベントも出さず 200。付けたときは `Document.AddTag` が版を
    1 つ進め（`changeNote: ai-suggestion-approved`）、`DocumentUpdated` を再発行する（射影が追随する。
    本文指紋は変わらないので却下解除は発火しない —— ADR-0050 の判定条件どおり）。
  - route group は認証のみ（`bodyIntake` と同じ理由でロールを積まない。積むと①が死ぬ）。
- **GraphService は承認者の `Authorization` ヘッダをそのまま転送して呼ぶ**（方式 A。`RagOrchestrator` →
  RetrievalService と同型。`IHttpContextAccessor` で読む）。**サービスアカウントを持たない**（決定 3）。
- ポート `IDocumentTagWriter.AddTagAsync(documentId, tagName)` → `TagWriteOutcome`
  （`Applied` / `UnknownTag` / `NotWritable` / `Unavailable`）。実装 `HttpDocumentTagWriter`。
- **承認の順序**（`Approve/Endpoint.cs`・`Kind == Tag`）:
  1. 既存の 4 段（read スコープ → 存在 → 端点の可視性 → 書き込みの認可）
  2. `pending` でなければ 409（**後段を呼ぶ前に**確かめる。書いてから 409 にしない）
  3. `IDocumentTagWriter.AddTagAsync` を呼ぶ
     - `Applied` → `TryApprove` → `SaveChanges` → 200
     - `UnknownTag` → **400 `unknown_tag`**（状態は `pending` のまま。決定 2 後段「承認できず却下のみ」。
       `unknown_edge_type` と同じ形）
     - `NotWritable` → 404（後段の最終防衛線が拒んだ。存在秘匿の一本道）
     - `Unavailable` → 502（**成功へ縮退しない**。BFF の後段不達と同じ姿勢）
  - 反映が成功して `SaveChanges` だけ失敗した場合は、文書にタグが付いたまま提案が `pending` に残る。
    再承認は DocumentService 側が冪等なので二重には付かない（受け入れ基準 2）。

### 2. 辞書の値域強制（#1014）—— 両段で

- **生成段**（決定 2 前段「辞書外の値を持つ提案は生成しない」）:
  - DocumentService に **`GET /internal/tags/names`**（名前だけ。使用件数を返さない）を足す。
    `/internal/knowledge-health/observations`（[[IADR-0299]] 決定 4）と同じ**メッシュ内部 API**
    （認証なし・OpenAPI に載せない・第一防御は mesh mTLS とネットワーク分離）。
    **利用者スコープで `/tags` を引かない** —— 同口は管理者・運用者限定（SC-05 Q18）であり、
    一般利用者の生成が全件 403 で 0 件になる。読み取り主体は **GraphService 自身**であり、
    辞書は LLM のプロンプトへ入るだけで**利用者へ丸ごと返らない**（ADR-0043 決定 1 に触れない。
    利用者が見るのは LLM が選んだ提案だけ＝ SC-03「タグ提案は辞書に定義済みの値のみ」の意図そのもの）。
  - ポート `ITagDictionaryReader.ReadNamesAsync` → `IReadOnlySet<string>?`（**null ＝引けなかった**）。
  - `AiSuggestionGenerator`: 辞書を `SuggestionPrompt.Seal` へ渡し（辺の型と同じく「LLM に選ばせる値集合」）、
    `PersistAsync` で**辞書に無い値を落とす**（Ordinal 一致。DocumentService の `TagResolver` と同じ比較）。
    引けなかったときは**タグ提案を 1 件も作らない**（fail-closed。リンク提案は影響を受けない）。
  - **落とした件数を OTel カウンタで数える** `graph.tag_suggestion_dropped.total`
    （理由タグ `out_of_dictionary` / `dictionary_unavailable`。値そのものはタグにもログにも出さない ——
    LLM が返す自由文であり基数が無界、かつ本文由来の語を含み得る）。`EdgeTypeFallbackMetrics` と同型。
- **承認段**（決定 2 後段）: §1 の DocumentService が権威として 400 を返し、GraphService は
  `unknown_tag` で承認を拒む。**GraphService が辞書を二重に引くことはしない**（判断点を 1 つに保つ）。
- **辞書の削除・改名と承認済みタグの関係**（#1014 受け入れ基準 5）: [[IADR-0361]] 決定 6 に明記する。
  改名は文書が識別子を参照するので自動追随（IADR-0153 決定 1）。削除は使用件数 1 以上で拒まれる
  （SC-09）ため、反映済みのタグは剥がれない。**提案行の `TagValue` は記録であり、改名に追随させない。**

### 3. 認可 —— ②（管理者ロール）を承認・却下の両方へ足す

- `AiSuggestionEndpoints.CanDecideAsync(suggestion, writeScope, user, db)`
  ＝ `IsSourceWritableAsync`（①。既存）**または** `user.IsInRole(PlatformAuthPolicies.AdminRole)`（②）。
  起点文書が複製に無ければ偽（②でも文書の実在は要る）。
- ②は **`platform-admin` のみ**（SC-05 の「作成・編集は管理者限定」。`UpdateMetadata` が `AdminOnly` で
  あることと揃える。運用者は含めない）。
- `Approve` / `Reject` の両方で `IsSourceWritableAsync` を `CanDecideAsync` に置き換える（決定 4）。
  **種別で分けない** —— リンク提案の承認にも②が効く。ADR-0063 §結果は「認可の判定が ADR-0059 と揃う。
  『所有者、または管理者経路』という同じ形になる」と述べており、種別で分けると承認欄の資格が
  行ごとに違う規則になる。
- 拒否は **404** のまま（[[IADR-0272]] 決定 5）。

### 4. 資格の表示 —— `AiSuggestionDto.CanDecide`（サーバ側で判定した値）

- `AiSuggestionDto` に **`bool CanDecide = false`** を末尾へ足す（既定値つき＝非破壊。IADR-0122 決定 2）。
  一覧（`List/Endpoint.cs`）が行ごとに `CanDecideAsync` の結果を載せる（write スコープは要求ごとに
  1 回だけ解決。**行が 0 件なら解決しない**）。
- SPA は辞書もポリシーも引かない。`canDecide` だけで表示を分ける（ADR-0063 決定 5）:
  - **持たない**: 承認・却下とも押せず、「**この文書のタグを編集する権限がありません。**」を
    画面上のテキストとして出す（恒久）。却下も塞ぐのは決定 4（同権限。押せても 404 になる）。
  - **持つ**: 承認ボタンが有効。「準備中」「未実装」の文言は**存在しない**（フォローアップ 4）。
  - 承認が 400 `unknown_tag` で返ったときは「このタグは辞書に無いため反映できません。却下してください。」
    を出す（決定 2 後段の帰結を利用者に読める形で）。
- **リンク提案の行は従来どおり**（ボタン有効・拒否は 404 → 汎用エラー）。決定 5 の射程はタグ提案であり、
  リンク行への拡張は [[IADR-0361]] フォローアップ。

### 5. 変更ファイル（宣言領域の内側）

| 領域 | 変更 |
| --- | --- |
| `Shared/Knowledge.Contracts/Dtos/EdgeTypeDictionaryDto.cs` | `AiSuggestionDto.CanDecide` |
| `Shared/Knowledge.Contracts/Dtos/TagDictionaryDto.cs` | `AddDocumentTagRequest` / `TagNamesResponse` |
| DocumentService `Domain/Document.cs` | `AddTag` |
| DocumentService `Features/Documents/AddTag/Endpoint.cs`・`DocumentEndpoints.cs` | 新口・route group |
| DocumentService `Features/Tags/Names/Endpoint.cs`・`TagDictionaryEndpoints.cs` | 内部の名前一覧 |
| GraphService `Domain/Ports/{IDocumentTagWriter,ITagDictionaryReader}.cs` | ポート |
| GraphService `Domain/SuggestionPrompt.cs` | `Seal` にタグ辞書・`Render` に「## タグ」 |
| GraphService `Infrastructure/ExternalServices/{HttpDocumentTagWriter,HttpTagDictionaryReader}.cs` | 実装 |
| GraphService `Common/Observability/TagSuggestionDropMetrics.cs` | カウンタ |
| GraphService `Features/AiSuggestions/{AiSuggestionEndpoints,Approve,Reject,List,Generate}` | 本体 |
| GraphService `Program.cs` | DI（`AddHttpContextAccessor` / `DocumentService` クライアント / メトリクス） |
| GraphService `Tests/**` / DocumentService `Tests/**` / `Platform.Bff.Tests` | テスト |
| `docs/api/openapi.yaml` → `pnpm run codegen` | `canDecide`・承認の説明・400 `unknown_tag` |
| `sc03-document/components/AiSuggestionPanel.tsx`（＋テスト）→ `pnpm run i18n` | 決定 5 |
| `docs/screens/SC-03,SC-21` `docs/tests/SC-03,SC-21,FR-18` | 追随 |
| `scripts/contract-schema-baseline.json` | `--update`（非破壊の追加） |
| `.ai-context/adr/IADR-0361_*.md` ＋ README | 実装 ADR |

## 受け入れ基準（#1187 の 11 件 ＋ #1014 の 5 件を写像）

| # | Given / When / Then | 写像 |
| --- | --- | --- |
| 1187-1 | `write` を持つ文書の `pending` タグ提案を承認 → `approved` かつ文書にタグが付く | GraphService `TagSuggestionApprovalTests`（陽性）・DocumentService `DocumentTagReflectionTests`（陽性） |
| 1187-2 | 再承認 → 409、タグは二重に付かない | 同上（writer 呼び出しは 1 回）・DocumentService の冪等テスト |
| 1187-3 | `owner=system` の文書の提案を管理者ロールが承認 → 成功 | `TagSuggestionApprovalTests`（write 拒否 ＋ admin → 200） |
| 1187-4 | `write` もロールも無い利用者が承認 → 404 | 同上 ＋ `WriteActionAuthorizationTests`（ロールを落として 404） |
| 1187-5 | 同じ利用者が却下 → 404 | 同上 |
| 1187-6 | 管理者ロールが却下 → 成功 | 同上 |
| 1187-7 / 1014-1..3 | 辞書外の値は生成されない・承認されない・**全経路で文書に付かない** | `TagDictionaryEnforcementTests`（生成段。陽性対照つき）・`TagSuggestionApprovalTests`（承認段 400）・DocumentService 400 |
| 1187-8 | 資格を持たない利用者に「この文書のタグを編集する権限が無い」が**テキストとして**読める | `DocumentDetailPage.test.tsx` |
| 1187-9 | 資格を持つ利用者は承認ボタンが有効で「準備中」の文言が無い | 同上 |
| 1187-10 | SC-21 の行は非表示にならず導線を持つ | 既存 `AiSuggestionListPage.test.tsx`（変更なし＝影響なし） |
| 1187-11 | 一括承認に当たるルートが無い | 既存 `No_bulk_approval_route_exists` / `BffGraphSuggestionTests`（変更なし） |
| 1014-4 | 辞書内の値は従来どおり通る（陽性対照を同じクラスに置く） | 上記の各陽性対照 |
| 1014-5 | 辞書の削除・改名後の挙動が IADR に明記されている | [[IADR-0361]] 決定 6 |

## テスト方針

- **陰性は陽性対照と対**で置く（同じクラス内）。「常に 404」「常に空」で緑になる形を作らない。
- **変異試験**: `AiSuggestionGenerator.PersistAsync` の辞書照合（`dictionary.Contains(value)`）を外して
  `TagDictionaryEnforcementTests` が赤になること、`AddTag/Endpoint.cs` の `UnknownTagsProblem` 分岐を外して
  `DocumentTagReflectionTests` が赤になることを実走して記録する（PR 本文）。
- 既存 `WriteActionAuthorizationTests` の否定形 2 件（承認・却下）は**既定ロールが `platform-admin`**
  のため②が通ってしまう。ロールを落として（`X-Test-Roles: viewer`）否定形を保つ。
- HTTP アダプタ（`HttpDocumentTagWriter`）は `HttpMessageHandler` 層で「Authorization を転送する」
  「200 / 400 / 404 / 5xx / 例外の写像」を固定する。**転送を外すと後段が匿名として拒む**ので、
  転送の陽性対照は必須。

## 実測（稼働 k3s）

GraphService / DocumentService / BFF のイメージだけ差し替え、一時利用者のセッションで
承認 → 文書のタグに反映（陽性）／辞書外は反映されない（陰性）／資格なしは拒否 を測る。
手順と結果は PR 本文に載せる。一時利用者は PR #1156 コメント（2026-09-02 23:44）の手順で作り、終了時に消す。

> ［2026-09-03 追記 / #1187］**実測は「イメージ差し替え ＋ 無認証で測れる範囲」まで行い、認証が要る部分は
> 人の実走へ引き渡した。** 3 つのイメージ（`graph-service` / `document-service` / `bff`。タグ `issue1187`）は
> `kubectl set image` で差し替え、いずれも rollout 完了・Ready 1/1 を確認した（差し替え前のタグ:
> `graph-service:issue1186` / `document-service:latest` / `bff:issue1199`）。port-forward で
> `GET /internal/tags/names` → 200 `{"names":[]}`（内部口が認証なしで応える）、
> `POST /documents/{id}/tags` / `POST /graph/suggestions/{id}/approve` / `GET /graph/suggestions/` の無認証 → 401 を実測した。
> **一時利用者の作成と password grant は Keycloak 管理者の資格情報を扱う作業であり、エージェントの権限規則
> （資格情報の入力・アカウント作成の禁止）に当たるため実行していない**（管理者パスワードの取り出しは
> 権限判定でも拒否された）。承認 → 反映（陽性）／辞書外（陰性）／資格なし（404）の実走手順は
> `msp1187-measure.sh`（`KC_ADMIN_PW` を環境変数で受け、終了時に一時ユーザー・クライアント・文書・タグ・提案行を消す）
> として PR 本文に添え、人が実走した結果を PR コメントへ残す。

## 計画書との差異

- 差異: なし。ADR-0063 決定 1〜5 をそのまま実装する。
- **生成段の読み取り主体**（issue #1014 ⑦「唯一の実装設計上の論点」）は [[IADR-0361]] 決定 2 で決めた
  （メッシュ内部 API・GraphService 自身が読む）。計画への裁定依頼は要らない（ADR-0063 が「新たな裁定を
  与えるのではない」と明記）。

## 未決事項

1. **リンク提案の行にも `canDecide` で表示を分けるか**（決定 5 の射程外。[[IADR-0361]] フォローアップ 1）。
2. **辞書が大きくなったときのプロンプト長**（値集合をそのまま渡す。上限を置くなら「どの部分集合を渡すか」の
   決めが要る。同 フォローアップ 2）。
3. **`/internal/tags/names` の残余リスク**（メッシュ内から無認証で辞書の名前を読める）は IADR-0299 と同じ受容。
