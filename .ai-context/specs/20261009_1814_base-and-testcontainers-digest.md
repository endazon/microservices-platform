---
title: 作業仕様書 — 自製イメージの基底イメージと統合試験の Testcontainers のイメージを digest で固定し、検査器の走査を src/ へ広げる（#1814）
type: spec
status: done
related_ids: [NFR, ADR-0107, IADR-0514, IADR-0081, IADR-0315, IADR-0461]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3（digest 固定）・決定 5（点検）
issue: "#1814"
---

# 作業仕様書 — 基底イメージと Testcontainers のイメージの digest 固定（#1814）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0514 への日付つき追記**（［2026-10-09 追記 / #1814］）に置く
> （固定の表記・digest の種類・検知の仕組みは IADR-0514 決定 1・3 をそのまま使う。新しい決定は「走査の範囲を広げる」ことと
> 「基底イメージの取り込みの契機」だけで、独立した IADR を立てるほどの分岐ではない）。
> 基点は MSP `origin/develop` `049a34e5`。レジストリへは匿名の HEAD / GET（manifest）だけを送った。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0107 決定 3**（外部イメージは digest で固定する）・**決定 5**（年 1 回と契機の点検）。
- 非機能要件: 無採番（工程の統制。メタ作業の扱いは `.claude/rules/traceability.md`）。
- 起点 issue: **#1814**（#1787 / PR #1813 の独立監査で判明した残余）。先行: IADR-0514（deploy/ の固定と検査器）、IADR-0081（frontend の基底を `mirror.gcr.io/library` から引く）、IADR-0315（統合試験の Qdrant を配備と同じ参照で起こす）。

## 受け入れ基準（#1814）

- [x] **1.** Given `src/` の Dockerfile の `FROM` When `check-image-digests.js` を走らせる Then 基底イメージも digest 固定を要求される（または理由つき例外に載る）。
- [x] **2.** Given 統合試験の Testcontainers のイメージ参照 When 走査する Then digest 固定されている（または理由つき例外）。
- [ ] **3.** chart 由来の製品（Istio・ESO・Reloader・cert-manager・Argo CD・k3s）は planning#750 の裁定に従う —— **裁定前のため着手しない**（受け入れ基準どおり。本 PR は `Refs`、#1814 は開いたまま残す）。
- [x] 補足: 監査で判明した検査器の取りこぼしのうち、`COPY --from=<外部イメージ>`（と同型の `RUN --mount=…,from=<外部イメージ>`）・テンプレートに直書きした `default "<参照>"` を拾う。`*.Dockerfile`／`Containerfile` は PR #1813 で塞いだ（再確認のみ）。

## 設計

1. **走査の範囲**: `deploy/`（従来どおり）に加え、**`src/` の Containerfile（`Dockerfile`・`*.Dockerfile`・`Containerfile`）と C# ソース（`*.cs`）**を読む。
   - 除外: `src/ai-stock-trading`（別リポジトリの submodule。CI で取得されても対象外）、`node_modules`・`bin`・`obj`。
   - `src/` の YAML は読まない（`image:` を持つ配備物は `deploy/` にしか無い。`pnpm-lock.yaml` 等を誤読しないため）。
2. **Containerfile の追加の形**（#1814 補足）:
   - `COPY --from=<ref>`: 既知の段名（`AS <名>`）と段番号（数字）以外を外部イメージの参照として拾う。
   - `RUN --mount=…,from=<ref>`: 同上。
