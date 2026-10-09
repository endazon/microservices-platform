---
title: 作業仕様書 — キャッシュ・セッションストアを Redis から Valkey へ差し替え、認証と到達の制限を必須にする（#1839・planning#750）
type: spec
status: done
related_ids:
  - NFR-18
  - ADR-0131
  - ADR-0107
  - ADR-0112
  - ADR-0032
  - ADR-0030
  - IADR-0522
  - IADR-0251
  - IADR-0316
  - IADR-0510
  - IADR-0514
  - IADR-0461
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0131_cache-session-store-valkey.md 決定 1〜5（決定 4 の受入条件 1〜5）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 基準 A〜C
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1（基準 D）
related_specs:
  - 20261008_1787_infra-audit-digest-pin
  - 20261009_1841_disable-default-egress
  - 20261008_1793_argv-secrets-to-stdin
issue: "#1839"
---

# 作業仕様書 — キャッシュ・セッションストアを Valkey へ差し替える（#1839）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `f4632f7b`。
> 計画は project-planning `142c3e4`（隣接クローン・読み取り専用）の ADR-0131 を読んだ。
> 🔴 **稼働中のクラスタには何も実行しない。** 実イメージの確認は手元の Docker（Testcontainers と `docker run`）で行う。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0131**（製品を Valkey とする。決定 3 で版を実装の IADR に委ね、決定 4 で受入条件 1〜5 を定める）。
- 選定基準: ADR-0107（基準 A ライセンス・B 配布・C 既定の外部通信）・ADR-0112 決定 1（基準 D 既定で開く管理用の口）。
- セッションの設計（覆さない）: ADR-0032・IADR-0251（決定 4 セッションの実体・決定 5 鍵リング）・IADR-0510（鍵リングの保存先）・IADR-0316（セッションの配備設定）。
- 非機能要件: **NFR-18**（シークレット管理。新しいパスワードを Secret から注入する）。
- 実装 IADR: **IADR-0522**（本件で起こす。差し替えの判断と受入条件の結果）。

## 受け入れ基準

ADR-0131 決定 4 の受入条件をそのまま写す。

- **AC1（受入条件 1）**: BFF の 3 用途 —— セッションの保存と失効（全セッションの即時失効を含む）、DataProtection の鍵リングの共有、
  SC-22 の書き込み記録 —— が **Valkey の実イメージ（digest で固定したもの）で通る**。クライアントは StackExchange.Redis のまま。
  🔴 既存の BFF の試験は記憶域を偽物（`MemoryDistributedCache`・プロセス内のリスト）へ差し替えており、**実イメージには届かない**。
  そこで既存の試験と同じ操作（`RedisTicketStoreTests`・`BffKeyRingSharingTests`・SC-22 の記録）を、**本番の配線
  （`AddBffSession`）のまま実イメージに向けて回す統合試験**を足す（`Category=Integration`。PR の CI では走らず、`integration.yml` が全量で回す）。
- **AC2（受入条件 2・基準 D）**: 認証（`requirepass`）を必須にする。パスワードが空ならサーバは**起動しない**（fail-closed）。
  パスワードは Secret（経路 B）・環境変数（compose）から入れ、**プロセスの引数に載せない**（#1793 の作法）。
  到達を制限する —— compose はホストへの 6379 の公開をやめる。経路 B は NetworkPolicy で BFF の Pod だけに絞り、egress を閉じる。
  NetworkPolicy を強制しないクラスタでは防御が認証の 1 段になることを記録する。
- **AC3（受入条件 3・基準 B）**: イメージを multi-arch の index の digest で固定する（`check-image-digests.js` が緑）。匿名で 2 回解決して一致すること。
- **AC4（受入条件 4・基準 C）**: 既定の外部通信の有無を確かめて記録する。有れば計画の 08_data-egress-policy への環流を起こす。
- **AC5（受入条件 5）**: 既存データの扱いを記録する（経路 B は揮発、compose は dev のデータのみ）。
- **AC6（否定形）**: セッション・鍵リング・SC-22 の設計（キー名・アプリ名・型名 `RedisTicketStore` 等）は変えない。
  ライブラリ（StackExchange.Redis・`Microsoft.Extensions.Caching.StackExchangeRedis`・`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`・
  `AspNetCore.HealthChecks.Redis`）は変えない（ADR-0131 決定 2 が改名を求めない）。
