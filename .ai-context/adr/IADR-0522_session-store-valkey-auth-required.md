---
title: IADR-0522 キャッシュ・セッションストアを Redis 7.4 から Valkey 9.1 へ差し替え、認証を必須（空なら起動しない）にして到達を BFF に絞る。パスワードは Secret から BFF の構成値 1 つで注入し、ヘルスチェックとセッションが同じ構成文字列を使う
type: impl-adr
status: Accepted
related_ids: [ADR-0131, ADR-0107, ADR-0112, ADR-0032, ADR-0030, NFR-18, IADR-0251, IADR-0316, IADR-0510, IADR-0514, IADR-0461, IADR-0066, IADR-0453]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0131_cache-session-store-valkey.md 決定 1〜5（決定 3 が版を、決定 4 が受入条件 1〜5 の記録を本 IADR に求める）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 基準 A〜C
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1（基準 D）
related_specs:
  - ../specs/20261009_1839_session-store-valkey.md
---

# IADR-0522: キャッシュ・セッションストアを Valkey へ差し替え、認証と到達の制限を必須にする（#1839）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（版・名前・認証と到達の制限の手段・構成値の形）。製品の選定は計画 ADR-0131 決定 1（利用者裁定）

## 起点・関連

- 起点 issue: **#1839**（planning#750 の裁定で新設された計画 ADR-0131 の実装）。
- 計画: **ADR-0131**（決定 1 製品を Valkey とする・決定 2「Redis」は Redis 互換と読む・決定 3 版は実装の IADR が選ぶ・
  決定 4 受入条件 1〜5・決定 5 3 点セット）。基準は ADR-0107（A〜C）と ADR-0112 決定 1（D）。
- 前提（覆さない）: [IADR-0251](./IADR-0251_bff-session-token-handler.md) 決定 4（セッションの実体）・決定 5（鍵リング）、
  [IADR-0510](./IADR-0510_bff-keyring-storage-resolves-di-connection.md)（鍵リングの保存先を DI の接続から引く）、
  [IADR-0453](./IADR-0453_sc22-screen-decisions-operator-granularity-updater-and-states.md) 決定 3（SC-22 の書き込み記録）。
