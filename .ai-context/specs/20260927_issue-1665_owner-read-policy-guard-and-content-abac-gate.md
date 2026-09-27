---
title: 所有者の読み取りのポリシーが消えたら検知してシステム管理者へ知らせ、内容の ABAC の有効化をその存在で門にする（#1665）
type: spec
status: in-progress
related_ids: [FR-05, FR-19, NFR-09, NFR-21, UC-05, SC-09, ADR-0121, ADR-0119, ADR-0036, ADR-0006, IADR-0253, IADR-0379, IADR-0473, IADR-0476, IADR-0480, IADR-0165]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 2・4・6・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-01・D-02
issue: "#1665"
---

# 仕様書: 所有者の読み取りのポリシーの消失の検知・通知と、内容の ABAC の有効化の門（#1665）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、`origin/main` を読み取り専用で参照）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-05**（ABAC）、FR-19（個人資料）、**NFR-09**（文書単位の認可）、NFR-21（障害検出）、UC-05、SC-09（ポリシーの口。削除の規則は変えない）
- 計画 ADR: **ADR-0121 決定 2**（内容の ABAC は所有者の read ポリシーが有効な状態で入っていることを確かめてから有効にする。無ければ有効にしない。
  削除・無効化されたら検知してシステム管理者へ知らせる。検知・通知は有効化より前〔遅くとも同時〕に働かせる。削除そのものは止めない。
  検知の手段は実装の IADR で決める）、**決定 4 の 3 番目**（検知と通知）と 4 番目の前提（門）、決定 6（3 点セット）、フォローアップ 2。
  ADR-0119 決定 3・4（内容の ABAC の本体は #1615）、ADR-0036 D-01・D-02、ADR-0006（アラートは Alertmanager）
- 関連 IADR: IADR-0480（所有者の read ポリシーの形・dev seed・本番の投入手順）、IADR-0253（1 ポリシー = 1 分岐・束縛は評価器の中だけ）、
  IADR-0379（east-west gRPC の契約の置き場と versioning）、IADR-0473（認可サービスの定期処理と計器・アラートの前例）、
  IADR-0476（DocumentService の読み取りの判定）、IADR-0165（Grafana の暫定アラート）
- 起点 issue: #1665（planning#688 の裁定の実装。#1664 の後段、#1615 の前段）

## 目的・背景

- #1664 で所有者の read ポリシー（`read`・利用者の条件なし・文書の条件 `owner ∈ {${current_user}}` だけ）を dev seed に入れ、本番は配備の手順で
  システム管理者が投入することにした。ところが**このポリシーは SC-09 から自由に削除・無効化できる**（ADR-0121 実測 6）。
- 消されると所有者は自分の文書を読めなくなるが、**誤りとして表に出ない**（読めないだけ）。内容の ABAC（#1615）を有効にした後は、機械クライアント
  （AST の KB の書き手）が自分の文書を静かに読めなくなり、AST が重複を静かに作る（ADR-0121 §理由）。
- そこで (1) ポリシーの存在を判定する部品、(2) 消えたときの検知と通知、(3) 内容の ABAC の有効化の門、を先に入れる（ADR-0121 決定 4 の 3 番目。
  #1615 より前か同時にマージする）。**内容の ABAC そのものは本件では有効にしない**（門を作るだけ。#1615 が門を読む）。

## 設計

### 1. 存在の判定（認可サービスの Domain）

`OwnerReadPolicyShape.Matches(AbacPolicy)` を置く。**真になるのは次をすべて満たすときだけ**:

- `IsActive` が真
- `Action` が `read`（評価器と同じ序数比較）
- `UserConditions` が 0 キー（キーが在れば、値が空でも「条件あり」。空の許可値は誰にも一致しない＝全員に効く所有者の分岐ではない）
- `DocumentConditions` が**ちょうど 1 キー**で、キーが `owner`（序数比較。`Owner` は別のキーとして扱う —— 消費側の述語が序数で比べるので、`Owner` の
  ポリシーは所有者の分岐として働かない）
- 値の**集合**が `{${current_user}}` に等しい（同じ値の重複は束縛で 1 値になるので同値として認める。他の値・`${current_groups}`・空は認めない）

