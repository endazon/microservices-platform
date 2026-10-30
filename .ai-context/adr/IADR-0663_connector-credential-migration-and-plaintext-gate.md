---
title: IADR-0663 データソースの平文の資格情報は、稼働の Pod の中の一回きりの移送で保管先へ移し、移送期間の旗を下ろした後は平文を同期・保存・起動のいずれでも拒む（#458 段 S4・S5）
type: impl-adr
status: Accepted
related_ids: [NFR-18, FR-01, SC-06, SC-22, ADR-0126, ADR-0095, IADR-0493, IADR-0495, IADR-0501]
author: claude
created: 2026-10-31
updated: 2026-10-31
related_specs:
  - ../specs/20261031_458_connector-credential-migration.md
  - ../specs/20261003_458_connector-secret-vault-reference.md
---

# IADR-0663: データソースの資格情報の移送と移送期間の旗（#458 段 S4・S5）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-31
- 決定者: claude（#458。MSP の棚卸し 2026-10-31・コーディネーター承認の (A)）

## 起点・関連

- 計画 ADR-0126 のフォローアップ 2（既存の行の平文を Vault へ移し、参照へ置き換える。移送は画面化しない。移送の後は、設定への平文の書き込みを拒む）。
- 計画 ADR-0126 のフォローアップ 4（無効化したデータソースの値を消すか残すか。計画・実装）。
- 前提:
  - IADR-0493（解決器の器・移送期間の素通し）。
  - IADR-0495（Vault の読み取り経路・専用接頭辞 `datasource/`）。
  - IADR-0501（SC-22 の群・BFF の書き込み・正規の参照の配置）。
- 作業仕様書 20261003_458 は §窓 1 の表（参照の行・平文の行・期間中・期間後の 3 列）を、段 S4 で `[Fact]` で実測すると定めていた。
- コーディネーターの条件（2026-10-31）:
  - 移送が稼働の平文を消すのか（不可逆か）と、失敗したときの戻し方を本 IADR に書く。
  - PoC 配備の節には、移送を 1 回流す手順と、流す前のバックアップを書く。

## コンテキストと課題

- 既存のデータソースは、資格情報を `DataSources.Config` に平文で持つ（`apiToken`・`password` など）。読み取り・書き込みの経路（S0〜S3）はできたが、平文の行は「画面以外」のままで、画面から書いても使われない。
- 移送には、保管先への**書き込み**の権限が要る。
  - datasource-service の常用の名乗り（`datasource-connector-reader`）は read だけである。BFF の群の policy は、data の read を持たない（同じパスの別のキーを残して書き足せない）。
  - ADR-0126 は「移送は画面化しない（BFF 経由ではない）」としている。
- 平文を読めるのは datasource-service の DB だけである。DB の外（運用者の端末）へ平文を運ぶ手順は、平文の露出を増やす。
- 経路 B（PoC）の datasource-service は保管先の所在を持っていない（`values-local.yaml` に無い）。移した参照を解決できない。

## 決定

### 決定 1: 「平文」の判定は 1 か所

`Domain/PlaintextCredential.IsPlaintext`。次のすべてを満たす値を平文とする。

- キーが秘密のキー（`SecretMask.IsSecretKey`）である。
- 値が空白でない。
- 値が参照（`vault:`）を名乗らない。
- 値がマスク（`***`）でない。

移送・起動の門・書き込みの拒否が同じ判定を使う。判定を 2 か所に持つと、移送を何度流しても門が開かない形が生まれる。

### 決定 2: 移送期間の旗（`ConnectorCredentials:PlaintextAllowed`。既定 true）

helm の `services.datasource.connectorCredentials.plaintextAllowed` が env へ描画する。テンプレートは `default` を使わない（`false | default true` は true になる）。

