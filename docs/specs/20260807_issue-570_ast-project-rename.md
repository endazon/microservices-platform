---
title: 作業仕様書 AST の *.Worker → *.Api 改名に deploy 面（compose / MAPPING）を追随させる（#570）
type: spec
status: draft
related_ids: [NFR, FR-14, IADR-0067, IADR-0068, IADR-0070, IADR-0071, IADR-0072, IADR-0101]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
related_specs:
  - "../adr/IADR-0070_ast-frontend-integration.md"
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "../adr/IADR-0072_ast-monitor-bff-integration.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
---

# 仕様書: AST の `*.Worker` → `*.Api` 改名に deploy 面を追随させる（#570）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。運用・保守——CI が継承した赤の解消）。deploy 面の登録自体は
  **FR-14**（構成変更で完結する疎結合ユニット）に由来する
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [[IADR-0067]]（compose を単一情報源にしたイメージビルド CI ゲート）／
  [[IADR-0068]]（`k8s-local-images.sh` の MAPPING と compose のドリフト検査）／
  [[IADR-0070]]（AST 共有 Dockerfile を context/args 対応で deploy 面へ載せる）／
  [[IADR-0071]]／[[IADR-0072]]（同型で risk-management / market-monitor を登録）
- Issue: #570（継承した CI の赤）。原因を作った pin bump は **#564**
- 計画書リンク: 上記 `plan_refs`

## 目的・背景

`develop` の CI が 4 件赤い。`build (configuration-service)` / `build (market-monitor-service)` /
`build (risk-management-service)` の 3 ジョブと、集約ゲート `image-build`（3 件の失敗を映しているだけの派生）。

```
> [build 5/6] RUN dotnet restore "backend/Services/ConfigurationService/src/ConfigurationService.Worker/ConfigurationService.Worker.csproj":
0.326 MSBUILD : error MSB1009: Project file does not exist.
```

**#564 が submodule `src/ai-stock-trading` の pin を `655e2ed` → `91d52c2` へ上げた際、AST 側がサービスホスト
プロジェクトを `*.Worker` → `*.Api` へ改名し `*.Infrastructure` を新設していた**（AST/IADR-0128。ホストと
技術詳細の分離）。本リポジトリの deploy 面が旧名を書いたままであり、**4 件の赤は 1 つの原因に収束する**。

## 着手時の実測

### 実測 1: 改名は AST の 11 サービス全部（MSP が参照するのは 3 つ）

submodule は本作業ツリーに populate していないため、pin されたツリーを直接読んで数えた。

```console
$ GD=<repo>/.git/modules/src/ai-stock-trading
$ for c in 655e2ed 91d52c2; do echo "== $c"; \
    git --git-dir="$GD" ls-tree -d --name-only -r $c backend/Services/ \
    | grep -E 'src/[A-Za-z]+Service\.(Worker|Api)$' | sort; done
== 655e2ed
backend/Services/AuditService/src/AuditService.Worker
...（11 件すべて .Worker）
== 91d52c2
backend/Services/AuditService/src/AuditService.Api
...（11 件すべて .Api）
```

**AST 側は 11 ホストを一斉に改名している。** MSP の deploy 面に載っているのはうち 3 つ
（ConfigurationService / RiskManagementService / MarketMonitorService）なので、直す対象は 3 つで足りる。
ただし **compose のコメントが語る「10 Worker」という母集合の記述は、pin `91d52c2` では成り立たない**
（11 ホスト・全部 `*.Api`）ので併せて追随させる。

| サービス | 旧 pin `655e2ed` | 新 pin `91d52c2` |
| --- | --- | --- |
| ConfigurationService | Application / Client / Domain / **Worker** | **Api** / Application / Client / Domain / **Infrastructure** |
| RiskManagementService | Application / Domain / **Worker** | **Api** / Application / Domain / **Infrastructure** |
| MarketMonitorService | Application / Domain / **Worker** | **Api** / Application / Domain / **Infrastructure** |

### 実測 2: 実行時の契約は変わっていない（＝直すのは名前だけ）

