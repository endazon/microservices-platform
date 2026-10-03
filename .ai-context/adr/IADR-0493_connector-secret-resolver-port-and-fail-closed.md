---
title: IADR-0493 コネクタの資格情報は同期の開始時に IConnectorSecretResolver で 1 回だけ解決し、解決できなければ外部へ要求を出さずに失敗する。IADR-0403 決定 8 段 1 の「データソース側で参照へ変換する」は投入の面（SC-22）に合わせて改める
type: impl-adr
status: Accepted
related_ids: [FR-01, UC-04, SC-06, SC-22, NFR-18, ADR-0005, ADR-0095, ADR-0104, ADR-0110, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0295, IADR-0403, IADR-0433, IADR-0456]
author: claude
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §認証・秘匿情報（コネクタは実行時に取得する）
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 実測 6・決定 1・3
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-06（2026-09-11 追記）
related_specs:
  - ../specs/20261003_458_connector-secret-vault-reference.md
---

# IADR-0493: コネクタの資格情報の解決器と fail-closed（#458 段 S0）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-03
- 決定者: claude（作業仕様書 20261003_458 §設計 S0 の実装。投入の面の動的項目は planning#716 で裁定待ち）

## 起点・関連

- 起点 issue: #458（残射程 a「コネクタ資格情報の Vault 化」。#447 から委譲）
- 関連する計画書 ID: FR-01・UC-04（事前条件「接続情報（認証情報はVault管理）が用意されている」）・SC-06・SC-22・NFR-18
- 関連する計画 ADR: ADR-0095 決定 1（秘密情報の投入の面は SC-22）・決定 3（書き込みの射程を項目で限る）、ADR-0104・ADR-0110（SC-22 の項目の型）、ADR-0005（#458 の起点）
- 関連する実装 ADR: [[IADR-0403]] 決定 8（本 IADR が段 1 を改める）、[[IADR-0295]]（露出経路の封鎖。本 IADR は読む経路を寄せる）、
  [[IADR-0051]]・[[IADR-0053]]・[[IADR-0054]]・[[IADR-0055]]（コネクタ）、[[IADR-0433]]・[[IADR-0456]]（SC-22 の書き込み policy と ExternalSecret 必須）
- 計画への環流: planning#716（2026-10-03 起票。データソースごとに増える項目が SC-22 の型に収まらない —— 書き込み射程・供給元・ロール）
- 基点コミット: `origin/develop` `233f432d`

## コンテキストと課題

[[IADR-0403]] 決定 8 は、コネクタ資格情報の Vault 化を 4 段で設計した（2026-09-06）。その段 1 は
「**書き込み時に**値そのものではなく Vault の参照（`vault:msp/datasource/<id>#<key>`）を `jsonb` へ入れる」であり、
データソースの API が秘密を受け取り、データソース側で Vault へ書いて参照へ変換する形を前提にしている。

その後、計画が投入の面を決めた（2026-09-11）。ADR-0095 実測 6・決定 1 と SC-06 の追記は、
**データソースの資格情報を名指しして SC-22 へ寄せ**、SC-06 では投入しないと定めた。段 1 をそのまま実装すると
「既存画面へ投入欄を足す」（ADR-0095 が不採用にした選択肢）をデータソースについてだけ採ることになる。
一方、SC-22 の型（完全一致パスの静的な書き込み policy・ExternalSecret 必須・運用者ロール）にデータソースごとに増える項目が
どう収まるかは計画に無い。作業仕様書 §判定がこれを確かめ、planning#716 として起票した。

**ただし、どの案で裁定が降りても変わらない部分がある**（作業仕様書 §設計の注記）。計画
`06_technical/09_datasource-connectors.md`（fixed）は「コネクタは**実行時に取得する**」と定めており、

- コネクタが資格情報を得る経路を 1 本にすること（現状は 3 コネクタが `Config` の平文を直接読む）
- 参照が解決できないときに平文へも「認証なし」へも倒さないこと（fail-closed）
- 1 回の同期の中で版を混ぜないこと

は、投入の面が SC-22 であれ SC-06 であれ要る。本 IADR はこの 3 点（段 S0）だけを決め、実装する。

## 検討した選択肢

### 解決の単位（作業仕様書 §窓 2）

