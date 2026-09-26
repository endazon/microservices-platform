---
title: "Grafana ルールの検査と realm の検査に残る細部を直す（#1605。#1600・#1602 の監査の残り）"
type: spec
status: done
related_ids: [NFR-21, NFR-09, ADR-0006, ADR-0032, IADR-0165, IADR-0294, IADR-0420]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-21（障害検出 5 分以内）／NFR-09（エッジでの認証）
related_specs: [20260926_issue-1595_grafana-check6-yaml-and-emptiness.md, 20260926_issue-1596_realm-login-grants-and-username-source.md]
issue: "#1605"
---

# 作業仕様書 — Grafana ルールの検査と realm の検査に残る細部を直す（#1605）

## 起点

- issue: #1605（#1600〔#1595〕と #1602〔#1596〕の監査で出た、ブロックしない指摘）。
- 起点 ID: **NFR-21**（Grafana 暫定アラートの検査 6）／**NFR-09**（realm の検査 5・検査 7。BFF の Bearer 受理の前提）。計画 ADR: ADR-0006・ADR-0032（改めない）。
- 実装 IADR: IADR-0165（Grafana 暫定アラート。検査 6）／IADR-0294（MFA の強制。検査 5 の「直接付与は MFA を迂回する」）／IADR-0420（検査 7 が守る `MachinePrincipal.IsMachine` の前提）。
- **新しい IADR は起こさない。** 検査器の読み方を配備の版（Grafana 11.0.0・Prometheus v2.52.0・Keycloak 24.0）の実装に合わせるだけで、新たな設計判断は無い。
  IADR-0165 と IADR-0294 へ日付つき追記を 1 つずつ足す（検査 7 の細部は #1589・#1596 と同じく IADR へは書かない）。
- 稼働クラスタには当たらない（LIVE 未設定。静的検査の修正だけ）。

## 配備の版の実装で確かめたこと（読み取りのみ。`gh api repos/<owner>/<repo>/contents/<path>?ref=<tag>`）

| 項目 | ソース（版） | 読んだこと |
| --- | --- | --- |
| provisioning の YAML の読み手 | grafana/grafana `pkg/services/provisioning/alerting/config_reader.go`・`go.mod`（v11.0.0） | `gopkg.in/yaml.v3` の `yaml.Unmarshal`（v3.0.1） |
| plain の数の読み方 | go-yaml/yaml `resolve.go`（v3.0.1） | 先頭の文字で種類を決め（数字 → D・符号 → S・`.` → 浮動小数・`yYnNtTfFoO~` → 表引き・その他 → 文字列）、D / S は `_` をすべて除いてから `strconv.ParseInt(plain, 0, 64)`（**0 始まりは 8 進**・`0x` / `0o` / `0b` と符号）→ `ParseUint` → `^[-+]?(\.[0-9]+\|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$` なら `ParseFloat`（`08` は 8 進に失敗して 8.0）。範囲外の `ParseFloat` は誤りになり文字列のまま |
| threshold の型 | grafana/grafana `pkg/expr/threshold.go`（v11.0.0） | `supportedThresholdFuncs` は `gt` / `lt` / `within_range` / `outside_range` の 4 つだけ。他は `NewThresholdCommand` が誤りを返す（`gte` / `lte` / `eq` / `ne` / `*_included` は 11.0.0 に無い） |
| `absent` のラベル | prometheus/prometheus `promql/functions.go` の `createLabelsForAbsentFunction`（v2.52.0） | 引数が選択子（範囲つきを含む）のときだけ照合子を**順に**読み、「等号で、その名前でまだ立てていない」なら立て、それ以外はその名前を消す（`{job="a", job!="b"}` は job を持たない、`{job!="b", job="a"}` は job="a"）。サブクエリ・括弧・その他の式はラベル無し |
| `directGrantsOnly` | keycloak/keycloak `server-spi-private/…/models/utils/RepresentationToModel.java` の `createClient`・`services/…/protocol/oidc/OIDCLoginProtocolFactory.java` の `setupClientDefaults`・`services/…/services/managers/ClientManager.java`（24.0.5。24.0.0 と差分なしを確かめた） | **import**（realm の作成・起動時 import）: `directGrantsOnly` があれば `standardFlow = !dgo`・`directAccess = dgo` を入れ、**その後で明示の `standardFlowEnabled` / `directAccessGrantsEnabled` が上書き**する。**管理 REST のクライアント作成**（`ClientManager.createClient`。reconcile の `POST /clients`）: 同じ後に `setupClientDefaults` が走り、`directGrantsOnly` があれば**明示の値を上書き**し、無ければ未設定の `standardFlowEnabled` と **`directAccessGrantsEnabled` を true にする**。更新（`updateClient`）は `directGrantsOnly` を読まない |
| 任意スコープ | keycloak/keycloak `services/…/protocol/oidc/TokenManager.java` の `getRequestedClientScopes`（24.0.5） | 既定スコープとクライアント自身は常に、任意スコープは `scope` で要求したものだけ |
| `user.attribute` の綴り | keycloak/keycloak `services/…/protocol/ProtocolMapperUtils.java` の `getUserModelValue`・`…/oidc/mappers/UserPropertyMapper.java`（24.0.5。24.0.0 と差分なし） | `"get" + 先頭の 1 文字を大文字 + 残り` のメソッドを `UserModel` から引く。`Username` も `username` も `getUsername`。残りの大小は区別する（`USERNAME` は `getUSERNAME` で無い） |
| メールアドレスを利用者名にする | keycloak/keycloak `services/…/userprofile/DeclarativeUserProfileProviderFactory.java`（24.0.5） | `registrationEmailAsUsername` のとき利用者名は編集できず、メールアドレスを利用者が入れられるのは登録・IdP の確認・`editUsernameAllowed`・機能 `UPDATE_EMAIL` のときだけ（`editEmailCondition`）。単独では利用者は名乗れない |

