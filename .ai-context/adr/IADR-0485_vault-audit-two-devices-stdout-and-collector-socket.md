---
title: IADR-0485 Vault の audit device は標準出力と collector への socket の 2 つを並べ、socket 側を Loki へ取り込む。値は HMAC のまま残し、秘密の書き込みは Loki の条件 1 本で抽出して、経路は BFF のロールで見分ける
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, SC-22, ADR-0124, ADR-0095, ADR-0006, ADR-0042, IADR-0453, IADR-0433, IADR-0457, IADR-0077, IADR-0096, IADR-0216]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 2・決定 4（2 行目）・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 4・フォローアップ 4
related_specs:
  - ../specs/20260928_issue-1683_vault-audit-to-observability.md
---

# IADR-0485: Vault の audit を 2 つの device で出し、socket 側を可観測性基盤へ取り込む（#1683）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-28
- 決定者: claude（#1683。計画 ADR-0124 決定 2 の「audit device の有効化・収集器・監査の抽出」の実装側の形）

## 起点・関連

- 関連する計画書 ID: NFR-18（シークレット管理）、NFR-21（障害検出）、SC-22
- 関連する計画 ADR: **ADR-0124 決定 2**（Vault の audit を可観測性基盤の監査へ取り込み、秘密の書き込みを監査として抽出する。値は記録しない。射程は監査）・
  決定 4（3 点セットの 2 行目）・フォローアップ 3、ADR-0095 決定 4（退避手段を使った事実を残す）、ADR-0006（可観測性基盤）、ADR-0042（監査）
- 関連する実装 ADR: [[IADR-0453]]（フォローアップ 3 が本件）、[[IADR-0433]]（BFF の k8s auth ロール `bff-secret-writer`）、[[IADR-0457]]（Vault の Pod 内ラッパー）、
  [[IADR-0077]]（経路B の可観測性・Vault）、[[IADR-0096]]（k8s auth）、[[IADR-0216]]（アプリのログの出口は OTLP）
- 裁定: planning#700（利用者裁定 2026-09-28。裁定 ②）

## コンテキストと課題

ADR-0124 決定 2 は、Vault の audit device を有効にし、そのログを可観測性基盤へ取り込んで秘密の書き込みを監査として抽出することを定めた。
実測（planning#700）では、audit device は無効で、可観測性基盤のログの受信は OTLP だけだった。

**audit には、出力先が止まると Vault が止まる性質がある。** Vault は、有効な audit device の**少なくとも 1 つ**に書けなければ要求を拒む
（1 つも有効でなければ記録しない）。本件の実測でも、socket の device 1 つだけの状態で collector を止めると、書き込みは `500 internal error` で拒まれた（下の §実測）。
可観測性基盤へ送る device だけを有効にすると、collector や Loki の停止が、画面（SC-22）からの書き込みと ESO の同期の停止になる。

決めることは次の 4 点だった。

1. 出力先と冗長化（何に出すか。止まったときにどうなるか）
2. 取り込みの経路（collector の受け口と Loki への出し方）
3. 値を記録しないことの固定
4. 抽出の条件と、画面経由とコンソール経由の見分け方

## 検討した選択肢

### 論点 1: 出力先と冗長化

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | socket（collector）1 つだけ | ✗ collector・Loki の停止が Vault の停止になる（実測） |
| B | file を PVC 上のファイルへ ＋ socket | △ 止まらない側は持てるが、Vault はファイルを回さない（回すには SIGHUP を送る仕組みが要る）。PVC（1Gi）が埋まれば file 側も書けなくなり A と同じになる。日次バックアップ（IADR-0471）の対象も膨らむ |
| **C** | **file を標準出力へ ＋ socket**（**採用**） | ○ 標準出力は詰まらず、回すのは kubelet。collector が止まっても Vault は答える（実測）。Loki に届かなかった間の行はコンテナログに残る |
| D | collector の sidecar が file を読む（filelog） | ✗ Vault の Pod に collector をもう 1 つ置くことになる（可観測性基盤の構成の変更で、裁定の射程を超える） |
| E | ノードのコンテナログを DaemonSet の collector が読む | ✗ 新しいコンポーネント（DaemonSet・hostPath）が要る。裁定の射程を超える |

### 論点 2: 取り込みの経路

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | **Vault の socket device（tcp）→ 既存の collector に `tcplog` 受信を足す → 既存の `loki` 出口**（**採用**） | ○ 既存の collector（contrib 0.102.0 に `tcplog` が入っている）と既存の出口だけで閉じる。新しいコンポーネントが要らない |
| B | socket を udp にする | ✗ 届かなくても分からない（取りこぼしを許す形になる） |

### 論点 3・4: 抽出と見分け

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | **collector では値で落とさず、全部を Loki の `{job="vault-audit"}` に入れ、抽出は LogQL の条件 1 本で行う**（**採用**） | ○ security.md の既存の約束（収集器のログ経路は値で落とさない。落とすと `failed` が黙って消える）と揃う。条件は文書 1 か所に置ける |
| B | collector の `filter` で書き込みだけを残す | ✗ 条件を間違えると黙って落ちる。読み取りの記録が後から要っても無い |