3. **テンプレートの直書きの既定値**（#1814 補足）: `{{ … }}` を含む `image:` 行の `default "<参照>"` で、値が `/` か `:` を含むもの（＝tag だけでなくイメージ全体）を拾う。`default "latest"`（自製の tag の既定）は拾わない。
4. **Testcontainers の参照**（C#）: 次の 3 つの文脈の文字列リテラルを拾う（イメージ参照の形〔空白なし・小文字〕のものだけ）。
   - `new <X>Builder("<ref>")`（`Testcontainers` を参照するファイルのみ。`StringBuilder` 等の非コンテナのビルダは除く）
   - `.WithImage("<ref>")`
   - `const string <…Image…|Reference> = "<ref>"`（`Testcontainers` に言及するファイルのみ。`QdrantTestImage.Reference`・`SeaweedFsContainer.Image` の形）
   - あわせて、**引数なしのモジュールのビルダ**（`new PostgreSqlBuilder()` 等）は「モジュール既定の tag だけのイメージ」を暗黙に使うので、参照として拾って落とす（Testcontainers 4.x では廃止予定でもある）。
   - コメント行（`//`・`///`・`*`）は読まない。
5. **固定の値**: 表記・digest の種類は IADR-0514 決定 1（`repo:tag@sha256:<multi-arch index digest>`）。tag は変えない。
   - `node:22-alpine`・`postgres:16-alpine` は **`deploy/` と同じ digest**（検査器の `[digest-mismatch]` が同一 `repo:tag` を突き合わせる。`${BASE_REGISTRY}/` の接頭辞と `mirror.gcr.io` は同一視される）。
6. **境界（IADR-0514 決定 2 の維持）**: 自製イメージ（`microservices-platform/*`・`k3d-local/*`）は引き続き対象外。基底イメージは自製ではない（上流が配る外部イメージ）ので対象に入る。

## 母集合（規則 9・10。`049a34e5` 時点）

### 規則 9 — 誤りの側（tag だけ）の文字列で全ファイルを走査

**(a) Containerfile の `FROM` / `COPY --from`**: `git ls-files | grep -iE '(^|/)(Dockerfile|Containerfile)(\.|$)|\.(Dockerfile|Containerfile)$'` → 17 本（`deploy/` 1・`src/` 16）。

| 置き場 | 参照 | 件数 | 処置 |
| --- | --- | --- | --- |
| .NET の Dockerfile 計 15 本（platform 5 本＝`src/platform/backend/{Bff/Platform.Bff,Services/{AuthorizationService,LlmGateway,McpServer,NotificationService}}/Dockerfile`、knowledge 10 本＝`src/knowledge/backend/Services/*/Dockerfile`） | `mcr.microsoft.com/dotnet/sdk:10.0` | 15 | **固定** `@sha256:e70cdb7f…06317` |
| 同上 | `mcr.microsoft.com/dotnet/aspnet:10.0` | 15 | **固定** `@sha256:222759b3…5ad4` |
| `src/platform/frontend/Dockerfile` | `${BASE_REGISTRY}/node:22-alpine` | 1 | **固定**（deploy と同じ `@sha256:0a7108bf…e402`） |
| 同上 | `${BASE_REGISTRY}/caddy:2.11-alpine` | 1 | **固定** `@sha256:d8542f48…f75f` |
| `deploy/local/platform-backup/image/Dockerfile` | `${BASE_REGISTRY}/postgres:16.15-alpine3.24@…` | 1 | 既に固定（#1787 以前から） |
| `src/platform/backend/Services/LlmGateway/Dockerfile` | `FROM build AS publish`・`FROM base AS final` | 2 | 段名（対象外） |
| 全 Containerfile の `COPY --from=` | `build`・`publish` | 16 | 段名（対象外）。外部イメージを `--from` で引く行は 0 件 |
| `RUN --mount=…from=` | — | 0 | — |

**(b) Testcontainers のイメージ**: `git grep -nE 'WithImage\(|(PostgreSql|RabbitMq|Redis|Keycloak|Qdrant|Minio|Container)Builder\('` と `git grep -nE '"(postgres|rabbitmq|redis|qdrant/qdrant|chrislusf/seaweedfs)[:@]'`（`src/ai-stock-trading` を除く）。