## 設計

### check-grafana-alerting.js（検査 6）

1. **plain の数を yaml.v3 と同じに読む**（`resolveYamlPlain`）: 上の表の手順をそのまま写す（0 始まりの 8 進・`_` の除去・符号つきの `0x` / `0o` / `0b`・`08` → 8・範囲外は文字列）。`.` で始まる値の `_` は読まない（文字列 → params では「解釈できない」の違反。fail-closed）。
2. **`absent` のラベルを Prometheus と同じに作る**: 上の表の手順を写す。サブクエリ（`[…:…]`）はラベル無し。照合子を読めない選択子はラベルを「不明」（照合の矛盾を判定しない ＝ 見逃し側）。
3. **空白の細部**: 二重引用の行末のエスケープした空白（`\ ` / `\<TAB>`）を残す／ファイル末の改行を空行として数えない（`|+` / `>+` の改行が 1 つ多かった）／ブロックスカラーの中の、内容の字下げより長い空白だけの行は、字下げを超えた空白を内容として残す（YAML 1.2 の `l-nb-literal-text`。字下げ以下の行は空行）。
4. **評価器の型を Grafana 11.0.0 の 4 つに絞る**（issue では「範囲外・記録のみ」。実データは `gt` / `lt` だけで、変更は受理する型を狭めるだけ ＝ 安く安全なので直す）。また、4 つの型で params が足りないときの文言を「型を本検査へ足すこと」から「params の数が足りない」へ改める: 他の型は「Grafana 11.0.0 の threshold が受け付けない型」の違反。

### check-realm-constraints.js（検査 5・検査 7 と SA の検査）

5. **軽量アクセストークンの検査は常に載るマッパーだけで満たす**: クライアント単位の `protocolMappers` と既定スコープの中身。任意スコープは要求しない限り載らないので数えない。
   出どころの検査（2''）は従来どおり任意スコープも数える（要求すれば載り、上書きし得る）。