- **AC7**: パスワードの注入は `check-secret-injected-options.js`（helm は secretKeyRef、compose は変数展開）が機械で守る。

## 設計

| 論点 | 決め | 理由 |
| --- | --- | --- |
| 版 | **Valkey 9.1**（`valkey/valkey:9.1-alpine` → 9.1.2） | 決定 3 が実装に委ねた。保守の期限は 9.1 が 2031-05-19、8.1 が 2030-03-31（計画の実測 4）。新規の採用で長い方を取る |
| 名前 | サーバは製品名 `valkey`（compose のサービス・Deployment・Service・ExternalName・volume）。資格情報は製品名を持たない `session-store-credentials`（キー `password`）・環境変数 `SESSION_STORE_PASSWORD` | IADR-0461 決定 3 と同じ（現物は製品名、サーバとクライアントが読む資格情報は役割の名前） |
| 接続先 | `BffSessionOptions.RedisConnectionString` の既定を `valkey:6379` へ改める。**配備で上書きしない**（IADR-0316 の判断を保つ） | 既定が compose（サービス名）と経路 B（MSP ns の ExternalName）の実態に一致する |
| パスワード | 新しい構成値 `BffSession:RedisPassword`（既定は空）。XML doc で「k8s Secret から環境変数で注入する」と宣言し、helm は secretKeyRef、compose は `${SESSION_STORE_PASSWORD:-…}` で注入する | 接続文字列にパスワードを埋めると、Secret 由来の値と既定の接続先が 1 つの文字列に混ざり、検査器（`check-secret-injected-options.js`）が見られない |
| ヘルスチェック | `Redis:ConnectionString`（ヘルスチェック専用の 2 つ目の置き場）を廃し、セッションと同じ `BffSession` の値から組む | 認証を足すと 2 か所にパスワードが要る。置き場を 1 つにしないと、片方だけ通る状態が作れる |
| サーバの起動 | `sh -c` でパスワードの空を拒み、`requirepass` を **here-document で `valkey-server -`（標準入力の設定）へ渡す** | `--requirepass <値>` は `ps` に載る（#1793）。イメージの entrypoint（`docker-entrypoint.sh`。root なら `valkey` 利用者へ落とす）を経由する |
| readiness / healthcheck | `VALKEYCLI_AUTH` を環境変数で与えて `valkey-cli ping` の応答が `PONG` であること | 認証なしの `ping` は `NOAUTH` を返しても終了コード 0（実測）。応答の文字列で判定する |
| compose の到達 | `ports` を外す（ホストへ公開しない） | ADR-0131 決定 4 の 2「公開をやめる、または絞る」の強い方。BFF は compose のネットワークで届く。手元の `dotnet run` から直接つなぐ経路は従前から既定の接続先（`redis:6379`）がホストで解決できず成り立っていなかった |
| 経路 B の到達 | `platform-infra` に NetworkPolicy `valkey-ingress-bff-only`（ingress は MSP ns の `app: bff-service` の Pod から 6379 だけ、egress は 0 件＝全拒否） | 基準 D。egress を閉じるのは基準 C の補強（送る先が無い） |
| 経路 B のパスワード | `SESSION_STORE_PASSWORD` を与えたらその値 ＞ 既存の Secret の値 ＞ 起動のたびではなく**初回だけ**乱数で生成。`platform-infra` と MSP ns の 2 か所へ同じ値を置く。ESO=1 でも手動で置く（infra の基盤 secret と同じ bootstrap の扱い） | 公知の dev 既定値を経路 B へ持ち込まない。既存値を使い回すのは、Valkey が起動時にしか設定を読まず、値を変えると BFF と食い違うため |
| compose のパスワード | `${SESSION_STORE_PASSWORD:-session-store-dev-password-change-me}`（他の dev 既定と同じ形） | compose は dev 限定で、ホストへ公開しない。既定値は鍵に見えない文字列（gitleaks） |
| 旧 Redis の後片付け（経路 B） | `k8s-local-up.sh` が `deploy/redis`・`svc/redis`（platform-infra）と ExternalName `svc/redis`（MSP ns）を `--ignore-not-found` で消す | kustomize の apply は消えたリソースを刈らない。**認証なしの Redis が既存クラスタに残り続ける**と、基準 D の穴が塞がらない |
| 旧 Redis の後片付け（compose） | 手順書で `docker compose up --remove-orphans` と旧 volume `redis-data` の削除を案内する | compose は孤児を自動では消さない |
| 既存データ | 移行しない。compose は新しい volume `valkey-data` を使い、旧 `redis-data` は読まない | 中身はセッション・鍵リング・SC-22 の書き込み記録だけ（dev）。失うと全員が再ログインになり、SC-22 の「最終更新者」が「記録なし」に戻る。Redis 7.4 の RDB を Valkey が読める保証も無い |
| 統合試験の image | `Platform.Bff.Tests` の `ValkeyTestImage.Reference` に compose・経路 B と同じ参照を置く。**一致は定義の試験（Docker 不要・PR で走る）が突き合わせる** | `check-image-digests.js` は tag と digest の対の一致を見るが、試験の定数が配備と別の版へずれることまでは止めない（IADR-0461 の `SeaweedFsContainerDefinitionTests` と同じ形） |

