---
title: "Grafana ルールの検査と realm の検査に残る細部を直す（#1605。#1600・#1602 の監査の残り）"
type: spec
status: draft
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
4. **評価器の型を Grafana 11.0.0 の 4 つに絞る**（issue では「範囲外・記録のみ」。実データは `gt` だけで、変更は受理する型を狭めるだけ ＝ 安く安全なので直す）: 他の型は「Grafana 11.0.0 の threshold が受け付けない型」の違反。

### check-realm-constraints.js（検査 5・検査 7 と SA の検査）

5. **軽量アクセストークンの検査は常に載るマッパーだけで満たす**: クライアント単位の `protocolMappers` と既定スコープの中身。任意スコープは要求しない限り載らないので数えない。
   出どころの検査（2''）は従来どおり任意スコープも数える（要求すれば載り、上書きし得る）。
6. **`directGrantsOnly` と管理 REST の既定を数える**: ログインの口を `loginFlowFlags(client)` にまとめ、**import と管理 REST の作成のどちらかで開くなら開く**（保守側）。
   - 標準フロー: `directGrantsOnly === true` なら明示の `standardFlowEnabled === true` のときだけ、それ以外は `standardFlowEnabled !== false`。
   - 直接アクセス（ROPC）: `directGrantsOnly === true` なら開く（REST は明示の false を上書き）。`directGrantsOnly === false` なら明示の true のときだけ。未設定なら **`directAccessGrantsEnabled !== false`**（REST の作成は未設定を true にする）。
   - 使う箇所: 検査 7 の `humanLoginGrants`、検査 5 (2)（直接付与は MFA を迂回する）、SA の realm 書き込みの検査（対話ログインの口を閉じていること）。
     これまでの「直接アクセスは既定 false なので明示の true だけを数える」は import にしか当たらない（reconcile はクライアントを `POST /clients` で作る）。
7. **`user.attribute` は `getUserModelValue` と同じに照らす**: 先頭の 1 文字の大小を問わず `sername` が続くもの（`username` / `Username`）を利用者名のプロパティと認める。
8. **`registrationEmailAsUsername` の文言**: 単独では名乗れず、登録・IdP の確認・`editUsernameAllowed`・`UPDATE_EMAIL` と組むとメールアドレスがそのまま利用者名になる、と書く。
   検出は続ける（`UPDATE_EMAIL` は realm の宣言の外で有効になり得るので、宣言だけからは閉じていると言えない）。
9. **クライアントポリシーの実行器の報告を 1 件にまとめる**: 同じ経路（`realm.clientProfiles[use-lightweight-access-token]`）でログイン用クライアントの数だけ出ていたのを、該当するクライアントを列挙した 1 件にする。

## 母集合（誤りになる記述の走査）

TBD（実装後に引く）

## 受け入れ基準

- [ ] `params: [010]` を 8、`[0o10]` / `[0x8]` / `[+0x8]` / `[1_0]` / `[08]` を yaml.v3 と同じ値に読む（`010` × `gt 9` を「永久に発火しない」と検出する）
- [ ] `absent(up{job="a", job!="b"}) and vector(1)` とサブクエリの `absent` を「値を返さない」と誤判定しない。`absent(up{job="a"}) and on(job) m{job="b"}` は従来どおり検出する
- [ ] 二重引用の行末のエスケープした空白・`|+` のファイル末・ブロックスカラーの空白だけの行を YAML のとおりに読む
- [ ] `gte` / `lte` / `eq` / `ne` / `within_range_included` / `outside_range_included` を違反にする
- [ ] 任意スコープにしか `lightweight.claim=true` の利用者名マッパーが無い軽量のクライアントを検出する
- [ ] `directGrantsOnly: true` を直接アクセスとして数える（検査 5・検査 7・SA の検査）。`directAccessGrantsEnabled` 未設定も数える
- [ ] `user.attribute=Username` を利用者名のプロパティと認める（`USERNAME` は認めない）
- [ ] クライアントポリシーの実行器の報告が 1 件になる
- [ ] 実データ（Grafana の provisioning の写し 2 つ・realm）が通る

## 検証

TBD

## 未検証・射程外

- Grafana の文字列型の欄（`title` / `condition` / `refId` / `datasourceUid`）は、yaml.v3 が plain を文字列の欄へ読むとき**元の綴り**を入れる（`refId: 010` は "010"）。
  検査器は木の値を文字列にする（"8"）。検査器の中では両側が同じ読み方なので鎖の照合はずれないが、`condition: 010` と `refId: "8"` を組むと検査器は繋ぎ、Grafana は繋がない。実データは英字の refId だけ。記録に留める。
- 稼働 Grafana / Keycloak では確かめていない（LIVE なし）。