- 引き継ぐ判断: [IADR-0316](./IADR-0316_bff-session-deploy-config.md) の「`RedisConnectionString` はコード既定を使い、配備で上書きしない」。
- 先例: [IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md)（製品の差し替え。決定 3 の名前の規則・決定 10 の fail-closed の起動スクリプト）、
  [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（digest 固定と点検）。
- 基点コミット: MSP `origin/develop` `f4632f7b`。採番: develop の最大は IADR-0520、PR #1855 が IADR-0521 を持つ。

## コンテキストと課題

初回のインフラ製品の点検（IADR-0514・#1787）で、配備の `redis:7-alpine`（実体 7.4.11）が基準 A を満たさない（RSALv2 / SSPLv1）ことが分かり、
計画は製品を Valkey へ差し替えると裁定した（ADR-0131）。同じ点検で基準 D の穴も見つかっていた —— Redis は既定で認証が無く、
`CONFIG`・`FLUSHALL` などの管理コマンドが通るのに、**配備は認証なしで、compose はホストへ 6379 を公開していた**。
計画は差し替えと同じ作業でこの穴を塞ぐよう求めている（ADR-0131 決定 4 の 2。配備の定義を作り直すのが同じ作業だから）。

依存するのは BFF だけで、用途は 3 つ（セッションの実体・DataProtection の鍵リング・SC-22 の書き込み記録）とヘルスチェックである。
クライアントは StackExchange.Redis で、Valkey は RESP とコマンドの互換を持つ。**計画はこの互換を上流の記載で確かめただけで、
実機の確認を実装に委ねている**（ADR-0131 着手可否の注記 2。通らなければ利用者の裁定へ戻す）。

着手時の実測で、設計に効く事実が 3 つあった:

1. **既存の BFF の試験は実イメージに届かない。** `RedisTicketStoreTests` は `MemoryDistributedCache`、`BffKeyRingSharingTests` はプロセス内のリスト、
   SC-22 の記録の試験も偽物の器を使う。受入条件 1 の「既存の試験で確かめる」をそのまま当てても、Valkey は 1 度も動かない。
2. **接続先の置き場が 2 つあった。** セッションと鍵リングは `BffSession:RedisConnectionString`（コード既定 `redis:6379`）、
   ヘルスチェックは `Redis:ConnectionString`（appsettings と compose の env）を読んでいた。認証を足すと 2 か所にパスワードが要る。
3. **認証なしの `valkey-cli ping` は `NOAUTH` を返しても終了コード 0 である**（実測）。従前の healthcheck・readiness（`redis-cli ping` の終了コード）の
   形のままだと、認証が壊れても「準備完了」になる。

## 検討した選択肢

### 論点 1: 版（決定 3）

1. **9.1**（**採用**） — 保守の期限 2031-05-19（計画の実測 4）。新規の採用で長い方を取る。
2. 8.1 — 期限 2030-03-31。8 系を選ぶ理由（既存の運用・既知の互換）が本リポジトリに無い。

### 論点 2: パスワードを BFF へどう渡すか

1. **パスワードを別の構成値 `BffSession:RedisPassword` にし、接続先はコード既定のまま**（**採用**） — Secret 由来の値が 1 つの env に閉じ、
   `check-secret-injected-options.js`（「Secret から注入する」と XML doc で宣言した構成値が、helm は secretKeyRef・compose は変数展開で注入されているか）が
   そのまま守る。接続先は IADR-0316 の判断（既定が配備の実態に一致するなら上書きしない）を保てる。
2. 接続文字列ごと Secret から入れる（`valkey:6379,password=$(…)` を k8s の変数展開で組む） — 接続先の 2 つ目の置き場を chart に作る（IADR-0316 が避けたもの）。
   検査器は「接続文字列の一部が Secret 由来」を見られない。
3. コード既定を撤去して未注入なら起動失敗にする（RabbitMQ の #1022 の形） — 接続先は秘密ではなく、既定で正しい。パスワードの注入漏れは
   検査器（CI）と非 optional な secretKeyRef（起動しない）と、サーバ側の認証必須（通らない）の 3 つで既に止まる。

### 論点 3: サーバへパスワードをどう渡すか

1. **起動スクリプトで空を拒み、here-document で標準入力の設定（`valkey-server -`）へ渡す**（**採用**） — 引数に載らない。
   イメージの entrypoint（root なら `valkey` 利用者へ落とす）を経由できる。
2. `--requirepass "$P"` — `ps`・`/proc/<pid>/cmdline` に載る（#1793 が撲滅した形）。
3. Secret を設定ファイルとしてマウントする — compose で同じ形を作るには別の仕組み（compose の secrets）が要り、経路で起動形が分かれる。
   root 所有 0600 のファイルは `valkey` 利用者へ落ちた後に読めない。

### 論点 4: 経路 B のパスワードの既定

1. **明示指定 ＞ 既存の Secret の値 ＞ 初回だけ乱数**（**採用**） — 公知の dev 既定値を経路 B へ持ち込まない。Valkey は起動時にしか設定を読まないので、
   up のたびに作り直すと走っている Valkey と BFF が食い違う。既存値を使い回せば冪等。
2. `${SESSION_STORE_PASSWORD:-<dev 既定>}`（他の dev の secret と同じ形） — 基準 D の穴を「誰でも知っているパスワード」で塞ぐことになる。

compose は他の dev 既定と同じ形（`${SESSION_STORE_PASSWORD:-session-store-dev-password-change-me}`）にした。compose は乱数を作って
2 つのサービスへ配る仕組みを持たず、ホストへ公開しないことと組み合わせた dev 限定の扱いとする。

## 決定

### 決定 1: 製品と版 —— `valkey/valkey:9.1-alpine@sha256:48332870…`（9.1.2）

- 参照は 3 か所（`deploy/docker-compose.yml`・`deploy/local/infra/valkey.yaml`・`Platform.Bff.Tests` の `ValkeyTestContainer.Image`）。
  digest は multi-arch の image index のもの。**一致は `ValkeyContainerDefinitionTests` が PR で突き合わせる。**
- 本番像の chart にストアの配備は足さない（計画の実測 8・決定 5）。

### 決定 2: 名前 —— サーバは製品名、資格情報は役割の名前（IADR-0461 決定 3 と同じ規則）

| 対象 | 旧 | 新 |
| --- | --- | --- |
| compose のサービス・Deployment・Service・MSP ns の ExternalName | `redis` | `valkey` |
| compose の volume | `redis-data` | `valkey-data`（旧 volume は読まない。決定 7） |
| 接続先（コード既定 `BffSessionOptions.RedisConnectionString`） | `redis:6379` | `valkey:6379` |
| 資格情報（Secret・キー） | 無し | `session-store-credentials`・`password`（platform-infra と MSP ns の 2 か所に同じ値） |
| 資格情報（環境変数） | 無し | `SESSION_STORE_PASSWORD`（サーバと起動器）・`BffSession__RedisPassword`（BFF） |

型名・ライブラリ名（`RedisTicketStore`・`RedisXmlRepository`・`RedisConnectionString`・StackExchange.Redis 系のパッケージ）は改めない（ADR-0131 決定 2）。

### 決定 3: 認証を必須にする（基準 D。fail-closed）

- 起動スクリプト（compose・経路 B・統合試験で同じ文字列）:
  ```sh
  [ -n "$SESSION_STORE_PASSWORD" ] || { echo 'valkey: SESSION_STORE_PASSWORD が空。認証なしでは起動しない' >&2; exit 1; }
  exec docker-entrypoint.sh valkey-server - <<EOF
  requirepass "$SESSION_STORE_PASSWORD"
  EOF
  ```
  経路 B は `command` ではなく `args` で与え、イメージの entrypoint（`tini` → `docker-entrypoint.sh`）を残す。compose は `$` を `$$` と書く。
- パスワードは設定の引用符の中へ入るため、起動器は `"`・`\`・空白を含む値を拒む（乱数は 16 進のみ）。
- readiness（経路 B）と healthcheck（compose）は `VALKEYCLI_AUTH` を環境変数で与えた `valkey-cli ping` の応答が `PONG` であることで判定する（実測 3）。
- 認証を通った相手には管理コマンドも通る。ACL でコマンドを絞ることはしない（相手は BFF だけで、StackExchange.Redis が接続時に使うコマンドの
  集合を固定して保守する費用に見合わない）。

### 決定 4: 到達を制限する（基準 D）

- **compose**: `ports` を外し、ホストへ公開しない。BFF は compose のネットワークで届く。
- **経路 B**: NetworkPolicy `valkey-ingress-bff-only`（platform-infra）。ingress は MSP ns（`kubernetes.io/metadata.name: microservices-platform`）の
  `app: bff-service` の Pod から TCP 6379 だけ。egress は 0 件（全拒否。readiness は exec なので通信を要らない）。
- 🔴 **NetworkPolicy を強制しないクラスタでは、経路 B の防御は認証の 1 段である**（ADR-0131 決定 4 の 2 が記録を求める段数）。強制するクラスタでは 2 段。

### 決定 5: BFF の構成値 —— パスワードを 1 つ足し、ヘルスチェックの置き場をセッションへ寄せる

- `BffSessionOptions.RedisPassword`（既定は空。XML doc で「k8s Secret から環境変数で注入する」と宣言）。helm は
  `services.bff.session.storeExistingSecret` / `storePasswordKey` の非 optional な secretKeyRef、compose は変数展開で注入する。
- `BffSessionOptions.SessionStoreConfiguration()` が接続先とパスワードを StackExchange.Redis の構成文字列にまとめ、
  **セッション（`AddStackExchangeRedisCache`）・鍵リング（`ConnectionMultiplexer.Connect`）・ヘルスチェック（`AddRedis`）が同じ 1 つを使う**。
- ヘルスチェック専用の `Redis:ConnectionString`（appsettings 2 つ・compose の env・試験の器）は廃した（実測 2）。

### 決定 6: 経路 B の起動器 —— Secret を 2 か所へ置き、旧 Redis を消す

- `scripts/k8s-local-up.sh` の [3/7] でパスワードを決め（論点 4）、platform-infra へ置く。[5/7] で MSP ns へ同じ値を置く。
  **ESO=1 でも手で置く**（[4/7] の rollout が消費する bootstrap であり、Vault の KV を持たない。postgres / rabbitmq の基盤 secret と同じ扱い）。
- kustomize の apply は宣言から消えたリソースを刈らないので、起動器が旧 `deploy/redis`・`svc/redis`（platform-infra）と ExternalName `svc/redis`（MSP ns）を
  `--ignore-not-found` で消す。消さないと既存クラスタに**認証なしのストアが残り続ける**。
- `scripts/check-bff-multi-replica-session.js` の鍵リングの件数の読みは `deploy/valkey` で、パスワードを Pod の中の env から読ませる（ホストの引数に載せない）。

### 決定 7: 既存データは移行しない（受入条件 5）

- compose は新しい volume `valkey-data` を使い、旧 `redis-data` は読まない。経路 B は従前から揮発（volume を持たない）。
- 中身は dev のセッション・鍵リング・SC-22 の書き込み記録だけである。失うと全員が再ログインになり、SC-22 の最終更新者が「記録なし」に戻る。
  Redis 7.4 の RDB（版 12）を Valkey が読める保証も無く、読ませる手間に見合う中身が無い。

## 受入条件の結果（ADR-0131 決定 4）

| # | 条件 | 結果 | 確かめ方 |
| --- | --- | --- | --- |
| 1 | BFF の 3 用途が実イメージで通る | **通った** | `BffSessionStoreValkeyTests`（`Category=Integration`。本番の配線 `AddBffSession` のまま、配備と同じ image・起動形の Valkey 9.1.2 へ向ける）。セッションの保存・取得・更新・単独の失効・**全セッションの即時失効**（`RemoveAllForSubjectAsync`。他の利用者は残る）、2 つのレプリカの間での**鍵リングの共有**（A が保護した値を B が復号。共有のキーに鍵がちょうど 1 件）、SC-22 の**書き込み記録**の別レプリカからの読み出し、ヘルスチェックの認証つきの疎通、の 5 件が手元の Docker で緑（2026-10-09）。変異（構成文字列からパスワードを落とす）で陰性対照以外の 4 件が赤になることを確かめた |
| 2 | 基準 D の穴を塞ぐ | **塞いだ** | 認証なしの接続が `RedisException` で拒まれる（同試験の陰性対照）。パスワードが空なら起動しない・引数に載らない（`ValkeyContainerDefinitionTests` が起動スクリプトを sh で走らせる）。compose は `ports` を持たない・経路 B は NetworkPolicy（同試験が配備の定義を読む）。compose の実起動で `healthy`・認証なしの `ping` が `NOAUTH`・`docker port` が空であることも確かめた。**経路 B は NetworkPolicy を強制しないクラスタでは 1 段**（決定 4） |
| 3 | digest で固定 | **固定した** | 匿名のトークンで index の digest を 2 回解決して一致（`sha256:48332870…`・`application/vnd.oci.image.index.v1+json`）。`check-image-digests.js` が 3 参照を固定と判定 |
| 4 | 既定の外部通信の有無 | **無い** | 配備と同じイメージを起こし、コンテナの中で `/proc/net/{tcp,tcp6,udp,udp6}` を 0.2 秒ごとに 130 秒読んだ。loopback 以外の相手を持つ接続は 0 件。**計画の 08_data-egress-policy への環流は要らない**。経路 B は egress を閉じた（決定 4） |
| 5 | 既存データの扱い | 移行しない | 決定 7 |

## 結果

- 良い影響: 確定スタックから非 OSS の製品が 1 つ消えた。認証なしの管理コマンドの口が塞がった。接続の置き場が 1 つになり、
  パスワードの注入漏れは CI（検査器）・起動（非 optional な secretKeyRef）・サーバ（認証必須）の 3 か所で止まる。受入条件 1 の試験が
  回収実行（integration.yml）で継続して回る。
- 悪い影響 / トレードオフ: 既存の dev 環境は差し替えの時に全員が再ログインになる。compose の既定パスワードは公知の dev 用の値である。
  経路 B のパスワードを変えるには Valkey と BFF の作り直しが要る（起動器は既存値を使い回すので、明示指定したときだけ起きる）。
- 残余リスク:
  1. NetworkPolicy を強制しないクラスタでは経路 B の防御は認証の 1 段（決定 4）。
  2. 認証を通った相手には管理コマンドも通る（決定 3）。
  3. 本番像の chart にストアは無い。本番像へ足すときは、同じ統制（認証必須・到達の制限）を先に入れる（計画 ADR-0131 決定 5）。
- フォローアップ:
  1. **計画へ**: 受入条件 1 が通ったので、ADR-0131 の着手可否の注記の充足と決定 5 の「現在の実現手段」の更新を計画へ環流する（ADR-0131 フォローアップ 2）。
  2. 本番像へセッションストアを足すときは、パスワードの供給を ESO（Vault → ExternalSecret）へ載せるかを決める（本件では経路 B の bootstrap だけ）。