「形が近いが違う」ものを在ると誤認しない（issue のやること 1）。数えるのは `CountActive(IEnumerable<AbacPolicy>)`。

### 2. 検知と通知（認可サービスの定期処理・計器・警報）

- **定期の検査**: 常駐（`BackgroundService`）が**起動時に 1 回**と、以後**一定周期**（構成 `OwnerReadPolicyCheck:Interval`、既定 `00:01:00`、
  書式 `hh:mm:ss`・下限 1 分。`DepartmentAttributeSync:Interval` と同じ規則）で、DB のポリシーを数える。**opt-in にしない**
  （門より前に常に働いている必要がある。ADR-0121 決定 4）。
- **計器**（Meter はサービス名 `microservices-platform.authorization-service`。部門の同期と同じ）:
  - ゲージ `authz.owner_read_policy.active`（Prometheus 名 `authz_owner_read_policy_active`）＝**直近の検査で数えた件数**。
    🔴 **検査に失敗したとき（DB に届かない等）と、まだ 1 度も検査していないときは系列を出さない**（古い値や 0 を出すと「在る」「無い」を偽る）。
  - カウンタ `authz.owner_read_policy.checks.total{authz.owner_read_policy.outcome=present|absent|failed}`（検査の結末）。
- **ログ**: 無いときは周期ごとに **Error**（投入の手順の在処を添える）。失敗も Error。無い → 在るへ戻ったら Information。
- **警報**（既存の通知の経路。`deploy/prometheus/alerts.yml` とその 3 つの写し。Alertmanager と Grafana の暫定の 2 系統）:
  - `OwnerReadPolicyMissing`（critical）: `authz_owner_read_policy_active < 1`、`for: 5m`。Grafana 版は生の値を `lt 1` で比べる（#1577 の形）。
  - `OwnerReadPolicyCheckSeriesAbsent`（warning）: `absent(authz_owner_read_policy_active)`、`for: 5m`。**見ていない**ことを知らせる
    （認可サービスが止まった・検査が失敗し続けている・収集されていない）。無いと、検査の失敗が続く間 `OwnerReadPolicyMissing` は空ベクタで鳴らない。
- **削除そのものは止めない**（SC-09 の削除・無効化の口は変えない。ADR-0121 決定 2）。

### 3. 門が問い合わせる口（east-west gRPC）

- 既存の `platform.authz.v1.AuthzScope` に rpc `GetOwnerReadPolicyStatus` を**追加**する（非破壊。新しいサービス・チャネル・主体を増やさない。
  DocumentService はすでに同じ口で読み取りのスコープを引いている）。応答は `active_count`（int32）。
  🔴 **proto3 の既定値 0 は「無い」と読まれる**（取り違えは門が閉じる側へ倒れる）。
- 受け手は**毎回 DB から数える**（常駐の直近の値を返さない —— 門が古い値で開かないように）。認可は同じサービスの既存の `ServiceCaller`。

### 4. 内容の ABAC の有効化の門（DocumentService）

- 構成 `ContentAbac:Mode`（`Off` 既定 / `On`）。値域外は起動時に落とす（`DepartmentAttributeSync:Mode` と同じ deny-by-default）。
- `IContentAbacGate`（`IsOpen` と `State`）。状態は `disabled`（構成が Off）/ `owner_read_policy_absent`（On だが 0 件）/
  `owner_read_policy_unknown`（On だが数えられない。宛先の未構成・RPC の失敗・時間切れ・s2s トークンの失敗）/ `open`。
  **開くのは On かつ 1 件以上を確かめたときだけ**（fail-closed）。
- 常駐が起動時に評価し、開くまで 1 分ごとに評価し直す。**1 度開いたらプロセスの寿命の間は閉じない**（ラッチ）——
  ADR-0121 §結果 は「消すと所有者が自分の文書を読めなくなる」をトレードオフとして受け入れ、消えた後は**検知と通知**で気づく設計である。
  門を動的に閉じると、消えた瞬間に内容の ABAC が外れ、機械クライアントへの許可が**広がる**向きに倒れる。
  再起動時は改めて確かめる（無ければ開かない。決定 2 の文言どおり）。