- **true（移送期間）**: 従前どおり。平文で同期し、SC-06 の API も平文を保存できる。
- **false（移送の後）**: 次の 3 つで平文を拒む。
  1. **同期**: 解決器の外側に飾り（`PlaintextGateConnectorSecretResolver`）を巻く。平文の値は、内側へ渡さずに `PlaintextRejected` で失敗させる（符号 `plaintext-rejected`。外部へ要求を出さない）。参照は内側（Vault の解決器か素通し）のまま。失敗の理由の列挙に 1 つ足した（自由文は足さない。IADR-0493 決定 3 の規律のまま）。
  2. **書き込み**: 登録・更新・部分更新の API が、秘密のキーへの平文を 400（`plaintext-credential-rejected`。キー名だけを返す）で拒む。参照・空白・マスクの書き戻し・`Config` を送らない部分更新は通す。
  3. **起動**: 平文の行が 1 件でも残っていれば、ホストの開始の前に起動を拒む（`PlaintextCredentialStartupGate`。同期のワーカーより先に登録する）。数えるのは状態に依らず全行で、ログは件数だけ。
- **旗は時刻で切らない。** 平文の行が 0 件でなければ旗を偽にできない（起動しない）。作業仕様書の §窓 1 の「両端」の形である。3 列は `ConnectorSecretResolutionTests.Sync_PlaintextWindow_MatchesTheBothEndsTable` が実コネクタで測る。

### 決定 3: 移送は、稼働の datasource-service の Pod の中で、移送専用の短命のトークンで流す

- 入口: `dotnet DataSourceService.dll migrate-connector-credentials`。
  - `--dry-run` は数えるだけで、Vault にも DB にも触れない。
  - `--token-stdin` は、標準入力の 1 行目を Vault のトークンとして読む。
  - ホストは開始しない（同期のワーカー・HTTP は動かない）。
  - 接続文字列と Vault の所在は Pod の環境から読む。
- 名乗り: 運用者が、移送専用の policy `datasource-credential-migrate` の**短命のトークン**を発行する。
  - policy は専用接頭辞の 1 セグメント `+` に create・update・read、metadata に read だけである。`*`・list・delete は無い。
  - トークンは既定 10 分・使用回数の上限つきで、終わったら accessor で失効させる。
  - `scripts/migrate-datasource-credentials.sh` が発行・受け渡し・失効をまとめる。`--live`／`LIVE=1` が無ければ何もしない。
  - 🔴 トークンは引数・環境変数・ファイルに置かない（パイプで標準入力へ渡す）。policy は role に束縛しない —— 常用の名乗りに書き込みを足さない。
- 1 行 1 キーずつ、次の順で行う。
  1. Vault の `datasource/<ID>` を読む。現在版が削除されていれば、metadata から版を読む。
  2. Vault にそのキーがあるときと無いときで扱いが違う。
     - **無い**: 既存のキーに平文を足し、読んだ版で check-and-set で書く（同じパスの別のキーを消さない・並行する書き込みに勝たない）。
     - **同じ値がある**: 書かない（途中で落ちた後の再開）。
     - **違う値がある**: 「衝突」として行を触らない。SC-22 から書いた値を平文で上書きしない。どちらが正しいかは人が決める。
  3. 読み直して、同じ値であることを確かめる（値は比べるだけで出さない）。
  4. 設定のそのキーを、**読んだ平文と今の値が同じときだけ**、正規の参照（`vault:datasource/<ID>#<キー>`）へ置き換える。
     - 1 文の条件つき更新である（`CredentialMigrationStore`。`jsonb_set` をそのキーにだけ当てる）。
     - 移送が読んだ後に SC-06 の更新が入った行は触らない。
- 🔴 **Vault が先、設定が後。** Vault に書けなかった行・衝突した行は、設定を触らない（平文のまま。旗が true の間は同期も止まらない）。
- 冪等である。2 回目は平文が無いので何もしない。
- 対象は状態に依らず全行（無効化した行の平文も保管の対象）。
- ログ・出力は件数と行の通し番号だけ。ID・キー名・値・パス・トークンを出さない。
- 終了コード: 0 = 全部移した・1 = 衝突／失敗が残った・2 = 使い方・構成の誤り。

### 決定 4: 不可逆性と戻し方

🔴 **移送は、稼働の DB の上では不可逆である。** 設定の平文を参照へ置き換えるので、DB には平文が残らない。値は保管先（版つき）に残り、移送の前のバックアップにも残る。

戻し方は 3 つある。