| 置き場 | 参照 | 処置 |
| --- | --- | --- |
| `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Fixtures/PostgresFixture.cs` | `new PostgreSqlBuilder("postgres:16-alpine")` | **固定**（deploy と同じ `@sha256:721873c3…80ea`） |
| `…/DataSourceService/DataSourceSyncSingleWriterTests.cs` | 同上 | **固定**（同じ digest） |
| `…/Fixtures/RabbitMqFixture.cs` | `new RabbitMqBuilder("rabbitmq:3.13-alpine")` | **固定** `@sha256:d7af1c87…52bc`（配備は `3.13-management-alpine`。tag が違うのは IADR-0315 の母集合表 E2 のとおり既存の選択で、本件では変えない） |
| `…/Fixtures/QdrantTestImage.cs` | `const string Reference = "qdrant/qdrant:v1.18.1@…"` | 既に固定（#1787） |
| `…/Fixtures/SeaweedFsContainer.cs` | `const string Image = "chrislusf/seaweedfs:4.47@…"` | 既に固定（IADR-0461） |
| `Search/{IngestToSearch,KeywordIndex,LexicalIndex}QdrantTests.cs` | `new QdrantBuilder(QdrantTestImage.Reference)` | 定数経由（定数の側で検査） |
| 引数なしのモジュールのビルダ | — | 0 件 |
| `templates/unit-template/…/HealthEndpointTests.cs` | コメントで Testcontainers に触れるだけ | 参照なし |

**(c) テンプレートの直書き `default "<参照>"`**: `grep -rn 'default "' deploy/helm --include=*.yaml` → `image:` 行の `default` は `{{ $f.tag | default "latest" }}`・`{{ $svc.tag | default "latest" }}`（自製の tag の既定。イメージ全体ではない）の 2 件だけ。拾う対象 0 件。

**(d) 母集合の外（理由つき）**:

| 置き場 | 参照 | 理由 |
| --- | --- | --- |
| `src/ai-stock-trading`（submodule）の Dockerfile・Testcontainers | `postgres:16` 等 | 別リポジトリ（AST）。本リポで変えない |
| `scripts/backup-restore-drill.sh:32` | `DEFAULT_IMAGE="postgres:16-alpine"` | 手で起動する復元訓練のスクリプトで、#1814 の受け入れ基準の母集合（`src/` の Dockerfile・統合試験）の外。`--image` で上書きできる。`scripts/` の製品は planning#750 の裁定とまとめて扱う（残余） |
| `scripts/verify-qdrant-attribute-payload.sh:18` | コメント中の `docker run … qdrant/qdrant:v1.18.1` | 手順の例示（コメント） |
| `scripts/` が chart / マニフェストで入れる製品 | Istio・ESO・Reloader・cert-manager・Argo CD・k3s | 受け入れ基準 3（planning#750 の裁定待ち） |
| `.devcontainer/devcontainer.json` | `mcr.microsoft.com/devcontainers/dotnet:10.0` | 開発環境（devcontainer）のイメージで、配備物でも統合試験でもない。検査器は `.devcontainer/` を走査しない（#1814 の独立監査で追記） |
| `.github/workflows/*.yml` の `services:`・`container:` | — | 0 件（`docker run` は自製の `platform-backup:ci` だけ） |
| Testcontainers の Ryuk（後始末のコンテナ） | `testcontainers/ryuk:0.14.0@sha256:7c1a8a9a…` | ライブラリ（Testcontainers）が内部で決める参照で、本リポのソースに現れない。実走で digest 付きで起動されることを確かめた（下の §検証の結果） |

### 規則 10 — この変更で新たに誤りになる自分の記述

`grep -rn 'deploy/ のインフラ\|deploy/ だけ\|deploy/\` だけ' scripts/check-image-digests.js scripts/README.md docs/operations/operations.md .github/workflows/ci.yml` で引いた。