- 計器: ゲージ `documents.content_abac.gate.open`（1 / 0、属性 `documents.content_abac.gate.state`）。
  ログ: On で閉じているときは評価ごとに Warning（理由つき）、開いたら Information、Off は起動時に Information 1 回。
- 🔴 **本件では門を読む判定は無い**（#1615 が `DocumentReadAccess` から読む）。本件の門は状態と計器・ログを出すだけで、読み取りの挙動は変わらない。

## 受け入れ基準

- AC-1（やること 1）: 形の判定は、陽性（正規の形・値の重複）で真、陰性（無効・`read` 以外・利用者の条件あり〔値が空でも〕・文書の条件が空・
  別キーの追加・キーが `Owner`/`author`・値に他の値や `${current_groups}` が混ざる・値が空・値がリテラルの利用者名）で偽。
- AC-2（やること 2）: 認可サービスの検査は、在る→ゲージ＝件数・`present`、無い／無効／形が違う→ゲージ 0・`absent`・Error ログ、
  DB の失敗→ゲージの系列なし・`failed`・Error ログ。無い → 在るへ戻ると Information。周期の構成は値域外で起動時に落ちる。
- AC-3: gRPC `GetOwnerReadPolicyStatus` は DB の件数を返し（在る 1・無い 0・無効 0・形違い 0）、s2s 資格情報が無い／ロールが無いと拒否する。
- AC-4: 警報 2 件が 4 か所（compose の Prometheus・経路 B の inline・Grafana の compose・経路 B）に入り、パリティ検査が通る。
  C# の計器名から導いた Prometheus 名が 4 か所の式に現れる（scripts.repo.test.js）。件数の記述（20 → 22）を数え直す。
- AC-5（やること 3・4）: 門は Off で閉じて認可サービスを呼ばない。On で 1 件以上なら開く（陽性対照）。On で 0 件（無い・無効・形違いは
  認可サービス側で 0 になる）なら閉じて `owner_read_policy_absent`、数えられなければ `owner_read_policy_unknown`。閉じた理由はログ（Warning）と
  計器（ゲージ 0・状態の属性）に出る。1 度開いたら後で 0 件になっても開いたまま。値域外の構成は起動時に落ちる。
- AC-6: gRPC の問い合わせは RpcException・s2s トークンの失敗・時間切れを「数えられない」（null）へ畳み、呼び出し元の取り消しは取り消しとして出す。
- AC-7: 運用仕様書（投入の節の「検知と通知はまだ無い」を改める・警報の節・障害対応・門の構成）、通信仕様書（east-west gRPC）、テスト仕様書、
  セキュリティ仕様書、IADR 新設、IADR-0480 の日付つき追記。