6. **`directGrantsOnly` と管理 REST の既定を数える**: ログインの口を `loginFlowFlags(client)` にまとめ、**import と管理 REST の作成のどちらかで開くなら開く**（保守側）。
   - 標準フロー: `directGrantsOnly === true` なら明示の `standardFlowEnabled === true` のときだけ（import は明示が勝ち、REST は false）。`directGrantsOnly === false` なら**常に開く**（REST が明示の false を上書きして true）。無ければ `standardFlowEnabled !== false`。
   - 直接アクセス（ROPC）: `directGrantsOnly === true` なら開く（REST は明示の false を上書き）。`directGrantsOnly === false` なら明示の true のときだけ。未設定なら **`directAccessGrantsEnabled !== false`**（REST の作成は未設定を true にする）。
     検査 5 は、未設定を数えるのを `bearerOnly` でないクライアントに限る（`bearerOnly` はトークンを得られない。明示の true と `directGrantsOnly: true` は従来どおり `bearerOnly` でも数える）。
   - ［2026-09-27 追記 / #1605］**PR の監査で後退が見つかり直した**: 初版は「未設定」を `directAccessGrantsEnabled === undefined` だけで判定しており、`null` や `"true"` を閉として黙って通していた
     （#1605 以前の SA の天井の検査は `!== false` で数えていた）。Keycloak は表現を Jackson で読み、JSON の `null` は未設定の Boolean（管理 REST の作成で true）、`"true"` も true へ読み替え得る。
     いまは **リテラルの false だけを閉**と読む（`null` / 文字列 / 数は開く。`bearerOnly` の `null` は未設定と同じく数えない）。`directGrantsOnly` は `null` を無い、`false` / `"false"` を false、他（`"true"` を含む）を true。
     `directGrantsOnly` が true の下の `standardFlowEnabled` は、未設定・`null`・リテラルの false 以外を開くと読む。自己試験 3 件（検査 5・SA の天井・検査 7）と、実データの `reset-gate` を `null` / `"true"` にした CLI 試験を足した。
   - 使う箇所: 検査 7 の `humanLoginGrants`、検査 5 (2)（直接付与は MFA を迂回する）、SA の realm 書き込みの検査（対話ログインの口を閉じていること）。
     これまでの「直接アクセスは既定 false なので明示の true だけを数える」は import にしか当たらない（reconcile はクライアントを `POST /clients` で作る）。
7. **`user.attribute` は `getUserModelValue` と同じに照らす**: 先頭の 1 文字の大小を問わず `sername` が続くもの（`username` / `Username`）を利用者名のプロパティと認める。
8. **`registrationEmailAsUsername` の文言**: 単独では名乗れず、登録・IdP の確認・`editUsernameAllowed`・`UPDATE_EMAIL` と組むとメールアドレスがそのまま利用者名になる、と書く。
   検出は続ける（`UPDATE_EMAIL` は realm の宣言の外で有効になり得るので、宣言だけからは閉じていると言えない）。
9. **クライアントポリシーの実行器の報告を 1 件にまとめる**: 同じ経路（`realm.clientProfiles[use-lightweight-access-token]`）でログイン用クライアントの数だけ出ていたのを、該当するクライアントを列挙した 1 件にする。

## 母集合（誤りになる記述の走査。パス除外: `src/ai-stock-trading` / `CHANGELOG.md`。拡張子で絞らない）

誤りの側の語で本ブランチの分岐元（`origin/develop` = f195df1f。本作業の記録を書く前）に対して引いた:
`git grep -n -E "明示の true だけ|既定 false|core schema|within_range_included|outside_range_included|\bgte\b|lightweight\.claim|registrationEmailAsUsername|user\.attribute=username|directGrantsOnly|use-lightweight-access-token|createLabelsForAbsentFunction" f195df1f -- ':!src/ai-stock-trading' ':!CHANGELOG.md'`（118 行。出力を切らずに読んだ）。
軸 2 として `directGrantsOnly|directAccessGrantsEnabled|optionalClientScopes|任意スコープ|lightweight|user\.attribute|registrationEmailAsUsername|use-lightweight-access-token`（検査器・`scripts.repo.test.js`・realm JSON・reconcile を除く）、
軸 3 として `check-grafana-alerting|evaluator|within_range|gte|core schema|yaml\.v3|8 進|absent\(`（`docs` / `scripts/README.md` / `deploy/grafana` / k8s の grafana.yaml）、
軸 4 として自己試験の件数（`(自己試験|self-test)… (54|156|157|60|167) 件`）を引いた。