- `scripts/check-image-digests.js` の冒頭コメント（「`deploy/` のインフラのイメージ」「`deploy/` 配下の…」「ネットワークを使わず `deploy/` だけを読む」）→ 範囲を `deploy/` と `src/` に直す。
- `scripts/README.md` の行 → 拾う形と範囲を足す。
- `.github/workflows/ci.yml` のステップのコメント（「deploy/ のインフラのイメージ」）→ 直す。**ジョブ名・起動条件・必須チェックは変えない**（ステップ名 `Check image digests (#1787)` も据え置く。`scripts.repo.test.js` は `run:` 行で配線を見ている）。
- `docs/operations/operations.md` §Third-party イメージ（置き場の列挙が `deploy/` だけ）と §インフラ製品の点検 の母集合の行（「`deploy/` の参照から機械的に引く」）→ 範囲と、基底イメージの取り込みの契機を足す。
- IADR-0514 決定 3 の「検査器はネットワークを使わず `deploy/` だけを読む」・残余 2 →［2026-10-09 追記 / #1814］で範囲の拡張と残余 2 の解消を書く（本文は書き換えない）。
- `scripts/scripts.repo.test.js` の検査器の母集合の件数（ラチェット）→ 検査器を新設しないので**変わらない**（数え直して確かめる）。

## 配備への影響（#1822 の「場中を避ける」規則に当たるか）

- **当たらない。** #1822 の規則は **platform-infra の Pod（Postgres・RabbitMQ・Keycloak 等）が作り直されるインフラのイメージ参照の変更**が対象である。本件は `deploy/` のインフラの参照を 1 件も変えない。
- 自製イメージの基底イメージの固定は、**自製イメージを次に CD でビルドしたとき**に効く（アプリの Pod の通常のロールアウト。platform-infra は作り直されない）。digest は 2026-10-09 時点の tag の中身と同じなので、ビルドの結果は固定前に今日ビルドしたものと変わらない。
- Testcontainers のイメージは CI と開発者の手元の統合試験だけで使う。配備物ではない。
- よって **PoC の再配備は要らない**（次の通常の自製イメージの配備で自然に取り込まれる）。

## 検証

- `node scripts/check-image-digests.js --self-test` / `node scripts/check-image-digests.js` / `--list`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `node scripts/check-deploy-manifests.js`（`deploy/` は変えないが回帰確認）
- `dotnet build` / `dotnet format --verify-no-changes`（knowledge の slnx。変更は統合試験の 3 ファイル）
- Testcontainers の試験の実走は、実行機に Docker デーモンが無ければ CI（`integration.yml`）に委ねる（結果は PR 本文に書く）
- `check-trace-blocks` / `check-adr-numbering` / `check-doc-updated --base origin/develop` / `check-commit-messages` / `check-cross-repo-refs` / `check-plan-id-qualification` / `gen-knowledge-graph --check`

## 検証の結果（2026-10-09）

- `check-image-digests.js --self-test` 40 件通過（#1814 で 8 件、独立監査の是正で 5 件を追加）。本検査: **24 製品・79 参照**すべて固定（自製 21 件は対象外。#1787 時点の 21 製品・42 参照から、基底イメージ 32・Testcontainers 5 を加えた）。例外 0 件。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 通過（#1814 で 3 件追加）。
- `dotnet build`（`Knowledge.IntegrationTests`）警告 0。`dotnet format --verify-no-changes`（変更した 3 ファイル）差分なし。
- 実行機で Docker デーモンを起こし、統合試験を実走した: `DataSourceSyncSingleWriterTests`（Postgres）・`WolverineBrokerReadinessTests`（RabbitMQ）・`DocumentCrudTests`（Postgres＋RabbitMQ）・定義試験（Qdrant・SeaweedFS）の計 16 件が通過。`docker events` で、起動したコンテナの参照が `postgres:16-alpine@sha256:721873c3…`・`rabbitmq:3.13-alpine@sha256:d7af1c87…` であることを確かめた。
- 基底イメージ 4 種（`mcr.microsoft.com/dotnet/{sdk,aspnet}`・`mirror.gcr.io/library/{node,caddy}`）を digest で `docker pull` できた。`docker buildx build --check`（frontend・LlmGateway・WikiService）は警告なし。自製イメージ全体のビルドは CI（`images.yml`）に委ねる。