| 事象 | 戻し方 |
| --- | --- |
| 移送の後に同期が失敗する（保管先の不達・所在・role の誤り） | 旗を true のまま、原因を直す。参照の行は平文へは倒れない（fail-closed）。値は保管先にあるので、直せば次の同期から効く |
| 設定を移送の前へ戻す必要がある | **移送の前に取った datasource の DB の論理バックアップ**から、`DataSources` の当該行の `Config` だけを戻し、旗を true に戻す。保管先の値は残るので、後で画面から書けば再び参照へ置ける |
| 保管先の値を誤った（直前の版へ戻す） | `vault kv rollback -version=<直前の版> secret/datasource/<ID>` |

- だから **バックアップは移送の前提**であり、省けない。手順は runbook（`secret-rotation-runbook.md`「データソースの資格情報の移送」）と PR の PoC 配備の節に書いた。
- 🔴 バックアップのファイルには平文が入る。置き場所と破棄の期限を runbook の記録の節に残す。

### 決定 5: 経路 B（PoC）では datasource-service が保管先を読む

- `deploy/local/values-local.yaml` に `services.datasource.vault.address` を置く。
- role・policy は `deploy/local/vault/eso/bootstrap.sh`（ESO=1）が入れる。
  - `datasource-connector-reader`（read だけ）は既存である。
  - 本 IADR で、移送専用の policy（role なし）を足した。
- 保管先が居ない経路 B の構成（VAULT=0）では、参照の行だけが `unreachable` で fail-closed になる。平文の行は旗のとおりに動く。
- **本番で保管先を読む配備の値**（ADR-0126 フォローアップ 3）は本 IADR の射程外である。#1789（本番の Vault の配備）で扱う。

### 決定 6: 無効化したデータソースの値は保管先に残す（ADR-0126 フォローアップ 4 の実装側の案）

- BFF にも datasource-service にも、削除・破棄の権限を与えない（IADR-0501 のまま）。移送の権限も削除を持たない。
- 消すのは運用者の棚卸しである。保管先の管理の権限で `vault kv metadata delete` を行い、手順は runbook に置いた。
- 理由:
  - 無効化は論理削除で、取り消せる（取り消した行の参照は値が無ければ解決できない）。
  - 削除の権限をサービスに持たせると、誤った ID で別のデータソースの値を消せる。
- 🔴 計画・実装の分担なので、計画への環流で確かめる（起票は本 PR の後）。

## 却下した案

- **移送を BFF 経由（SC-22 の群）で行う**: ADR-0126 フォローアップ 2 の「画面化しない」に反する。BFF の群の policy は data の read を持たず、同じパスの別のキーを残して書き足せない。
- **移送用の k8s Job（helm）**: deployment のテンプレートの env（DB の TLS・CA・接続文字列の補間）を Job へ複写することになる。複写はいずれずれる。稼働の Pod の中なら同じ環境で流れる。
- **datasource-service の常用の role に書き込みを足す**: 常時の名乗りが専用接頭辞へ書けるようになる。移送は一回きりで、短命のトークンで足りる。
- **運用者の端末で DB から平文を読み出して Vault へ書く**: 平文が DB の外（端末・シェルの履歴・パイプ）を通る。
- **旗を時刻（移送期間の終了日）で切る**: 移送の漏れが「同期が止まった」に化ける（作業仕様書 §窓 1）。

## 結果

- 移送を流して旗を下ろした環境では、稼働の DB の平文の資格情報は 0 件になり、平文の保存・同期・起動の 3 つの口が閉じる。
- 残余:
  - 旗が true（既定）の間は従前どおりである。旗を下ろすのは運用の判断で、配備の値の変更が要る。
  - 移送の前のバックアップには平文が残る（保持期間が満ちるまで）。
  - 本番は保管先を読まない（#1789）。
  - 稼働の Vault との結合（policy の `+`・check-and-set・トークンの失効）は、経路 B で流すまで未実測である（PoC の配備で測る）。
  - 設定の置き換えの SQL は、実 PostgreSQL の試験（`CredentialMigrationStoreTests`）が固定する。統合テストは PR では起動しない。