| | A. コネクタが Discover / Fetch のたびに解決器を呼ぶ | B. 同期サービスが開始時に 1 回解決し、コネクタへ渡す（**採用**） |
| --- | --- | --- |
| 1 回の同期で版が混ざるか | 🔴 混ざる（探索と取得の間に書き込みが入ると、前半と後半で別の版） | 混ざらない |
| 書き込み前に始まった同期 | 🔴 途中から新しい版になり、失敗し得る | 古い版で完走する |
| 解決器の呼び出し回数 | 1 + 件数 | 宣言したキーの数（wiki / saas / db は 1） |
| コネクタの依存 | 解決器を DI で受ける | 受けない（ポートの引数で受け取るだけ） |

### コネクタへの渡し方

| | A. 解決済みの値で `Config` を差し替えた `DataSource` の写しを渡す | B. ポートの引数に `ConnectorCredentials` を足す（**採用**） |
| --- | --- | --- |
| コネクタが `Config` の秘密を読まないことの強制 | 🔴 されない（読んでいる先が差し替わるだけ） | 型で分かれる（`Config` から読めば参照の文字列が送られ、試験が落ちる） |
| エンティティへの影響 | 🔴 EF 管理下のエンティティを写す | 無し |
| 変更量 | 小 | 中（ポートの 2 メソッド・既存試験のスタブ 10 クラス・コネクタ単体試験の呼び出し 44 箇所） |

## 決定

### 決定 1: 読む経路を `IConnectorSecretResolver` の 1 本に寄せ、開始時に 1 回だけ解決する

- ポート `Domain/Ports/IConnectorSecretResolver.ResolveAsync(configuredValue, ct)` を置く。入力は `Config` に保存された値そのもの。
- コネクタは**資格情報の `Config` キー名を宣言する**（`IDataSourceConnector.CredentialKeys`。wiki / saas = `apiToken`、db = `password`、filesystem = なし）。
  宣言するキーは `SecretMask.IsSecretKey` に当たる名前に限る（応答のマスクと同じ集合。試験が固定する）。
- `DataSourceSyncService` が、コネクタを引いた直後・探索の前に、宣言されたキーだけを 1 回ずつ解決し、
  `ConnectorCredentials` として Discover と**すべての** Fetch へ同じインスタンスを渡す。
- 🔴 **コネクタは `source.Config` から資格情報を読まない。** `Config` に値が無い（または空白の）キーは解決せず、従前どおり認証なしで接続する。

### 決定 2: 参照の形は `vault:<path>#<key>`、段 S0 の実装は Fake と移送期間用の素通しだけ

- 形は `vault:<path>#<key>`（`#` はちょうど 1 つ、パスとキーは空でなく空白を含まない）。**接頭辞の判定は大文字小文字を区別しない** ——
  区別すると `VAULT:…` が平文として外部へ送られる。
- 🔴 **パスの置き場所は本 IADR では決めない。** [[IADR-0403]] 決定 8 の `msp/datasource/<id>` は ESO の `secret/data/msp/*` の read policy に
  入ってしまう（作業仕様書 母集合 V1）。接頭辞は段 S1 が Vault の role・policy と同じ PR で決める。
- 本番に配線するのは `PlaintextPassthroughConnectorSecretResolver`（Vault に繋がない）。平文はそのまま返し、
  `vault:` で始まる値は**解決器が無い**として失敗させる。試験は `FakeConnectorSecretResolver`（呼び出しの記録・版の変更・例外）を使う。
- 平文の素通しは**移送期間の前の端**である（作業仕様書 §窓 1）。期間フラグと readiness 検査（平文の行 0 件）は段 S4 が入れる。
  **段 S4 までは平文の行がそのまま同期できる**（現状から後退しない）。

### 決定 3: 解決できなければ、外部へ 1 件も要求を出さずに失敗する（fail-closed）

- 失敗の理由は列挙 `ConnectorSecretFailure`（`MalformedReference` / `ResolverUnavailable` / `NotFound` / `Empty` / `Unreachable`）で返し、自由文を運ばない。
  解決した値が空白なら `Empty` として失敗させる（空を「認証なし」として送らない）。解決器が例外を投げたら `Unreachable` に畳む
  （呼び出し側の ct による取り消しだけは外へ出す。#1604 と同じ）。