## 決定

### 決定 1: audit device は 2 つ並べ、標準出力の device を持たない Vault は起動しない

| path | 種別 | 出力先 | 起動時の扱い |
| --- | --- | --- | --- |
| `stdout/` | file（`file_path=stdout`） | コンテナの標準出力（kubelet のコンテナログ） | 無ければ有効にする。**有効にできなければ起動を失敗させる**（audit の無い Vault を動かさない） |
| `otel-collector/` | socket（`socket_type=tcp`・`write_timeout=2s`） | `otel-collector.platform-infra.svc:9514`（collector の `tcplog/vault-audit`） | 無ければ有効にする。**起動を止めずに裏で再試行する**（既定 5 秒 × 60 回）。諦めたら WARN を出す |

- 宣言の場所は経路B の Vault の起動器（`deploy/local/vault-persistence/vault-entrypoint.sh`）である。有効化は Vault の storage に残るので、2 回目以降の起動では「在る」を確かめるだけになる。
- 標準出力の device は **kv の mount より前に**有効にする（秘密の最初の書き込みより前に記録を立てる）。
- 🔴 **標準出力の device を手で外さない。** 外した状態で collector が止まると、Vault は要求をすべて拒み、起動器の API 呼び出し（トークンの確認）すら audit に書けずに通らない（§実測 6）。

### 決定 2: collector は専用のパイプラインで受け、既定は debug、転送構成は Loki へ出す

- 受信 `tcplog/vault-audit`（`0.0.0.0:9514`）と専用パイプライン `logs/vault-audit`（`memory_limiter` → `resource/vault-audit`〔`service.name=vault-audit`〕→
  `attributes/vault-audit`〔`loki.format=raw`〕→ `batch`）を、**経路B の 2 つの collector 設定の両方に置く**。
  既定（`deploy/local/infra/otel-collector.yaml`）の出口は `debug`（外へ出さない）、転送構成（`deploy/local/observability/otel-collector-forward.yaml`）の出口は `loki`。
  - 両方に置くのは、Vault が有効化のときに試験の 1 行を書くためである。既定の構成に受け口が無いと、可観測性を opt-in しない起動で socket の device を有効にできない。
    転送構成は同名の ConfigMap で既定を上書きするので、片方だけに置くと差し替えた瞬間に受け口が消える（#1090 の形）。
- collector の Deployment と Service に `9514/TCP`（`vault-audit`）を足す。
- **compose の collector 設定には置かない**（compose に Vault は居ない。`prometheus/mail-relay` と同じ意図した乖離）。
- **アプリの OTLP のログとは別のパイプラインにする**（混ぜると、アプリの監査〔`Audit=true`〕の抽出に Vault の行が混ざる）。
- Loki では `{job="vault-audit"}` のストリームになり、1 行が Vault の JSON 1 件そのものになる（§実測 2）。

### 決定 3: 値は HMAC のまま残し、平文を出す設定を置かない

- 両方の device に `log_raw=false`・`hmac_accessor=true` を**明示する**（Vault の既定と同じ値。既定に頼らず書いて固定する）。
- 平文を出す設定（device の `log_raw=true`、mount の `audit_non_hmac_request_keys` / `audit_non_hmac_response_keys`）を `deploy/`・`scripts/` に置かない。
  `scripts/scripts.repo.test.js`（#1683）が固定する。
- 残るのは、時刻・主体（`auth.display_name`・`auth.metadata`・`auth.policies`）・パス（項目）・プロパティ名（`request.data.data` のキー）・`error` である。
  値・トークン・accessor は `hmac-sha256:…` に置き換わる（§実測 3）。

### 決定 4: 抽出は Loki の条件 1 本で行い、経路は BFF の k8s auth ロールで見分ける

```logql
{job="vault-audit"} | json
  | type="response"
  | request_operation=~"create|update|patch|delete"
  | request_path=~"secret/(data|metadata|delete|undelete|destroy)/.+"
```

- `auth_metadata_role="bff-secret-writer"`（IADR-0433 の BFF 専用ロール）の行が画面経由、それ以外が画面以外である。
- `error` で絞らない（拒否の行を落とさない）。読み取りは条件で落ちる（Loki には入っている）。
- 条件の正本は `docs/security/security.md`「保管先（Vault）の audit」である。collector の `service.name`・KV の mount（`secret`）・ロール名との一致は `scripts/scripts.repo.test.js` が固定する。

## 理由

- **2 つ並べるのは、出力先の停止を Vault の停止にしないためである**（Vault の「少なくとも 1 つ」の性質。§実測 4・5）。
  止まらない側に標準出力を選ぶのは、詰まらず、回す仕組みが既に在り（kubelet）、PVC を埋めないからである。