| 検査 | 旧 | 新（`91d52c2` を実読） |
| --- | --- | --- |
| csproj の SDK | `Microsoft.NET.Sdk.Web` | **同一**（3 件とも `<Project Sdk="Microsoft.NET.Sdk.Web">`） |
| 待受 | `ASPNETCORE_URLS=http://+:8080` | **同一**（`backend/Dockerfile` は無変更。`EXPOSE 8080`） |
| ヘルス | `/health/live`・`/health/ready` | **同一**（3 件とも shim の `MapAiStockTradingHealthChecks` を呼ぶ） |
| アセンブリ名 | csproj 名と同一（`AssemblyName` の上書き無し） | **同一規則**（3 件の csproj に `AssemblyName` 無し）→ `<Service>.Api.dll` |

compose の `expose` / `environment` / `depends_on` / 接続文字列 / Helm values / BFF の既定下流
（`http://<service>:8080`）は**いずれも変更不要**である。

### 実測 3: 影響範囲は 6 ファイル（親の「8 箇所」より広い）

親は 8 箇所（compose 6 ＋ ADR 2）と数えたが、**少なくとも `scripts/k8s-local-images.sh` の MAPPING 3 行
（6 occurrence）が漏れている**。原因は数え方で、親の grep が `--include` に `*.yml *.yaml *.json *.md` しか
渡しておらず、**`.sh` と `.js` を母集合から外していた**ためである。母集合は `--exclude-dir` だけで取る。

```console
$ grep -rn "\.Worker" --exclude-dir=node_modules --exclude-dir=.git . \
    | grep -v "^\./src/ai-stock-trading" | grep -v "^\./planning"
```

| ファイル | occurrence | 種別 |
| --- | --- | --- |
| `deploy/docker-compose.yml` | 6（496/497/517/518/540/541） | **実体**（`build (<service>)` が落ちている当のもの） |
| `scripts/k8s-local-images.sh` | 6（47/49/51 の各行に PROJECT と DLL） | **実体**（k3d へのローカル配備。かつドリフト検査の相手） |
| `scripts/check-image-mapping.js` | 4（450/451/463/464） | 自己試験の**合成フィクスチャ**（compose 相当の文字列を貼ったもの） |
| `docs/adr/IADR-0070` / `IADR-0072` | 2（0070:44 / 0072:36） | **過去の記録**（当時の典拠引用。本文は書き換えない） |

さらに、`.Worker` という綴りに掛からないだけで**同じ陳腐化を起こしている記述**が 2 件ある。
どちらも「読み手が旧パスを追って空振りする」点で上表の ADR 2 件と同類のため、同じ形（日付つき追記）で扱う。

| ファイル | 記述 | 実測した現行値 |
| --- | --- | --- |
| `docs/adr/IADR-0071` 決定 3 / 根拠 | `risk-management-service` の build args ＋ 呼称「Worker」 | `RiskManagementService.Api`（登録の形は不変） |
| `docs/adr/IADR-0101` フォローアップ 5 | `TradeDecisionService.Worker/...` / `ReportService.Worker/...` の `MaxTokens: 1024` ハードコード（「必須・本リポジトリでは修正不可」） | **AST 側で消化済み**。2 箇所とも `MaxTokens: 4096`（コメントに `IADR-0101, MSP/ADR-0025`）。ファイルは `*.Infrastructure` へ移動 |

**`k8s-local-images.sh` は「漏れると別の赤を作る」種類の漏れである。** `check-image-mapping.js` は
compose の `build.args` と MAPPING の args を `argsEqual()` で突合するため（IADR-0070 決定 2）、
**compose だけ直すと `image-mapping` ジョブが `args-mismatch` で新たに赤くなる**。
実測でも着手前は両者が一致して green（`node scripts/check-image-mapping.js` → exit 0）であり、
片側だけ動かせばこの green が壊れる。

`src/knowledge/` 配下にも `*.Worker`（ConversionService / IngestionService）が多数あるが、**これは MSP 自身の
プロジェクトで AST とは無関係**であり対象外。

`docs/specs/20260730_issue-420-421_report-and-trade-model-routing.md:85` にも AST の旧パス
（`ReportService.Worker/Program.cs`）が残るが、**完了済み PR の作業仕様書＝その時点の作業記録**であり、
継続的に参照される決定記録（ADR）とは性質が異なるため触らない（過去の作業仕様書を現行値へ追随させ始めると
際限がない）。

