---
title: IADR-0495 コネクタの資格情報は datasource-service が専用接頭辞 secret/data/datasource/* の read だけを持つ k8s auth ロールで Vault の KV v2 から読む。ESO の msp/* の外に置き、素の HttpClient で話し、Vault が読めなければ平文へ倒さず失敗する
type: impl-adr
status: Accepted
related_ids: [FR-01, UC-04, SC-06, SC-22, NFR-18, ADR-0005, ADR-0095, IADR-0096, IADR-0117, IADR-0295, IADR-0403, IADR-0433, IADR-0453, IADR-0493]
author: claude
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §認証・秘匿情報（Vault で集中管理し、コネクタは実行時に取得する）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-18
related_specs:
  - ../specs/20261003_458_connector-secret-vault-reference.md
---

# IADR-0495: コネクタの資格情報の Vault 読み取り経路（#458 段 S1）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-03
- 決定者: claude（作業仕様書 20261003_458 §設計 S1 の実装。S1 は planning#716 の裁定に依らない段）

## 起点・関連

- 起点 issue: #458（残射程 a「コネクタ資格情報の Vault 化」）
- 関連する計画書 ID: FR-01・UC-04（事前条件「接続情報（認証情報はVault管理）が用意されている」）・NFR-18
- 関連する計画 ADR: ADR-0005（#458 の起点）・ADR-0095（投入の面は SC-22。本 IADR は読む側だけで、投入の面に触れない）
- 関連する実装 ADR: [[IADR-0493]]（解決器のポートと fail-closed。本 IADR はその Vault 実装）、[[IADR-0403]] 決定 8（移行段の設計）、
  [[IADR-0096]]（ESO の `eso-read` policy）、[[IADR-0433]]・[[IADR-0453]]（BFF の Vault 書き込みと素の HttpClient の前例）、
  [[IADR-0117]]（ユニット外参照の規則）、[[IADR-0295]]（ログの規律）
- 採番: 本 IADR を起こした時点の develop の最大は 0493。0494 は未マージの別ブランチ（#1728）が取る見込みのため 0495 とした
  （本リポは欠番を許さない。#1728 が先に入らない場合はマージ時に改番する）。
- 基点コミット: `origin/develop` `84c80853`

## コンテキストと課題

[[IADR-0493]] は、コネクタが資格情報を得る経路を `IConnectorSecretResolver` の 1 本に寄せ、`vault:<path>#<key>` の参照が
解決できないときは外部へ要求を出さずに同期を失敗させる形を入れた。ただし配線した解決器は移送期間用の素通しだけで、
参照は常に `ResolverUnavailable` で止まる。**参照を実際に Vault から読む経路**（段 S1）が要る。

決めることは 4 つある。

1. 参照のパスを Vault のどこに置き、datasource-service にどの権限を与えるか。[[IADR-0403]] 決定 8 の
   `msp/datasource/<id>` は ESO の policy（`secret/data/msp/*` の `read`。[[IADR-0096]]）に入る —— ESO が読めると、
   ExternalSecret を 1 枚書くだけでコネクタの資格情報が k8s Secret へ材料化され得る（作業仕様書 母集合 V1）。
2. Vault と何で話すか（VaultSharp か、素の HttpClient か）。
3. 構成と配線（Vault を配備しない構成で何が起きるか）。
4. Vault の応答・失敗を、[[IADR-0493]] の失敗の列挙（`ConnectorSecretFailure`）へどう写すか。

## 検討した選択肢

### パスと権限

| | A. `msp/datasource/*` に置き、ESO の policy から除く | B. `msp/` の外の専用接頭辞 `datasource/*` に置く（**採用**） |
| --- | --- | --- |
| ESO が読めないこと | 🔴 Vault の ACL は deny を書かない限り広い `msp/*` が勝つ。`deny` を足すと ESO の policy に例外が入り、字面で読みにくい | 字面で成り立つ（`secret/data/msp/*` は `secret/data/datasource/…` に一致しない） |
| datasource が `msp/*` を読めないこと | 接頭辞が `msp/datasource/*` なら成り立つ | 成り立つ |
| 既存の policy の変更 | 要る（ESO） | 要らない（新しい policy を 1 本足すだけ） |

### Vault との通信

| | A. VaultSharp | B. 素の HttpClient（**採用**） |
| --- | --- | --- |
| 使う API | k8s auth のログインと KV v2 の GET の 2 本だけ | 同じ 2 本 |
| 依存 | 新しいパッケージ（CPM・ライブラリの baseline・脆弱性監視の母集合が増える） | 無し（BCL の `HttpClient` と `System.Text.Json`） |
| 前例 | 無し | BFF の `VaultKvClient`（[[IADR-0453]]）が同じ形で稼働している |
| 失敗の写像の制御 | ライブラリの例外型から逆算する。例外文が URL（＝参照のパス）を運ぶ | 状態コードを直接見る。例外文をログへ出さない形を自分で守れる |

[[IADR-0403]] 決定 8 が挙げた懸念（「VaultSharp が全サービスへ入る」）は 1 サービスに限られるが、2 本の API のために依存を足す理由が無い。

## 決定

### 決定 1: 参照のパスは KV マウントからの相対で専用接頭辞 `datasource/` に限り、datasource-service には `secret/data/datasource/*` の `read` だけを与える

- policy `datasource-connector-read`（`deploy/local/vault/eso/policy-datasource-connector-read.hcl`）は `path "secret/data/datasource/*" { capabilities = ["read"] }` の 1 本だけ。
  書き込み・一覧・削除・metadata を与えない（書き手は段 S2 以降。planning#716）。
- k8s auth ロール `datasource-connector-reader` を datasource-service 専用の ServiceAccount `microservices-platform/datasource-service`
  にだけ束縛する（`default` に束縛しない。`eso` role へ足さない）。SA は helm の `services.datasource.serviceAccount` が作る。
- 🔴 **ESO の `eso-read` policy は変えない。** `secret/data/msp/*` と `secret/data/ai-stock-trading/*` はこの接頭辞に一致しないので、
  ESO はコネクタの資格情報を読めず、datasource-service は基盤の資格情報を読めない。
- 解決器も接頭辞を守る: 参照のパスが `datasource/` で始まらない（大文字小文字を区別する —— Vault のパスは区別する）、
  または空・`.`・`..` のセグメントを持つなら、**Vault へ送らずに** `MalformedReference` で止める。policy が拒むはずの要求をそもそも出さない
  （`..` は URL の正規化で接頭辞の外へ出得る）。
- 字面の一致（policy の path 集合・ESO / BFF の policy との非交差・role の束縛先・helm の SA 名と role 名）は
  `DataSourceService.Tests` の `ConnectorSecretVaultPolicyTests` が固定する（BFF の `SecretItemVaultPolicyTests` と同型）。

### 決定 2: Vault とは素の HttpClient で話す（VaultSharp を入れない）

- `Infrastructure/Secrets/VaultConnectorSecretResolver` が `POST v1/auth/<mount>/login`（role と SA トークン）と
  `GET v1/<kv mount>/data/<path>` の 2 本だけを送る。トークンはリース満了の 30 秒前まで使い回し、403 のときだけ 1 度取り直す（BFF の `VaultKvClient` と同じ）。
- BFF の `VaultKvClient` と `IServiceAccountTokenReader` は再利用しない —— ユニット外参照は Shared の 3 プロジェクトに限られ
  （[[IADR-0117]]）、BFF は引けない。トークンの読み口（数行）はサービス内に写した。Shared へ寄せるのは 3 つ目の消費者が現れたときにする。

### 決定 3: `Vault:Address` が在るときだけ Vault 解決器を配線する。無ければ段 S0 と同じ素通しだけ

- 構成キーは BFF と同じ `Vault__Address` / `Vault__Role` / `Vault__AuthMount`（helm の deployment テンプレートが `services.<name>.vault` から描く）。
  既定の role 名は `datasource-connector-reader`。helm の values でも明示する（テンプレートの既定は BFF の role 名であるため）。
- `services.datasource.vault.address` の既定は空（＝Vault を配備しない構成）。このとき配線は段 S0 と同じ素通しで、参照は
  `ResolverUnavailable` のまま止まる。compose は Vault を持たないので変えない。
- Vault 解決器は**合成**である: `vault:` で始まらない値は移送期間の素通し（`PlaintextPassthroughConnectorSecretResolver`）へ委ね、
  参照だけを Vault から読む。🔴 **参照を Vault で解決できなかったときに平文の素通しへ回さない。**
- NetworkPolicy の「BFF → Vault」を「`vault.address` を持つサービス → Vault」へ一般化した（名前は `allow-<name>-egress-to-vault`。
  BFF の分は従前と同じ名前・同じ内容で描かれる）。address が空のサービスには穴を開けない。
- 構成値に秘密は無い（名乗りは SA トークン）。`check-secret-injected-options.js` の母集合（「Secret から注入する」と宣言した構成値）には入らない。

### 決定 4: Vault の応答は [[IADR-0493]] の失敗の列挙へ写し、列挙を増やさない

| Vault の応答 | 結果 |
| --- | --- |
| 200 で `data.data[<key>]` が空白でない文字列 | 成功（その値） |
| 200 で値が空白 | `Empty` |
| 404（パス無し・現在版の削除・破棄）／200 でも `metadata.deletion_time` か `destroyed`／`data.data` が null／キー無し／値が文字列でない | `NotFound` |
| ログイン不能（SA トークンを読めない・ログインが 2xx でない）／403（取り直し後も）／5xx 等／不達・時間切れ／応答の解釈不能 | `Unreachable` |
| 接頭辞の外・不正なセグメント・形の誤り | `MalformedReference`（Vault へ送らない） |

- 呼び出し側の ct による取り消しだけは外へ出す（[[IADR-0493]] 決定 3）。
- 🔴 **ログは HTTP の状態コードと例外の型名だけ。** 値・参照のパス・キー名・例外オブジェクト・例外文を出さない
  （例外文は要求の URL＝参照のパスを運び得る）。どの項目の解決が失敗したかは同期サービスが番号で記録する（[[IADR-0493]] 決定 3）。

## 理由

- 権限の統制を policy の字面で読めるようにした（ESO・BFF・datasource の 3 つの policy が交わらないことを、Vault の照合規則のまま試験で測る）。
- 依存を足さず、稼働中の前例（BFF）と同じ形にしたので、ログインのリース・403 の取り直しの挙動を 2 箇所で揃えて読める。
- 既定（address 空）は段 S0 から挙動が変わらない。Vault を配備した構成だけが参照を解決できるようになる。

## 結果

- 良い影響: `vault:datasource/<…>#<key>` を保存した行は、Vault を配備した構成で同期できるようになった。Vault が読めないときも平文へ倒れない。
  datasource-service は Vault を引く最初のアプリになるが、権限は専用接頭辞の `read` だけである。
- 悪い影響・トレードオフ:
  - Vault に参照先の値を入れる面がまだ無い（段 S2・S3 は planning#716 の裁定待ち）。いまはコンソールで `secret/datasource/<…>` へ書くしかない。
  - SA トークンの読み口が BFF とサービス内で 2 つになった（決定 2）。
  - 1 回の同期の開始時に、宣言されたキーの数だけ Vault を引く（wiki / saas / db は 1 回）。トークンは使い回すのでログインは 1 時間に 1 回程度。
- フォローアップ:
  1. 段 S2・S3（planning#716 の裁定後）: 参照先へ書く面。書き手の policy は本 IADR の接頭辞の内側に限る。
  2. 段 S4: 移送・期間フラグ・readiness 検査。素通しの撤去。
  3. 段 S5: live 文書（runbook のローテーション手順。版で戻す）。

## 関連

- Supersedes: なし（[[IADR-0403]] 決定 8 の `msp/datasource/<id>` のパスは本 IADR 決定 1 が改める。IADR-0403 には日付つき追記で指す）
- Superseded by: なし