## 母集合（規則 9・10。`f4632f7b` 時点）

### 走査

`git grep -il 'redis'`（追跡下の全ファイル。`CHANGELOG.md` を除く）で **93 本**、あわせて `git grep -n '6379'` を引いた。
誤りの側の字面（`redis:7-alpine`・`redis-cli`・`deploy/redis`・`redis:6379`・`Redis:ConnectionString`・`Redis__ConnectionString`・`redis-data`・
`6379:6379`）で引き直し、残りを読んで分類した。

### 対象（直す）

| # | 箇所 | 直し方 |
| --- | --- | --- |
| 1 | `deploy/docker-compose.yml` の `redis` サービス（image・ports・healthcheck・volume）・BFF の `Redis__ConnectionString`・`depends_on`・volume `redis-data` | `valkey` へ。認証・起動の拒否・公開の撤去。BFF へ `BffSession__RedisPassword` |
| 2 | `deploy/local/infra/redis.yaml`・`kustomization.yaml` | `valkey.yaml` へ改名。Secret 由来の env・起動形・readiness・NetworkPolicy |
| 3 | `deploy/local/aliases/microservices-platform-externalnames.yaml` の `redis` | `valkey` |
| 4 | `deploy/helm/.../templates/deployment.yaml`（BFF の session ブロックの注記）・`values.yaml`（`services.bff.session`） | `BffSession__RedisPassword` を secretKeyRef で注入。Secret 名・キーを values に持つ |
| 5 | `scripts/k8s-local-up.sh`（`rollout status deploy/redis`・Secret の作成・旧 Redis の後片付け・冒頭の env の一覧） | 上の設計どおり |
| 6 | `scripts/check-bff-multi-replica-session.js`（`exec deploy/redis -- redis-cli LLEN`） | `deploy/valkey` で、パスワードは Pod の中の env から読ませる（ホストの argv に載せない） |
| 7 | `scripts/scripts.repo.test.js` #1787 の変異試験（`image: redis:7-alpine` の行を前提にしている） | `valkey/valkey:9.1-alpine` の行で変異させる |
| 8 | `scripts/k8s-local-up.test.js`（実行環境から漏れると既定が変わる env の一覧） | `SESSION_STORE_PASSWORD` を足し、Secret の作成の試験を足す |
| 9 | BFF: `BffSessionOptions.cs`・`BffSessionExtensions.cs`・`Program.cs`・`appsettings.json`・`appsettings.Development.json` | 既定の接続先・パスワード・ヘルスチェックの置き場の一本化 |
| 10 | BFF の試験: `BffTestFactory.cs`（`Redis:ConnectionString`）・`Platform.Bff.Tests.csproj` | 構成キーの追随。`Testcontainers` の参照と統合試験・定義の試験を足す |
| 11 | 文書（`docs/`）: `operations/operations.md`（依存イメージの列挙・揮発の注記・点検の記録）・`operations/bff-multi-replica-session-runbook.md`（`exec deploy/redis`・接続先の既定・揮発の注記）・`operations/secret-item-live-sync-check-runbook.md`（記録の置き場）・`security/security.md`（新節）・`how-to/local-development.md`（compose の列挙）・`tech/system-architecture.md`（構成図）・`tests/TEST_STRATEGY.md`（Testcontainers の列挙） | 製品名と手順を Valkey に。trace ブロックへ ADR-0131・IADR-0522・#1839 |
| 12 | `deploy/local/README.md`（ns の構成図・揮発の注記 2 か所）・`deploy/istio/README.md`（依存の列挙） | 製品名を Valkey に |
| 13 | `.ai-context/adr/README.md`（索引） | IADR-0522 の行を足す |