## 独立監査（PR #1828）の是正（2026-10-09）

判定 GO（🔴 0）。🟡・🟢 を同じ PR で直した。

| 指摘 | 処置 |
| --- | --- |
| 🟡1 `new PostgreSqlBuilder().WithImage("…@sha256:…")` を既定のイメージとして誤検知 | 直した（同じ式に `.WithImage(` があれば除外）。自己試験 1 件 |
| 🟡2 `global using`・csproj の `<Using Include>`・完全修飾名 | 直した（ビルダ名の許可リストで判定し、`using` に依らない）。自己試験 |
| 🟡2 名前付き引数 `image: "…"` | 直した（ビルダと `WithImage` の両方）。自己試験 |
| 🟡2 `static readonly string …Image = "…"` | 直した。自己試験 |
| 🟡2 target-typed の `new("…")` | 既知の限界（IADR-0514 の追記に列挙）。自己試験で「拾わない」を固定 |
| 🟡3 行継続した `RUN \` の次の行の `--mount=…,from=` | 直した（論理行で読む）。自己試験 |
| 🟡3 行末の `//` コメント内の `new RedisBuilder()` を誤検知 | 直した（行末・ブロックコメントを空白化。文字列・文字リテラルは残す）。自己試験 |
| 🟡3 `AuthorizationPolicyBuilder("…")` を誤検知（拒否リスト） | 直した（許可リスト化）。自己試験 |
| 🟢 frontend の Dockerfile の `BASE_REGISTRY` の上書き | 「同じ index digest を返すミラーに限る」をコメントに足した |
| 🟢 表 (d) に `.devcontainer/devcontainer.json` | 足した |
| 🟢 表 (a) の「（10 本）」 | 「計 15 本（platform 5 本、knowledge 10 本）」に直した |
| 🟢 運用仕様書の括弧の掛かり方・余分な空白 | 新しい文を手順の括弧の後ろへ移し、「引け後」の項目の追記を項目の末尾へ移した |

## digest の解決と確かめ方（2026-10-09）

- 匿名のトークン（Docker Hub は `auth.docker.io`。`mcr.microsoft.com`・`mirror.gcr.io` はチャレンジ無し）→ `HEAD /v2/<repo>/manifests/<tag>`（Accept に OCI index と Docker manifest list）→ `docker-content-digest`。
- 確かめ: 得た digest で `GET /v2/<repo>/manifests/<digest>` を取り直し、**本文の sha256 が digest と一致**し、`mediaType` が index / manifest list で、複数のプラットフォーム（少なくとも `linux/amd64`・`linux/arm64`）を持つことを確かめた。`node`・`caddy` は `mirror.gcr.io/library`（frontend の既定の `BASE_REGISTRY`）と `registry-1.docker.io` の両方で同じ digest が返ることを確かめた。

## 範囲外・残余

- 受け入れ基準 3（chart 由来の製品）: planning#750 の裁定待ち。#1814 は本 PR では閉じない。
- `scripts/backup-restore-drill.sh` の `DEFAULT_IMAGE`（上表 (d)）。
- 浮動 tag（`10.0`・`22-alpine`・`2.11-alpine`・`16-alpine`・`3.13-alpine`）の自動パッチは止まる（IADR-0514 決定 1 と同じトレードオフ）。基底イメージは**アプリの実行環境のセキュリティ修正**を運ぶので、取り込みの契機を運用仕様書に足す（IADR-0514 追記）。
- 単独参照の誤った digest と、tag 表記の揺れる組（`postgres:16-alpine` と `16.15-alpine3.24`）は検査器の構造上の限界のまま（#1814 のコメントの対策案 (1)(2)）。本件では手を付けない（`--verify-remote` のようなネットワーク検査の要否は別に判断する）。
