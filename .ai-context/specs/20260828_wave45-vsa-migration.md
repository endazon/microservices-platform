---
title: 作業仕様書 — 波 4.5（アーキ移送波）: 14 サービスを単一プロジェクト＋VSA/DDD 配置へ移す
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
author: claude
created: 2026-08-28
updated: 2026-08-28
related_adrs:
  - IADR-0027
  - IADR-0218
  - IADR-0280
  - IADR-0282
---

# 作業仕様書 — 波 4.5（アーキ移送波）

## 射程

`IADR-0282`（オーナー裁定 2026-08-28）が定めた樹形へ、**14 サービス全部**を移す。
`IADR-0280` の 8 要素プロジェクト（層 csproj 58 個）は撤去する。**振る舞いは 1 バイトも変えない。**

| 段 | 内容 |
| --- | --- |
| 0 | 基準例 1 サービス（FeedbackService）を自分で移送し、罠を洗い出してレシピ化する |
| 1 | 残り 13 サービスを 4 エージェントへ並列で出す（サービス単位で領域が非重複） |
| 2 | 共有ファイル（slnx・compose・MAPPING・props・baseline 群・pipeline.json・検査器）を一括で追随させる |
| 3 | 全数検証（両ユニットのビルド・テスト・format・検査器一式） |

**共有ファイルはエージェントに触らせない。** 4 体が同じ slnx と baseline を編集すると衝突が必ず起きる。
代わりに「触ってはいけないファイル」を明示し、統合時に私が 1 回で直した。

## 実測

| 母数 | 実測 |
| --- | ---: |
| 移送したサービス | **14**（knowledge 10 / platform 4） |
| 撤去した層 csproj | **58**＋`.gitkeep` 枠 |
| 移動した `.cs`（リネーム検出つき） | **544** |
| **新規追加した `.cs`** | **0** |
| テストプロジェクト | 19 本すべて緑・**件数は移送前と全一致**（knowledge 1057 / platform 987） |

## 🔴 罠（順に踏んだ。次にやる人はここを読む）

### 1. `Tests/` の入れ子は二重コンパイルになる

サービス直下に `Tests/` を置くと、SDK の既定 glob が `.cs` を拾って**本体側にもコンパイルされる**。
csproj へ `Compile` / `Content` / `None` の `Remove="Tests/**"` が要る（無いと CS0246 が大量に出る）。

### 2. XML コメントに `--` を書けない

`<!-- --project を指す -->` のような字面で MSBuild が **MSB4025** で落ちる。言い換える。

### 3. **`pipeline.json` の consumer 完全修飾名は起動を止める**

`AddPlatformWolverineStep` が `typeof(TStep).FullName` と**文字列一致**で検査し、不一致なら
**起動失敗**させる（規則 3）。名前空間が `Composable.Steps` → `Features.<集約>` へ変わるので、
**8 件すべてを直さないと 5 サービスが起動しない。** ビルドもテストも通るので**気づけない。**
値は**実装から採取**すること（宣言を先に書いて実装を合わせない）。

### 4. EF Migrations は型名の**文字列**を持っている

`Designer.cs` / `ModelSnapshot.cs` の `modelBuilder.Entity("<Svc>.Domain.X")` は文字列である。
据え置くと次の `dotnet ef migrations add` が**実体の無い DropTable / CreateTable** を生む。
本波で 59 ファイルが該当した（差分は引用符の中だけ）。

### 5. 同一名前空間で**暗黙に見えていた**型が見えなくなる

分割前は `using` を持たずに参照できていた型が、フォルダ（＝名前空間）を跨ぐと `CS0246` になる。
移送後は「元の名前空間内で見えていた型」を洗い直す必要がある。

### 6. `using` 走査では**相対名前空間参照**が捕まらない

`Foundation.Persistence.XDbContext` のような書き方は `using` 行に現れない。
`(^|[^.\w])(Foundation|Composable)\.` で引き直すこと。

### 7. `InternalsVisibleTo` はテストプロジェクト名を持つ

`<Svc>.Api.Tests` → `<Svc>.Tests` の改名に追随させないと `internal` が見えなくなる。

### 8. `XUnit1051Migrated` の改名漏れは**黙って ratchet を外す**