- 同期は探索の前で止まり、`SyncResult.CredentialsResolved = false`（`DiscoverSucceeded` も false、watermark は進めない）。
  直近エラー（`LastSyncError`。SC-06 が読む）と応答の `message` は固定の文 `credentials not resolved for credential #<番号> (<理由の符号>)` であり（番号はコネクタが宣言する資格情報の項目の 1 始まりの順。キー名は `password` 等の機微な語なので出さない —— CodeQL `cs/cleartext-storage` の指摘で初版から改めた）、
  連続失敗として数える（継続失敗アラートの対象）。
- 🔴 **ログ・`SyncError`・応答に出すのは資格情報の項目の番号と理由の符号だけである（キー名も出さない。上の項）。** 値・参照の文字列（パスを含む）・解決器の例外文は出さない。
  解決器の例外は**型名だけ**を記録し、例外オブジェクトもメッセージも渡さない（[[IADR-0295]] 決定 4 より一段強い。解決器の例外は値や Vault のパスを運び得る）。
  `ConnectorSecretResolution` / `ConnectorCredentials` / `ConnectorSecretReference` の `ToString` は値とパスを伏せる。

### 決定 4: [[IADR-0403]] 決定 8 の移行段を次のとおり改める

| 段（IADR-0403 決定 8） | 改めた後 |
| --- | --- |
| 1. 書き込み時に（データソース側で）参照を `jsonb` へ入れる | 🔴 **改める。** 投入の面は計画が決めた SC-22 である（ADR-0095 決定 1・SC-06 の 2026-09-11 追記）。データソースの API が秘密を受け取って Vault へ書く形は採らない。SC-22 で動的な項目をどう扱うか（書き込み射程・供給元・ロール）は **planning#716 の裁定待ち**であり、作業仕様書の段 S2・S3 はその後に着手する |
| 2. 読み出しはコネクタ実行時にのみ解決（`IConnectorSecretResolver`） | **本 IADR 決定 1 で実装した**（段 S0）。Vault 解決器は段 S1 |
| 3. 既存行の移送と fail-closed | fail-closed は**本 IADR 決定 3 で実装した**。移送と期間フラグは段 S4 |
| 4. ローテーションは Vault の版管理に委ねる | 変わらない（段 S5 で runbook へ）。開始時に 1 回解決するので、版の切り替わりは同期の単位で効く |

## 理由

- 段 S0 は計画の確定事項（09_datasource-connectors「実行時に取得する」）だけに依り、planning#716 のどの案（A / B / C）でも作り直さない。
- 型（ポートの引数）で分けるので、コネクタが `Config` を読み戻すと**参照の文字列がそのまま送られて試験が落ちる**。規約やコメントで守るより強い。
- fail-closed の失敗状態を固定の文にしたので、SC-06 の直近エラーの表示を変えずに「資格情報未設定」を区別できる（新しい応答の項目を足さない）。

## 結果

- 良い影響: 3 コネクタが資格情報を得る経路が 1 本になった（作業仕様書 §受け入れ基準 2 の前半）。`vault:` 参照を保存した行は、
  Vault 解決器が配備されるまで**外へ何も送らずに**止まる（参照を Bearer トークンやパスワードとして相手へ渡さない）。
- 悪い影響・トレードオフ: `IDataSourceConnector` の 2 メソッドの引数が 1 つ増えた（新しいコネクタは `CredentialKeys` を宣言し、`credentials` から読む）。
  移送期間用の解決器は平文を素通しするので、**平文保存そのものは残る**（[[IADR-0295]] の応答のマスクが引き続き外への経路を塞ぐ）。
- フォローアップ:
  1. 段 S1: Vault 解決器と `datasource-service` の読み取り専用 role（専用接頭辞・ESO の `msp/*` の外）。
  2. 段 S2・S3: planning#716 の裁定の後。
  3. 段 S4: 移送・期間フラグ・readiness 検査（作業仕様書 §窓 1 の 3 列を `[Fact]` で実測）。
  4. 段 S5: live 文書（`docs/security/security.md` の「暫定」節・runbook）の是正。

## 関連

- Supersedes: なし（[[IADR-0403]] 決定 8 の段 1 を**部分的に**改める。IADR-0403 全体は Superseded にしない。同 IADR に日付つき追記で指す）
- Superseded by: なし
