---
title: IADR-0128 変換ジョブの再変換は BFF で管理者限定に絞り、照会は据え置き、下流は代償統制を機械検査で固定する
type: impl-adr
status: Accepted
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - IADR-0042
  - IADR-0029
  - IADR-0026
  - IADR-0039
  - IADR-0127
author: Claude
created: 2026-08-05
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# IADR-0128: 変換ジョブの再変換は BFF で管理者限定に絞り、照会は据え置き、下流は代償統制を機械検査で固定する

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **FR-12** ／ **UC-06** ／ **SC-07**
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-07 §データソース・**2026-08-04 確定**）
- 関連 ADR: [[IADR-0042]]（決定 3 を本 IADR が部分改定＝ retry を例外化）／
  [IADR-0029](IADR-0029_config-info-api-placement-and-drift-granularity.md)（ワーカーの最小 HTTP サーフェス）／
  [IADR-0026](IADR-0026_mesh-mtls-supersedes-network-isolation.md)（mTLS 第一防御・ネットワーク分離は多層防御）／
  [IADR-0039](IADR-0039_datasource-management-bff-and-role-gating.md)（管理系ロール）
- 関連する実装仕様書: [作業仕様書 #501](../specs/20260805_issue-501_retry-admin-only.md) ／
  [画面仕様書 SC-07](../screens/SC-07_conversion-jobs.md) ／ [テスト仕様書 SC-07](../tests/SC-07_conversion-jobs.md)
- 本リポジトリの起点: #501（画面側 #503 / PR #508・親 #454）

## コンテキストと課題

計画は 2026-08-04 に「**再変換の実行権限は管理者ロールに限る**。**本画面のアクセス制御と API の権限を揃える**
—— API 側だけ緩いと画面の制御が意味を持たない」と確定した。しかし実装（`de55761`）では
`MapGroup("/bff/conversion/jobs")` が**グループ一括で admin または operator** を許しており、`retry` も同じ扱いだった
（[[IADR-0042]] 決定 3 が定めた姿）。画面のボタンを隠しても、API を直接叩ける運用者は retry できる。

同時に、次の 2 点が「単純にグループを絞る」ことを許さない。

1. **照会（閲覧）は、計画と実装が既に食い違っている。** 計画は §共通シェル
   （[`01_screens.md:115`](../../planning/projects/microservices-platform/05_screens/01_screens.md)
   「アクセス制御の割当（モックのバッジ準拠）: **SC-05/06/07 = 管理者（管理）**」）と §SC-07
   （同 `:250`「アクセス制御: **管理者ロール限定。**」）で **SC-07 全体を管理者ロール限定**と定めている。
   すなわち閲覧ロールは**未確定なのではなく**、現状の `admin` ＋ `operator` が
   [[IADR-0039]] 決定 1 に由来する**既知の逸脱**（実装が計画より緩い）である。
   この差異は planning#198 提案 8 で**計画改訂か実装是正かの裁定を仰いでいる最中**であり、
   ここでグループごと絞ると、**裁定を待たずに実装が先に答えを出す**ことになる。
2. **下流（ConversionService の `/jobs/*`）はそもそも認可を課していない**。BFF だけ絞って
   「揃った」と称すると、下流に到達できる経路がある限り穴は残る。

決めること: (1) retry を admin へ絞る**方法**、(2) 照会の扱い、(3) 下流に対する措置。

## 検討した選択肢

### (1) retry を admin へ絞る方法

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | グループの認可はそのまま、`retry` にだけ `RequireAuthorization(PlatformAuthPolicies.AdminOnly)` を重ねる | 認可メタデータは AND 合成されるため実効は admin のみ。**グループ定義に差分が出ない＝照会が巻き添えにならないことがコード上で自明**。**再利用するのは既存の名前付きポリシー**（`/bff/authz` のグループ・`/bff/dashboard` のエンドポイントが使う `PlatformAuthPolicies.AdminOnly`）であり、管理者限定の表現がリポジトリ内で 1 種類に保たれる。**ただし「グループとエンドポイントの両方に認可を重ねる」形そのものは本 PR が初出**である（`grep -rn "RequireAuthorization" --include=*.cs src/platform src/knowledge` の 18 件〔コメント 4 件を除く呼び出し 14 箇所〕を実測。`AuthzEndpoints.cs:28` は入れ子 `MapGroup` の内側だけ、`DashboardBffEndpoints.cs:21` のグループには認可が無い）。挙動は `Retry_AsOperator_IsForbidden` / `GetById_AsOperator_IsAllowed` の実測で確かめた |
| B | グループを admin 限定にし、照会 2 本に operator 許可を個別に付け直す | 実効は同じだが、**閲覧の権限指定が「グループ＋例外」から「例外＋例外」へ散る**。閲覧の裁定（planning#198）が出たときに触る箇所が増える。差分も大きく、レビューで「閲覧も変わったのでは」と読み違えられる |
| C | `retry` を別の `MapGroup` へ分離する | 意図は最も明示的だが、同一プレフィックスのグループが 2 つ並び、共通の `Forwarding` ヘルパや `WithTags` が重複する。**構造の変更に見合う利得が無い** |
| D | 認可を BFF に置かず、下流（ConversionService）で判定する | 下流には認証基盤が無い（§(3)）。エッジ認証の一元化（[[IADR-0042]] 決定 3・[[IADR-0039]]）を崩す |

### (2) 照会の扱い

| 案 | 評価 |
| --- | --- |
| **据え置き（採用）** | 2026-08-04 の確定が命じたのは「**再変換の実行権限**」の是正であり、本 PR はそこだけを直す。照会が計画（`:115`・`:250` の管理者ロール限定）と食い違っていること自体は既知だが、それは [[IADR-0039]] 決定 1 由来の**別の差異**であり、**その是正の向き（計画改訂か実装是正か）は planning#198 提案 8 の裁定に従う**。ここで併せて絞ると、**裁定を待たずに実装が先に答えを出す**ことになる |
| 一緒に admin へ絞る | 実装は計画の字面へ寄るが、**planning#198 の裁定を実装が先取りする**。裁定が「計画側を admin ＋ operator へ改訂する」と出れば戻す作業が要る。さらに同じ [[IADR-0039]] 決定 1 が適用される SC-05・SC-06 だけが取り残され、**同一の差異の扱いが画面ごとにばらつく** |

### (3) 下流（ConversionService `/jobs/*`）への措置

実測（作業仕様書 §下流の調査）:

- `MapGroup("/jobs")` に `RequireAuthorization` は**無い**。`Program.cs` は `AddPlatformAuth` /
  `UseAuthentication` / `UseAuthorization` を**呼んでいない**（認証基盤そのものが無い）。
  すなわち下流は「operator に緩い」のではなく **ロールの区別が存在しない**。
- 到達性: compose は `expose` のみ（host 非公開）／Helm は ClusterIP ＋ default-deny NetworkPolicy ／
  Istio VirtualService の経路は `/bff/*` → bff-service と catch-all → frontend-service のみ。
  **外部から conversion-service へ到達する経路は無い**。

| 案 | 評価 |
| --- | --- |
| **代償統制を機械検査へ載せる（採用）** | 「認可を課さない代わりにネットワークで塞ぐ」という前提が、`NetworkIsolationTests` の列挙から `conversion-service` が漏れていたため**機械で守られていなかった**。列挙に加え、`ports:` の追加を CI で止める |
| 下流に `RequireAuthorization` を足す | **そのままでは起動後に `/jobs/*` が全滅する**（認可ミドルウェア不在で `Endpoint contains authorization metadata, but a middleware was not found`）。認証配線と全環境への `Auth:Authority` 注入を伴い、[[IADR-0029]] / [[IADR-0042]] 決定 3 の構造変更になる。**本 issue が求める「揃える」の範囲を超える独立の決定** |
| 何もしない | 代償統制が無防備なまま残る。**BFF を絞った意味が構成変更 1 行で失われうる** |

## 決定

1. **`POST /bff/conversion/jobs/{id}/retry` は `platform-admin` のみに限定する。** グループの認可
   （admin または operator）は残したまま、当該エンドポイントに `PlatformAuthPolicies.AdminOnly` を重ねる（案 A）。
   実効要件は「(admin または operator) かつ admin」＝ **admin のみ**。
2. **照会（`GET /bff/conversion/jobs`・`GET /bff/conversion/jobs/{id}`）は据え置く**（admin または operator）。
   これは計画（`01_screens.md:115`・`:250` の**管理者ロール限定**）に対する [[IADR-0039]] 決定 1 由来の
   **既知の逸脱**である。是正の向き（計画改訂か実装是正か）は **planning#198 提案 8 の裁定に従う**。
   **operator が照会できることをテストで固定**し、将来の巻き添え変更を検出できるようにする。
3. **下流 ConversionService の `/jobs/*` の姿勢そのものは変えない。新しいのは代償統制の機械化である。**
   - **（既存決定の維持を確認した部分。本 IADR の新規決定ではない）** アプリ層の認可を課さないことは、
     [[IADR-0042]] 決定 3（「ワーカー自身は最小 HTTP サーフェスに留め認可は課さない」）と [[IADR-0029]] が
     **既に決めている**。本 IADR は §(3) の実測でその前提が崩れていないことを確かめ、**維持することを確認する**
     （retry を BFF で絞ったことは下流の姿勢に影響しない）。
   - **（本 IADR が新たに決める部分）** その前提である**ネットワーク分離（代償統制）を機械検査へ載せる**。
     到達不能の論拠は **4 本**（compose の host 非公開／Helm Service が ClusterIP ／ NetworkPolicy の
     既定 deny ／ Istio VirtualService に経路が無い）であり、本 IADR は**そのうち 2 本**を固定する。
     (a) `NetworkIsolationTests.InternalAppServices` に `conversion-service` を加え、compose の
     host 公開の回帰を止める（**列挙から漏れていたため、これまで誰も止められなかった**）。
     (b) `InternalServices_HelmServicesMustStayClusterIp` で Helm の `service.yaml` に `type:` /
     `nodePort:` が現れないことを固定する（**`type` の変更は最も起こりやすい公開経路**であり、
     同ファイルには Helm 側を見る先例〔`WikiJs_HelmIngressDisabledByDefault`〕がある）。
     **残る 2 本（NetworkPolicy の例外追加・Istio VirtualService へのルート追加）は機械では止まらない**
     （下記フォローアップ 4）。
4. **権限テストは「拒否される」側を主とする。** `Retry_AsOperator_IsForbidden`（403）を置く。
   「admin で通ること」だけでは、実は誰でも通る状態を検出できない。
   **応答を 403（無認証は 401）とする根拠は [[IADR-0039]] 決定 3** である —— 「権限外は 403（無認証は 401）
   とする。データソースは文書のような『存在自体の秘匿』対象ではなく（[[IADR-0009]] とは性質が異なる）、
   管理 API としては標準的な 403/401 が適切」。変換ジョブも同じ管理系の運用資産であるため、
   **404 秘匿は採らない**（画面の存在秘匿はフロントの `RequireRole` が担う）。

## 理由

- 決定 1 の形（案 A）は、**変更の影響範囲がコードの差分と一致する**。グループ定義に手を入れないため、
  「照会の権限は変えていない」ことをレビューで grep 1 回で確認できる。認可メタデータの AND 合成は
  ASP.NET Core の `AuthorizationPolicy.CombineAsync` の仕様である。重ねるポリシー自体は
  `/bff/authz`・`/bff/dashboard` が使う既存の `PlatformAuthPolicies.AdminOnly` をそのまま用いる。

  > **［2026-08-09 追記 / #544］`/bff/dashboard` はもう `AdminOnly` を使わない。**
  > 計画 §SC-10 を正として **admin ＋ operator** へ広げたためである（[[IADR-0129]] 決定 4 の追記）。
  > **本決定 1 は変わらない** —— 重ねるポリシーが既存の名前付きポリシーであることが要点であり、
  > 現在の利用例は `/bff/authz`・`/bff/datasources`・`/bff/documents`・`/bff/tags`（**#640**）である
  > （実測）。**`AdminOnly` の表現がリポジトリ内で 1 種類に保たれている**という性質も維持されている。
  **一方、「グループとエンドポイントの両方に認可を課す」形の先例はリポジトリ内に無い**——
  `grep -rn "RequireAuthorization" --include=*.cs src/platform src/knowledge` の **18 件**
  （コメント 4 件を除く**呼び出し 14 箇所**。是正前の `origin/develop` は 17 件 / 13 箇所。
  別プロジェクトの `src/ai-stock-trading` は母集合から除く）を実測したところ、
  `AuthzEndpoints.cs:28` は入れ子 `MapGroup` の
  内側にだけ認可を置き、`DashboardBffEndpoints.cs:21` のグループには認可が無く `:71` のエンドポイント
  だけが持つ。**本 PR が初出である**ため、AND 合成の実効（operator は 403・照会は 200）は
  仕様の読みに頼らずテストで固定した（決定 4）。
- 決定 2 は、**本 PR が是正するのは 2026-08-04 に確定した retry だけである**ことの帰結である。
  照会の差異（**計画は管理者限定・実装は admin ＋ operator**）は [[IADR-0039]] 決定 1 由来の既知の逸脱であり、
  planning#198 提案 8 が**計画改訂か実装是正かを裁定中**である。**実装が先に答えを出さない。**
- 決定 3 は、下流の姿勢が**ロールの非対称ではない**という実測に基づく。BFF を絞ることで
  「運用者ロールを持つ人間が retry を実行する」経路は塞がる（運用者が持つのは Keycloak ロールであって
  クラスタ内ネットワークではない）。同 Namespace 内 Pod や `kubectl port-forward` からの到達は
  Keycloak ロールではなく**クラスタ権限**の問題で、[[IADR-0026]] の防御対象である。
  一方、その代償統制が**機械で守られていなかった**のは実際の欠落であり、そこを塞ぐのが費用対効果に優れる。

## 結果

- 良い影響:
  - 計画 2026-08-04 の確定（再変換＝管理者ロール限定）が API 面で満たされ、画面の制御が意味を持つ。
  - 照会の既知の逸脱（[[IADR-0039]] 決定 1 由来）が据え置かれ、planning#198 の裁定を実装が先取りしない。
  - 下流の「認可を課さない」前提を支える**到達不能の論拠 4 本のうち 2 本**が回帰ガード付きになる ——
    compose への `ports:` 追加と、Helm `service.yaml` への `type:` / `nodePort:` 追加で CI が落ちる。
    **残る 2 本（NetworkPolicy の例外追加・Istio VirtualService へのルート追加）は依然として機械では
    止まらない**（フォローアップ 4）。
- 悪い影響・トレードオフ:
  - **retry の認可がグループ定義とエンドポイント定義の 2 箇所に分かれる**。読み手が
    「グループが admin+operator だから retry も operator 可」と誤読しうるため、コード内コメントと
    [[IADR-0042]] 決定 3 の［追記］の両方で例外であることを明示する。
  - **画面と API の一時的な不整合**。本変更が PR #508（画面側）より先にマージされると、その間だけ
    「operator に再変換ボタンが見えるが API は 403」になる。計画が禁じたのは「API 側だけ緩い」向きであり、
    逆向きの一時不整合は許容できる（かつ #508 のマージで解消する）。
    **［2026-08-05 追記］PR #508（#503 / [[IADR-0127]]）が先に develop へ入ったため、この過渡状態は生じない** —— 本 PR のマージで画面と API の権限が同時に揃う（[`docs/adr/README.md`](README.md) の索引行・[作業仕様書 #501](../specs/20260805_issue-501_retry-admin-only.md) §目的・背景 の［追記］と同旨）。
  - 下流は依然として認証を持たない。ゼロトラストの徹底は未達である（下記フォローアップ 1）。
- フォローアップ:
  1. **ConversionService へのアプリ層認証の要否**を別 issue で判断する（同 Namespace 内 Pod からの到達は残る）。
     判断時は [[IADR-0029]] の「最小 HTTP サーフェス」との整合を再検討する。
  2. `ingestion-service` も `NetworkIsolationTests` の列挙外である（同型の穴）。今回入れないのは、
     HTTP サーフェスが `MapPlatformIntrospection()` 1 件のみで**副作用のある操作を持たず**、
     FR-12 / SC-07 の射程外だからである。**「公開してよい」という意味ではない**旨を
     `NetworkIsolationTests` の列挙にもコメントで残した（次に触る人の読み違えを防ぐため）。
  3. **閲覧ロールの差異の裁定**（planning#198 提案 8。**計画は SC-07 全体を管理者限定と定め、実装は
     admin ＋ operator** という既知の逸脱）が出たら、計画改訂・実装是正のいずれであれ照会側の権限と
     決定 2 を追随させる。SC-05・SC-06 も同じ [[IADR-0039]] 決定 1 の適用先であり、まとめて扱う。
  4. **代償統制の残り 2 本を機械検査へ載せる** —— NetworkPolicy への `istio-system` 例外追加と、
     Istio VirtualService への内部サービス向けルート追加は、いまも誰も止められない。
     いずれも「BFF 以外の公開エッジを作る」変更であり、対象は conversion に限らず内部サービス全体である
     （本 issue の射程を超えるため別 issue で扱う）。

## 関連

- Supersedes: なし（[[IADR-0042]] 決定 3 を**部分改定**する。同決定の本文と
  [`docs/adr/README.md`](README.md) の索引行の両方に日付付き［追記］を入れた
  —— 書式は `planning/.claude/rules/adr.md` §部分改定 2・4 と先例 [[IADR-0084]] / [[IADR-0087]] に倣う）
- Superseded by: なし
- **[[IADR-0039]] は本 IADR の改定対象外である。** 同 IADR §影響 は
  「**SC-05・SC-07 も本方針（管理系＝ admin/operator）に従う（各 IADR で個別記録）**」と明記して
  画面ごとの記録を委任しており、SC-07 の個別記録は [[IADR-0042]]（→ 本 IADR）の系列が担っている。
  よって retry の権限改定は [[IADR-0042]] 決定 3 の部分改定として閉じ、[[IADR-0039]] 決定 1 の本文には
  手を入れない（一般方針としての決定 1 は `Accepted` のまま有効。SC-05・SC-06 への適用も不変）。
  なお **PR #508（#503 / [[IADR-0127]]）は develop へマージ済みであり**（`5ce3ec9`）、[[IADR-0039]] 決定 1 へ
  「計画との差異は planning#198 で裁定待ち」の［2026-08-05 追記］を入れている。
  **本 PR は同じ［追記］を「retry は画面・API の両側で是正済み／照会は planning#198 の裁定まで据え置き」へ
  書き分けた**（本 IADR 決定 1・決定 2 の反映）。一般方針としての決定 1 の本文には手を入れていない。