許可リストは名前一致なので、`<Svc>.Api.Tests` のままだと移送後のプロジェクトが一致せず、
`WarningsAsErrors` を失って `NoWarn` 側へ落ちる。**警告は出るが CI は緑**になる。
`check-xunit1051-ratchet.js` が props と baseline の不一致として捕まえる。

### 9. 🔴 `src/*` のグロブは submodule に届く

後片付けで `rm -rf src/*/backend/Services/<Svc>/src` を回したところ、`src/*` が
**`src/ai-stock-trading`（別プロジェクトの submodule）にも一致**し、AST 自身の
`NotificationService` の 67 ファイルを削除した。gitlink は動かしておらず、
コミット前に気付いて `git checkout -- .` で復元したが、**危うくコミットするところだった。**

**教訓: 「自分が持つ 2 ユニット」を意図しているなら、`src/knowledge` と `src/platform` を
明示的に列挙する。`src/*` は『`src/` の下の全部』であって、その中には他人のリポジトリが在る。**

## 判断が要った配置（原則と、割れた例）

レシピの決め手は「**その型が外部 I/O を持つか**」— 持てば `Infrastructure/`、持たなければ
`Domain/` か `Features/`。加えて **`IADR-0282` 決定 2 の参照方向に反したら置き場所のほうを見直す**。

- **ポートは原則 `Domain/Ports/`**。実装が `Infrastructure/` にあるため、`Features/` へ置くと
  `Infrastructure → Features` の逆流になる。
- 例外: NotificationService のポートは利用者が 1 スライスのみのため `Features/Notifications/` へ置いた。
  **SMTP リレーの実装が `Infrastructure/` に入った時点で `Domain/Ports/` へ引き上げが要る。**
- `RetrievalService.GraphRerank` は当初 `Domain/` へ置いたが、`HybridSearchService.RrfK`
  （`internal const`）への依存で `Domain → Features` になりビルドが落ちたため `Features/Search/` へ。
  **参照方向の規約が設計の誤りを実際に検出した例である。**

## GraphService の型ゲートについて（確認事項）

`AuthorizedNode` / `AuthorizedGraphView` / `UnfilteredSubgraph` は private ctor ＋ `internal` で
「非許可ノードからの展開が型として書けない」ことを保証している（`ADR-0034` / `IADR-0266`）。
**アセンブリ境界が変わればこの保証は変わり得る**ため確認した ——
**型ゲートの実コードは移送前から全て `GraphService.Api` アセンブリ内にあり**（層プロジェクトに在ったのは
パーサ・ポート・アダプタのみ）、境界は実質変わっていない。
`GraphTypeGateArchitectureTests` はリフレクションで private ctor・`Seal` のスコープ・
`UnfilteredSubgraph.IsPublic == false` を検査するもので、**「別アセンブリから見えないこと」には
依存していない**。250 件全緑で確認した。**設計は変えていない。**

## 検証

- `dotnet build` / `dotnet test` 両ユニット（19 テストプロジェクト・件数一致）
- `dotnet format --verify-no-changes` 両ユニット
- 検査器: `check-unit-dependencies`（VSA 層分類 6 → **336 件**）/ `check-event-topology` /
  `check-image-mapping` / `check-xunit1051-ratchet` / `check-backend-libraries` /
  `check-default-credentials` / `check-bff-downstreams` / `check-contract-schema` /
  `check-trace-blocks` / `check-adr-numbering` / `check-cross-repo-refs` /
  `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-route-manifest` /
  `check-test-spec-coverage` / `check-doc-links` / `gen-knowledge-graph --check`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 645 緑

**文書側は検査器が落ちて教えた** —— `check-test-spec-coverage` と `check-doc-links` が、
移動した `.cs` を指す live 文書を検出した。**指摘の 1 件ずつではなく旧樹形パスの側で全走査し**
（規則 1・9）、新パスを追跡下ファイルで一意に解決できたものだけ置換した（11 文書）。

## 残件（本波の射程外）

- **操作単位のスライス分割**（`Features/<集約>/<操作>/{Endpoint,Command,Handler}` の 3 分割）は
  していない。`IADR-0282` 決定 4 が「器の移送まで」と定めており、端点は集約フォルダ直下に 1 枚のまま。
- 太いエンドポイントのハンドラ化・値オブジェクト化・ドメインイベント導入も同様に別作業。
- `Properties/launchSettings.json` の**起動プロファイル名**は `<Svc>.Api` のまま
  （名前空間でもパスでもない表示ラベル。基準例に倣った）。