- **標準出力の device が無ければ起動しないのは、記録の無い Vault を作らないためである。** socket 側で失敗を止めにすると、collector より Vault が先に上がる順序で起動が落ちる。
- **collector で値で落とさないのは、既存の監査の約束と揃えるためである**（条件の誤りで黙って消える形を作らない）。
- **経路をロールで見分けるのは、BFF の書き込みがそのロールでしか成立しないからである**（IADR-0433。ロールは BFF 専用の ServiceAccount にだけ束縛され、policy は項目ごとの完全一致パス）。

## 実測（2026-09-28・ローカルのプロセス。Vault 1.16.3 / otelcol-contrib 0.102.0 / Loki 3.0.0。クラスタは使っていない）

本リポの起動器（`vault-entrypoint.sh`）・転送構成の collector 設定・Loki の設定を、宛先のアドレスだけ差し替えてローカルのプロセスとしてつないだ。

1. 起動器が 2 つの device を有効にした（`vault audit list -detailed` の options に `log_raw=false`・`hmac_accessor=true`・`socket_type=tcp`・`write_timeout=2s`）。
2. Loki のラベルは `{exporter="OTLP", job="vault-audit", service_name="vault-audit", level="info"}`、1 行は Vault の JSON 1 件そのものだった。
3. root トークンでの作成（コンソール相当）、`role=bff-secret-writer` のメタデータを持つトークンでの部分更新（画面相当）、同じトークンでの許可の無いパスへの書き込みを行い、
   決定 4 の条件で **3 行**が抽出された（経路は画面以外 1・画面 2、拒否の行は `error="permission denied"` つき）。読み取りは抽出されなかった。
   投入した平文 2 つと画面相当のトークンは、Loki の `{job="vault-audit"}` の全行と Vault の標準出力のどちらにも無かった。プロパティ名はキーとして残った。
4. collector を止めても、書き込みは成功した（74〜123 ms）。
5. 対照: 標準出力の device を外して socket だけにし、collector を止めると、書き込みは `500 internal error` で拒まれた。
6. 同じ socket だけの状態で Vault を再起動すると、起動器のトークンの確認が audit に書けずに失敗し、起動器は Vault を止めた（決定 1 の「手で外さない」の根拠）。
7. 2 つの device が在り collector が止まった状態で Vault を再起動すると、unseal・起動器・書き込みが通った。collector を戻すと socket は繋がり直し、
   以後の書き込みは Loki に届いた。**止まっていた間の書き込みは Loki に無く、標準出力にだけ残った。**

## 結果

- 良い影響
  - 画面経由と画面以外の秘密の書き込みを、同じ記録（Vault の audit）で、条件 1 本で辿れる（ADR-0124 決定 2）。
  - collector や Loki が止まっても Vault は止まらない。
  - 値は HMAC のまま残る。
- 悪い影響 / トレードオフ
  - 🔴 **collector が止まっていた間の行は Loki に入らない。** 標準出力（コンテナログ）には残るが、kubelet が回すので、後から Loki へ入れ直す手段は無い。
    起動器は socket の device を有効にできなかったときに WARN を出すが、繋がった後の切断は Vault のログ（`failed to audit`）にしか出ない。
  - 🔴 **共有の root トークンで書いた行は「画面以外」までしか言えない。** 誰が（人）・なぜ画面を使わなかったかは残らない。退避の Runbook の人の記録は引き続き要る。
  - `PERSIST=0`（インメモリの `-dev`）の Vault は audit を持たない（起動器を通らない）。本番の Vault は未配備であり、本決定は経路B の Vault に限る。
  - 起動器の readiness（`vault status`）は unseal で通るので、標準出力の device の有効化の直前に、ごく短い「audit の無い」時間がある（kv の mount も同じ位置にある）。
  - 1 要求につき 2 行（request / response）を 2 か所へ出す。ESO の同期（1 時間ごとの読み取り）も入る。保持は Loki の既定（削除しない。容量は PVC で縛られる）。
- フォローアップ
  1. **稼働クラスタで確かめる**（本件の外。LIVE 未設定）: k8s auth で入った BFF の書き込みの `auth.metadata.role`、collector の Service 越しの socket の疎通、
     Grafana の Explore での抽出、collector の再起動（転送構成への差し替え）の後の再接続。
  2. 監査の保持期間・改ざん防止（#198。NFR「監査ログ保持」）。
  3. 退避の記録先の一本化（ADR-0124 フォローアップ 4）は本件の外（#1682 または別件）。

## 関連

- 作業仕様書: [20260928_issue-1683_vault-audit-to-observability](../specs/20260928_issue-1683_vault-audit-to-observability.md)
- 実装: `deploy/local/vault-persistence/vault-entrypoint.sh`・`deploy/local/infra/otel-collector.yaml`・`deploy/local/observability/otel-collector-forward.yaml`
- 試験: `deploy/local/vault-persistence/vault-entrypoint.test.sh`（T-1683-01〜05）・`scripts/scripts.repo.test.js`（#1683）
- 文書: `docs/security/security.md`「保管先（Vault）の audit」
- Supersedes: なし
- Superseded by: なし