- AC-8: 変異 3 件以上（形の判定を緩める・検査の失敗で 0 を出す・門を未確認で開く 等）で試験が赤になる。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n -E '検知と通知|検知・通知|消されたときの検知|消えたときの検知|ADR-0121 決定 2|内容の ABAC の有効化'`（誤りになる側の文字列。凍結の specs は除く）
- `git grep -n -E '(20|19) (件|ルール)'`・`20 件返す`・`＝ \*\*20\*\*`・`19 → 20`（警報の件数。導出値）
- `git grep -n -e 'department_sync' -- deploy scripts docs`（警報を 4 か所に置く前例の在処）
- `git grep -n 'platform.authz.v1' -- deploy scripts docs`・`scripts/proto-contract-baseline.json`（proto の追加の追随先）
- `git grep -n -l -E 'owner_read|OwnerReadPolicy|所有者の読み取りのポリシー'`（#1664 の成果物）
- `git grep -n -E '内容の ABAC' -- docs '*.cs'`（門の消費者 #1615 の差し込み口）

### 結果（変えるもの）

- 認可サービス: `Domain/`（形の判定を新設）、`Features/Authz/OwnerReadPolicyGuard/`（検査・計器・常駐を新設）、
  `Features/Authz/ResolveScope/GrpcService.cs`（rpc を足す）、`Program.cs`（登録）。
- 契約: `Protos/platform/authz/v1/authz_scope.proto`（rpc・message の追加）、`scripts/proto-contract-baseline.json`（`--update`）。
- DocumentService: `Domain/Ports/`（口）、`Infrastructure/ExternalServices/`（gRPC 実装・未構成の縮退）、`Features/Documents/ContentAbacGate/`（門・常駐）、
  `Common/Observability/`（計器）、`Program.cs`（登録）。
- 警報: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・
  `deploy/local/observability/grafana.yaml`（2 件と、冒頭の件数 20 → 22）、`scripts/check-grafana-alerting.js`（冒頭の件数の注記）、
  `scripts/scripts.repo.test.js`（計器名と式の突き合わせ）。
- 文書: `docs/operations/operations.md`（L454・L457 の「検知と通知はまだ無い」、監視の節の件数 L783・L823、**既に腐っている L842 の「19 ルール」**も
  22 へ直す、障害対応の表、門の構成の節）、`docs/api/east-west-grpc.md`（参照実装の節に rpc を足す）、`docs/tests/FR-05_abac-access-control.md`（T-39〜）、
  `docs/security/security.md`（検知と門の節）、`deploy/local/abac-seed/README.md`（切り戻しの節の「消すと 404」に警報を添える）、
  `scripts/test-spec-coverage-baseline.json`（`--update`）、IADR 新設、`IADR-0480`（日付つき追記）、`.ai-context/adr/README.md`（索引）。

### 除外したもの

- **内容の ABAC の本体**（`DocumentReadAccess` が門を読む・判定を認可サービスへ寄せる）: #1615（ADR-0121 決定 4 の 4 番目・決定 5）。
- **AST の古い写しの削除**（決定 3）・**SC-09 の動的束縛の入力**（ADR-0036 フォローアップ 5）: 本件の外。
- **SC-09 の削除・無効化の口**（`DeletePolicy`・`SetPolicyActive`・`UpdatePolicy`）: 削除を止めない（決定 2）ので変えない。変更の直後に即時の検査を
  走らせる案は採らない（周期 1 分＋警報の `for: 5m` で検知の遅れは最大およそ 6 分。複数レプリカの間で即時の検査を揃える仕組みが要り、見合わない）。
- `IADR-0480` 本文 L64・L91（「検知と通知は無い」）: 凍結記録。日付つき追記で「入った」を足す。
- `.ai-context/specs/` の過去の作業仕様書: point-in-time の記録で書き換えない。
- `IADR-0033`・`IADR-0355` 等の grep の当たり（`IADR-0121 決定 2` は別の IADR・`19 → 20 モジュール` は合成点の数）: 別の事柄。
- 通知サービス（SC-10 のアプリ内通知）: 運用の警報は Alertmanager / Grafana の経路が正（ADR-0006・IADR-0165）。システム管理者宛のアプリ内通知の
  宛先解決は存在しない（新設は計画外の機能追加になる）。

## 配備の順番

- 本件は ADR-0121 決定 4 の **3 番目**（検知と通知）と、4 番目の門の部品である。**内容の ABAC はまだ有効にならない**（`ContentAbac:Mode` の既定は Off、
  かつ門を読む判定が無い）。
- 🔴 **authorization-service を document-service より先に（または同時に）上げる。** 門の問い合わせ先の rpc は新しい認可サービスにしか無い。
  逆順でも門は `owner_read_policy_unknown` で閉じたまま（UNIMPLEMENTED は RpcException）で、安全側である。
- 警報のルールは Prometheus・Grafana の再読込で入る。

## 検証

- `dotnet build`（platform・knowledge の slnx 全体）
- AuthorizationService.Tests・DocumentService.Tests・Platform.Shared.Infrastructure.Tests・Platform.Bff.Tests（契約の生成物の利用者）全件
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`check-trace-blocks`・`check-test-spec-coverage`・`check-test-traceability`・`check-cross-repo-refs`・
  `check-plan-id-qualification`・`check-proto-contracts`・`check-grafana-alerting`・`check-prometheus-alerts-parity`・`check-grafana-provisioning-parity`・
  `gen-knowledge-graph --check`・`check-reading-budget`・`check-commit-messages --range=origin/develop..HEAD`
- 変異 3 件以上（コミット済みの状態で当て、`git show HEAD:<path> > <path>` で戻す）