### 除外（直さない）と理由

| 除外 | 理由 | 外す条件 |
| --- | --- | --- |
| 型名・パッケージ名（`RedisTicketStore`・`RedisXmlRepository`・`InMemoryRedisLists`・`DataProtectionKeysRedisKey`・`RedisConnectionString`・`AddStackExchangeRedisCache`・`AspNetCore.HealthChecks.Redis`・`Testcontainers.Redis`（`Directory.Packages.props`）） | ADR-0131 決定 2「実装の型名・ライブラリ名は製品名ではない。改名を求めない」 | 無し（決定が変わらない限り） |
| コード・試験・スクリプトの注記の「Redis」（`BffSessionExtensions.cs` の鍵リングの説明・`SecretWriteRecordStore.cs`・`AuthBffEndpoints.cs`・`SessionTokenRefresher.cs`・`BackchannelLogoutProcessor.cs`・`InMemoryIdentityAdminClient.cs`・試験の注記・`check-bff-multi-replica-session.js` の文言・`verify-oidc-edge-flow.sh` の注記） | 役割（RESP のセッションストア）を指しており、決定 2 の「Redis 互換（Valkey）と読む」に当たる。改めると差分が設計判断の無い字面の置換で埋まる | 無し |
| `docs/tests/NFR-09_bff-edge-authentication.md`（「偽 Redis」「鍵の Redis キー」） | 試験の器（StackExchange.Redis の `IDatabase` の偽物）とキーの説明であり、製品の名指しではない | 無し |
| `docs/tech/tech-requirements.md` の「HybridCache（L1）+ Redis（L2）」 | 計画の確定スタック（ADR-0030）の写しであり、決定 2 が「書き換えず Redis 互換と読む」と定める。キャッシュの L2 は未実装（計画の実測 7） | 計画の 03_tech-stack-selection の行が改まったら追随を検討 |
| `scripts/check-image-digests.js` の自己試験の `redis:7` 等の例 | 抽出器の試験の入力文字列で、配備の参照ではない | 無し |
| `templates/unit-template/.../SampleService.Tests.csproj` の注記（Testcontainers の列挙） | 雛形の利用者が選ぶ部品の例。本リポジトリの配備ではない | 無し |
| `scripts/README.md` の `check-bff-multi-replica-session.js` の説明 | 「鍵リングを Redis に共有」の役割の説明（上の注記と同じ） | 無し |
| `docs/operations/operations.md` の点検の記録（2026-10-08 の表の redis 行・Argo CD の同梱の redis） | 記録は回ごとに足し、過去の回は書き換えない（同節の記録先の規則）。Argo CD の同梱の redis は別製品（本件の対象外） | 無し |
| `docs/how-to/plan-id-range-history-annex.md`・`docs/api/openapi.yaml`・`src/platform/frontend/.../auth.ts`・`LICENSE` | `redistribute` 等の部分一致、または製品を指さない（openapi・auth.ts はセッションの説明文。生成物） | 無し |
| `.ai-context/`（既存の IADR・作業仕様書・superpowers） | 凍結記録 | 無し |
| 本番像の chart に Valkey の Deployment を足すこと | ADR-0131 実測 8・決定 5（本番像には無い。足すなら差し替えの後）。本件は差し替えそのもの | 本番像へセッションストアを足す issue |
| ESO（Vault → ExternalSecret）での `session-store-credentials` の供給 | 起動器が ESO の有無によらず手で置く（infra の基盤 secret と同じ bootstrap）。Vault の KV・ExternalSecret・数え直しの規則を増やす理由が本件に無い | 本番像へセッションストアを足すとき、または SC-22 の投入対象に加えるとき |