| 拾ったもの | 扱い |
| --- | --- |
| `scripts/check-grafana-alerting.js`（`gte` / `*_included` の評価器・「core schema の型」の注記 2 箇所・自己試験の `gte` / `within_range_included`） | 本作業で直す（評価器を 4 つに絞り、注記を yaml.v3 に改め、区間の自己試験は絞り込みの区間どうしで書き直す） |
| `scripts/check-realm-constraints.js`（「直接アクセスは既定 false なので明示の true だけを数える」・軽量の検査・`registrationEmailAsUsername` の文言・`user.attribute` の比較） | 本作業で直す |
| `docs/screens/SC-17_user-account-management.md` §運用上の注意 2・3（軽量のマッパーの置き場・メールアドレスを利用者名にする設定を「どれも名乗れる」と一括りにした文） | 直す（既定スコープに置くこと・`directGrantsOnly` と未設定の直接アクセス・単独では名乗れないこと）。trace ブロックへ #1605 と本仕様書 |
| `docs/tests/SC-17_user-account-management.md`（T-54） | T-54 は正しいまま。T-55 を足し、trace ブロックへ #1605 と本仕様書 |
| `docs/tests/SC-14_otp-mfa.md` T-12（「全 client で `directAccessGrantsEnabled` が true でない」） | 検査 5 の射程が広がったので改める。trace ブロックへ #1605 と本仕様書 |
| `docs/operations/operations.md`（検査 6 の射程の列挙） | 数の読み方と評価器の型を足す。trace ブロックへ #1605 と本仕様書 |
| `scripts/README.md`（`static-checks` の行） | #1605 を足す |
| `scripts/scripts.repo.test.js`（#1596 の CLI 試験の前提 `user.attribute === 'username'`） | 前提の確認であり正しいまま。#1605 の CLI 試験・実データの変異試験・自己試験の名指しを足す |
| `scripts/check-realm-copy-drift.js`（`CLIENT_FLAG_DEFAULTS.directAccessGrantsEnabled: true`） | 突合の正規化の既定で、管理 REST の作成の既定（true）と整合する。変えない |
| IADR-0075 / 0252 / 0294 本文 / 0329 / 0404 / 0434、`seed-abac-policies.js`、`reset-gate.test.js`、`docs/how-to/session-handoff.md` の `directAccessGrantsEnabled` | 当時の実測・明示の false を求める記述で、誤りにならない。IADR-0294 は日付つき追記を足す（本文は書き換えない） |
| `.ai-context/specs/` の確定済み記録（#1589・#1595・#1596 など） | 凍結記録。書き換えない（#1589 の「直接アクセスは検査 5 が既に禁じている」も当時の記録として残す） |
| 「既定 false」の一般語（helm の opt-in・DTO の既定など 80 行余り） | 本件と無関係 |
| 自己試験の件数（#1595 の 54 件・#1596 の 156 件） | 確定済み記録の中だけ。件数を書く live な文書は無い（`scripts/README.md` にも無い） |
| `deploy/grafana/.../slo-alerts.yaml`・k8s の `grafana.yaml` の「機械で確かめたのは…の範囲」 | 範囲の下限の列挙で誤りにならない。両写しの同内容の検査があるので、コメントだけの改稿は #1588・#1595 と同じく見送る |

## 受け入れ基準