### 実測 4: Helm values はサービス名しか持たない（無変更）

```console
$ grep -n "ConfigurationService\|RiskManagementService\|MarketMonitorService" \
    deploy/helm/microservices-platform/values.yaml
338:  # Issue #283, FR-17, UC-06, IADR-0070: ... BFF 先＝ConfigurationService。
353:  # Issue #287, FR-14, IADR-0071: ...
370:  # Issue #288, FR-14, IADR-0072: ...
```

**3 件ともコメント中の説明語で、プロジェクトパスも DLL 名も持たない。** chart はイメージ名
（`microservices-platform/<service>`）でしか参照しないため無変更とする（`deploy/create-multiple-dbs.sh`・
`deploy/keycloak/*.json`・BFF のコードも同様に compose のサービス名／論理名しか持たず無変更）。

## 対象範囲

### 対象

- `deploy/docker-compose.yml`: 3 サービスの `SERVICE_PROJECT` / `SERVICE_DLL` を `*.Api` へ（6 行）。
  併せて**旧構成を語るコメント**（「10 Worker」）を pin `91d52c2` の実態へ追随させる。
- `scripts/k8s-local-images.sh`: MAPPING の 3 エントリを同値に（3 行）。
- `scripts/check-image-mapping.js`: 自己試験フィクスチャの旧名（compose からの貼り付け）を追随（4 行）。
  **検査ロジックは変えない**（フィクスチャは合成文字列で、値は検査の成否に対して任意）。
- `docs/adr/IADR-0070` / `IADR-0071` / `IADR-0072` / `IADR-0101`: **本文は書き換えず**、日付つき追記で旧名の
  典拠が pin `91d52c2` で失効したことを記録する（本リポの先例: IADR-0117→0056 / 0122→0049 / 0123→0118 /
  0135→0131）。`updated:` のみフロントマターで前進させる。**IADR-0071 / IADR-0101 は親の指定（0070・0072）を
  超える**が、実測 3 のとおり同類の陳腐化であり、とくに IADR-0101 は**未消化と読める必須フォローアップが
  実は消化済み**という誤読を残すため記録する。不要と判断されればこの 2 件は落として構わない。

### 対象外（送り先を明記する）

| 対象外 | 理由 | 送り先 |
| --- | --- | --- |
| `src/ai-stock-trading` の pin | #564 が上げた pin は正しい。**戻さない**（本 issue は追随側の未修正） | — |
| `planning/` | `CLAUDE.md` の規約により実装ブランチで変更しない | — |
| Helm values / create-multiple-dbs.sh / realm json / BFF コード | 実測 4 のとおりプロジェクトパスを持たない | — |
| `src/knowledge/**/*.Worker` | MSP 自身のプロジェクト。AST の改名とは無関係 | — |
| 再発防止の機械化 | 本 PR では**実装しない**（後述「申し送り」） | 利用者判断 |
| 裸の AST 計画 ID（`compose:486` / `values.yaml:338` の `FR-17, UC-06`、`BffTestFactory.cs:152,199,237,559` の `SC-01`） | 規約は「裸の ID は必ず MSP を指す」と定めるが、これらは **AST の ID**（MSP の `FR-17` は知識グラフ探索、AST の `FR-17` は取引前提条件の一元管理）。**本 PR で 2 箇所だけ直すと母集合の一部だけが揃い、同型が残る** | **#576** |
| 空白区切りの `AST <ID>` 既存 12 箇所（`AST IADR-0048` 等） | 本 PR が新規に書いた 9 箇所は `AST/IADR-0128` へ揃えたが、既存分は射程外。**8 番号すべてが本リポジトリの同番号 IADR と衝突している** | **#576** |

## 設計

置換は 1 対 1 の機械的な写像で、`<Service>.Worker` → `<Service>.Api` のみ。

```
backend/Services/<S>/src/<S>.Worker/<S>.Worker.csproj  →  backend/Services/<S>/src/<S>.Api/<S>.Api.csproj
<S>.Worker.dll                                          →  <S>.Api.dll
```