### 規則 10（この変更で新たに誤りになる自分の記述）

- IADR-0316 の表「`RedisConnectionString` は既定 `redis:6379`」は凍結記録なので書き換えない。後継の判断は IADR-0522 に置き、IADR-0316 を引く。
- `docs/operations/bff-multi-replica-session-runbook.md` の「接続先はコード既定の `redis:6379`」は本件で誤りになる → 直す（対象 11）。
- 運用仕様書の点検の記録の「redis と vault の差し替えは計画の裁定を待つ」は、差し替えの記録を新しい回として足し、元の行は残す。
- 導出値: compose・経路 B の参照の数（`check-image-digests.js --list` の valkey の行）は走査し直して書く。

## 手順

1. BFF の構成（パスワード・既定の接続先・ヘルスチェックの一本化）と試験の追随。
2. 配備（compose・経路 B・helm）とスクリプト（起動器・多レプリカ検査）。
3. 実イメージの統合試験と定義の試験（AC1・AC3）。手元の Docker で回す。
4. 文書・IADR-0522・索引。
5. 検証（下記）。

## 検証

- `dotnet build` 両ユニット（警告 0）・`dotnet test`（Platform.Bff.Tests は統合試験込みで Docker 上で回す）・`dotnet format --verify-no-changes`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/k8s-local-up.test.js`・`node scripts/check-deploy-manifests.js`・
  `check-image-digests`・`check-secret-injected-options`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・
  `check-plan-id-qualification`・`check-doc-updated --base origin/develop`・`check-commit-messages --base origin/develop`。
- 実イメージでの確認（AC2・AC4）: パスワードが空なら起動しない／認証なしの `ping` が `NOAUTH`／`ps` にパスワードが出ない／起動から 130 秒の
  コンテナのソケット表に loopback 以外の接続が無い（手元の `docker run`）。

## 結果

- 受入条件 1〜5 の結果は IADR-0522 に記録した（同 IADR §受入条件の結果）。
- IADR の採番: develop の最大は IADR-0520、PR #1855 が IADR-0521 を持つため本件は **IADR-0522** とした。#1855 のマージまで欠番検査は赤になる（想定どおり）。

## ［2026-10-09 追記 / #1860］独立監査（条件付き GO）の指摘の是正

| 指摘 | 是正 |
| --- | --- |
| 🟡1 構成へのパスワードの伝搬に PR で回る試験が無い | `BffSessionStoreConfigurationTests`（Docker 不要）を足した。載る・空なら載らない・区切りを含む値が欠けない・呼ぶたびに別の実体 |
| 🟡2 パスワード変更時の作り直しの順序 | 起動器が明示指定で値が変わったときだけ Valkey（[4/7] の後）→ BFF（[6/7] の後）の順に作り直す。`k8s-local-up.test.js` が順序と陰性対照 3 通りを固定。運用仕様書に差し替えの手順を置き、秘密情報のローテーションの Runbook の「扱わない秘密」から引いた。helm の checksum 注釈は採らない（理由は IADR-0522 決定 6 の 2） |
| 🟡3 経路 B の段数 | IADR-0522 決定 4・セキュリティ仕様書・運用仕様書に「2 段・稼働クラスタで未実測」を明記し、確かめ方を運用仕様書に置いた |
| 🟡4 Argo CD・本番像で Secret が要る | chart は非 optional のまま（fail-closed）。`deploy/bootstrap/` の表とテンプレート・運用仕様書に Secret を載せた（IADR-0522 決定 6 の 3） |
| 🟢5 区切り文字を含むパスワード | 構成を文字列へ戻さず `ConfigurationOptions` のまま 3 か所へ渡す形へ改めた（`a,b=c` の往復が `Keyword 'b' is not supported` で落ちることを実測） |
| 🟢7 切り戻し | IADR-0522 §切り戻し・運用仕様書に置いた |