- [x] `params: [010]` を 8、`[0o10]` / `[0x1F]` / `[+0x1F]` / `[1_000]` / `[08]` を yaml.v3 と同じ値に読む（`> 9` × `lt 010` を「永久に発火しない」と検出する）
- [x] `absent(up{job="a", job!="b"}) and vector(1)` とサブクエリの `absent` を「値を返さない」と誤判定しない。`absent(up{job="a"}) and on(job) m{job="b"}` は従来どおり検出する
- [x] 二重引用の行末のエスケープした空白・`|+` のファイル末・ブロックスカラーの空白だけの行を YAML のとおりに読む
- [x] `gte` / `lte` / `eq` / `ne` / `within_range_included` / `outside_range_included` を違反にする
- [x] 任意スコープにしか `lightweight.claim=true` の利用者名マッパーが無い軽量のクライアントを検出する
- [x] `directGrantsOnly` を数える（検査 5・検査 7・SA の検査）。`directAccessGrantsEnabled` 未設定も数える
- [x] `user.attribute=Username` を利用者名のプロパティと認める（`USERNAME` は認めない）
- [x] クライアントポリシーの実行器の報告が 1 件になる
- [x] 実データ（Grafana の provisioning の写し 2 つ・realm）が通る

## 検証

- `node scripts/check-grafana-alerting.js --self-test` → 54 → 60 件通過。`node scripts/check-grafana-alerting.js` → OK（Prometheus 20 / Grafana 20・組み合わせ 20 件・許可リスト 0 件）
- `node scripts/check-realm-constraints.js --self-test` → 157 → 167 件 OK（PR の監査の後退を直して 170 件）。`node scripts/check-realm-constraints.js` → OK（exit 0。CI の `static-checks` と同じ 2 行）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 835 件 pass（#1605 の realm の CLI 試験・Grafana の実データの変異試験・自己試験の名指しを含む）
- **テストを先に書いた**: 追加した自己試験は修正前の検査器ですべて落ちた（realm は 10 件が FAIL。Grafana は数・absent・エスケープ・`|+`・空白だけの行・評価器の型の順に、1 つ直すごとに次が落ちた）
- **変異試験（検査器のソース）**: 修正を 1 つずつ戻し `--self-test` を走らせ、`git show HEAD:<path> > <path>` で戻した。**15 通りすべて exit 1**
  （G1 0 始まりを 10 進／G2 absent の後続の照合子で消さない／G2b サブクエリにラベル／G3 エスケープした空白を剥がす／G4 ファイル末の改行を空行に／G4b 改行で終わらない入力にも改行／G5 空白だけの行を空行に／G6 `gte` を受け付ける／
  R1 任意スコープで軽量を満たす／R2 `directGrantsOnly: true` を数えない／R2b 未設定の直接アクセスを数えない／R2c `directGrantsOnly: false` の標準フローを数えない／R3 `user.attribute` の先頭の大小を区別／R4 旧文言／R5 実行器をクライアントごとに報告）
- **旧検査器との対照**（`origin/develop` の検査器に実データの変異を当てた）: `lte` の評価器・`> 9` × `lt 010` は旧で違反 0 件 → 新で写しの両方に違反。`absent(up{job="otel-collector", job!="x"}) and vector(1)` × `gt 0` は旧で「決して値を返さない」の誤検出 2 件 → 新で 0 件。
  realm の CLI 試験は旧検査器で「任意スコープだけの軽量マッパーの変異を検出しなかった」で落ちる。
- 実ファイルでの新しい指摘: **無い**（realm は全クライアントが `standardFlowEnabled` / `directAccessGrantsEnabled` を明示し、`directGrantsOnly` を持たず、軽量も使わない。Grafana の params は 0 始まりの数を持たず、評価器は `gt` / `lt` だけ）。
  検査器の前提の誤り（Keycloak の管理 REST の作成は未設定の `directAccessGrantsEnabled` を true にする）は本作業で直した。

## 未検証・射程外

- Grafana の文字列型の欄（`title` / `condition` / `refId` / `datasourceUid`）は、yaml.v3 が plain を文字列の欄へ読むとき**元の綴り**を入れる（`refId: 010` は "010"）。
  検査器は木の値を文字列にする（"8"）。検査器の中では両側が同じ読み方なので鎖の照合はずれないが、`condition: 010` と `refId: "8"` を組むと検査器は繋ぎ、Grafana は繋がない。実データは英字の refId だけ。記録に留める。
- 稼働 Grafana / Keycloak では確かめていない（LIVE なし）。