`context`（`../src/ai-stock-trading`）・`dockerfile`（`backend/Dockerfile`）・環境変数・依存・ポートは不変
（実測 2）。**compose と MAPPING は同時に、同じ値へ**動かす（実測 3。片側だけだと `image-mapping` が赤くなる）。

## 受け入れ基準

- [ ] `deploy/docker-compose.yml` の 3 サービスが `*.Api` の csproj と `*.Api.dll` を指す
- [ ] `scripts/k8s-local-images.sh` の MAPPING 3 エントリが compose と同値
- [ ] `node scripts/check-image-mapping.js` / `--self-test` が exit 0（ドリフト無し）
- [ ] `node scripts/check-doc-links.js` が exit 0
- [ ] compose の YAML が構文的に妥当
- [ ] 旧名の残存が **ADR の本文（過去の記録）と、それを指す日付つき追記の引用だけ**になる
- [ ] `docs/adr/IADR-0070` / `IADR-0071` / `IADR-0072` / `IADR-0101` に日付つき追記があり、**本文は無改変**

## テスト方針

本作業はコードを伴わない構成変更のため、単体テストは追加しない。既存の機械検査に写像する。

| 受け入れ基準 | 検査 |
| --- | --- |
| compose ↔ MAPPING の同値 | `node scripts/check-image-mapping.js`（IADR-0068 の当の検査） |
| 検査器自体の健全性 | `node scripts/check-image-mapping.js --self-test` |
| compose の構文 | `python3 -c "import yaml; yaml.safe_load(open('deploy/docker-compose.yml'))"` |
| 純粋ロジックの退行なし | `node scripts/scripts.test.js` / `node scripts/k8s-local-up.test.js` |
| 文書リンク | `node scripts/check-doc-links.js`（＋ `--self-test`） |
| 置換漏れ | 実測 3 の grep（`--include` を絞らず母集合から数える） |

**実ビルド（`docker build`）はこの環境では走らせられない**（submodule 未 populate ＋ docker 不在）。
`build (<service>)` の green は CI での確認になる。ただし実測 1・2 で「新しいパスが pin されたツリーに実在し、
SDK・待受・ヘルス・アセンブリ名の規則が同一である」ことは確認済みで、restore の失敗要因は解消している。

## 計画書との差異

- 差異: なし。IADR-0070 決定 2（AST の実態＝単一 Dockerfile＋build args に deploy ツールを合わせる）を
  そのまま維持し、args の**値だけ**を実態へ追随させる。ADR の制約に触れる変更は無い。

## 未決事項・親への申し送り

1. **再発防止は本 PR では実装しない。** #570 のコメントで挙げた 3 案と評価を再掲し、**利用者判断に委ねる**。

   | 案 | 内容 | 評価 |
   | --- | --- | --- |
   | (a) | `image-mapping` に `SERVICE_PROJECT` の実在検査を足す | 軽く、既存の検査器に自然に載る。ただし **pin bump 以外の PR では空振り**（submodule を populate しない CI では検査自体が skip になる）。**根に届かない** |
   | (b) | submodule pin bump PR で `build (<service>)` を必須チェックにする（ブランチ保護） | **根に最も近い**（実ビルドが唯一の真の検査）。ただし**リポジトリ設定の変更であり、AI が勝手に触るべきでない** |
   | (c) | 何もしない | pin bump の頻度が低いなら妥当。今回のコストは 1 PR |

   (b) はブランチ保護＝リポジトリ設定であり、実装セッションの権限外・裁量外である。**採否は利用者が決める。**

2. **母集合の切り方が今回の見落としを作った。** 親の「8 箇所」は grep の `--include` から `.sh` を
   落としていた（実測 3）。**「置換漏れ検査」を `--include` で絞ると、絞った分だけ見えなくなる。**
   同種の追随作業では、まず `--exclude-dir` だけで全数を取り、そこから対象外を落とす順にする。

3. **AST 側の `backend/Dockerfile` のコメントも「10 の Worker」のまま**である（pin `91d52c2` で実測）。
   別リポジトリのため本 PR では触らない。AST へのフィードバックが要るなら別途。
