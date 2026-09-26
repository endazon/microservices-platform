---
title: IADR-0472 SC-06 で明示した部門の値域検証は、AuthorizationService の east-west gRPC に足した照会（CheckDepartmentCodes）で行い、引けなければ 502 で保存しない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-01, UC-04, SC-06, ADR-0074, ADR-0115, ADR-0064, ADR-0029, ADR-0075, IADR-0199, IADR-0329, IADR-0359, IADR-0379, IADR-0401, IADR-0431, IADR-0468]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 1・5
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md 決定 4
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md ★未確定表「部門コードの値域」
related_specs:
  - ../specs/20260926_issue-1557_department-domain-validation.md
---

# IADR-0472: 明示した部門の値域検証を、AuthorizationService の gRPC 照会で行う（#1557）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1557。計画 ADR-0115 決定 5 の実装）

## 起点・関連

- 関連する計画書 ID: FR-05（ABAC 文書属性）・FR-01 / UC-04（データソース登録）・SC-06（データソース管理）
- 関連する計画 ADR: ADR-0115 決定 1（値域＝realm の `/department/<code>` の `<code>`）・決定 5（明示値は書き込み時に値域で検証する。ADR-0074 決定 4 と同じ形）／
  ADR-0074 決定 4（手で入れる写像は登録時に検証し、通らないものは保存しない。検証は SC-17 側のクライアントで行う）／ADR-0064 決定 4（取り込み経路の主体と SC-17 側の主体を分ける）
- 関連する実装 ADR: IADR-0468（登録者の部門グループから導く。値域検証を #1557 へ送った）・IADR-0359（写像表と実在検証）・IADR-0329（`identity-admin`）・
  IADR-0379 決定 4 / IADR-0401 決定 2・3（east-west に利用者トークンを載せない・読み口は照会の形へ狭める）・IADR-0431（gRPC だけの口と未宣言時の縮退）・IADR-0199（予約値は未解決の記録）
- 作業仕様書: `../specs/20260926_issue-1557_department-domain-validation.md`

## コンテキストと課題

計画 ADR-0115 決定 5 は「SC-06 で明示した `department` は、書き込み時に値域（決定 1）で検証する（ADR-0074 決定 4 と同じ形）」と定めた。
IADR-0468 §(d) は、DataSourceService が realm の部門グループを読む口を持たないため、この検証を送った。決めるべき点は 4 つあった。
**(a) DataSourceService はどこから値域を読むか**、**(b) 引けないときにどう応えるか**、**(c) 予約値 `unassigned` を明示したらどうするか**、**(d) どの書き込みを検証するか**。

## 実測 —— ADR-0074 決定 4 は今どう検証しているか

- `OwnerMappingValidation.ValidateAsync` を登録・全置換・部分更新の 3 端点が呼ぶ。写像先を `IPlatformUserDirectory.LookupAsync` で**照会**し
  （列挙しない。IADR-0401 決定 3）、**居なければ 400（`ValidationProblem` の `errors`）、引けなければ 502（`{ message }`）**で、どちらも保存しない。
- 輸送は gRPC（`platform.authz.v1.UserDirectory/CheckUsernames`・s2s の `platform-service`）で、後段は AuthorizationService の `IIdentityAdminClient`
  （`identity-admin`。realm を読む主体は 1 つ）。配備（helm / compose）の DataSourceService は `Services:AuthorizationServiceGrpc` を宣言している。
- `IIdentityAdminClient` は既にグループを読む（`GetUserGroupsAsync` / `SearchGroupsAsync` / `GetGroupsByIdsAsync`）。`identity-admin` の権限（`view-users`）でグループの閲覧は足りる。
  ただし `SearchGroupsAsync` は**名前の部分一致を上限つきで返す**ので、値域の判定に流用すると一致の多い realm で目的のグループが上限の外へ落ちる。

## 検討した選択肢

### (a) 値域の読み口

1. **DataSourceService に読み取り専用の Keycloak Admin API クライアントを持たせる**（専用のサービスアカウント・`view-groups` / `query-groups` 相当）—— 却下。
   realm を読む主体が 2 つになり、ADR-0074 決定 4 が名指しした「SC-17 側のクライアント」から外れる（ADR-0064 決定 4 が分けた線も崩れる）。
   realm に新しいクライアントと資格情報を宣言することになり、稼働 realm（AST の PoC と共有）への適用も要る。
2. **既存の基盤サービス（AuthorizationService）が部門グループの一覧をキャッシュして出す** —— 形を変えて採用（キャッシュは持たない・一覧は出さない）。
   - **一覧を出さない**: 呼び出し元が要る問いは「この値は値域に在るか」だけである。`CheckUsernames` と同じく**照会の形**に狭める（IADR-0401 決定 3）。
   - **キャッシュを持たない**: 書き込みは管理者の低頻度な操作で、1 回の書き込みで引くのは 1 値だけである。キャッシュを置くと、グループの削除・改名が
     反映されるまでの間に値域の外の値が通る（決定 5 の検証が遅れて効く）。ADR-0074 決定 4 の実装もキャッシュを持たない。
3. **構成で部門コードの一覧を与える** —— 却下。値域は realm のグループである（ADR-0115 決定 1）。構成の一覧は realm と黙ってずれ、
   開発 seed の属性辞書 `department.allowedValues`（`finance` / `legal` を含む）がすでにずれている実例がある（IADR-0468 §(d)）。

### (b) 引けないとき

- **502 で保存しない**（採用）。ADR-0074 決定 4 の実装と同じ挙動・同じ本文の形（`message`）。「確かめられなかった」を「値域の外」（400）と報告しない。
  黙って受理すると検証が障害時に外れ、黙って 400 にすると嘘の理由になる（原則 A）。

### (c) 予約値 `unassigned` の明示

- **受け付ける・照会しない**（採用）。`unassigned` は部門コードではなく「解決できなかった」の記録であり（IADR-0199）、取り込み経路・登録時の導出は
  空白・欠落と同じ「未解決」として扱っている（`DataSource.IsDepartmentUnresolved`）。**同じ述語で検証の対象から外す**（「未解決」の定義を 2 つ持たない）。
  結果として、認可サービスが落ちていても部門を空にする・予約値へ戻す操作は通る。登録時に明示した `unassigned` は、従来どおり登録者の部門で補われ得る（IADR-0468 決定 3）。

### (d) どの書き込みを検証するか

- **POST / PUT / PATCH のすべてで、`department` が未解決でないときだけ**（採用）。`defaultAttributes` を送らない PATCH は照会しない。
- ~~PUT は値が変わっていなくても検証する~~ ［2026-09-26 追記 / #1557 監査で改めた］**更新（PUT / PATCH）は、部門が保存済みの値から変わったときだけ検証する**（比較は保存される値そのもので序数一致。trim しない）。**登録は明示されていれば常に検証する。**
  - 理由: SC-06 の既定属性フォームは保存済みの部門を毎回送り返す。変わっていない値まで検証すると、値域が定まる前に保存された値（旧属性辞書の `finance` 等）を持つソースは**機密区分・ライフサイクル・写像表だけの編集まで 400** になり、認可サービスや Keycloak が落ちている間は**あらゆる編集が 502** になる（無関係な操作を巻き込む）。検証の目的は「値域の外の値を**新しく**書かせない」ことであり、変わった値だけを見れば果たせる。削除・改名で値域の外になった既存の値は、次に部門を変えるときに検出される。
  - 写像表の検証（ADR-0074 決定 4）が送られた全対を毎回検証するのと非対称になるが、写像表は変わっていない対を送り返す画面の経路で旧データが問題になった実測が無い。部門は値域が後から定まったため、旧データが確実に存在する。
- **登録者の部門から導いた値は検証しない**。導出は `/department/<code>` のフルパスからしか作らず、構成上つねに値域の内側である。

## 決定

1. **AuthorizationService の east-west gRPC `platform.authz.v1.UserDirectory` に `CheckDepartmentCodes` を足す。** 照会であって列挙ではない（部門グループの一覧を返さない）。
   `ServiceCaller`（`platform-service`）だけを通し、管理者の利用者トークンは PERMISSION_DENIED。要求と同じ順・同じ数を返す。
   - 値域は **`/department` の直下だけ**である。`/` を含む値（入れ子のパス）・空文字・前後空白を含む値は後段を引かずに `exists=false`。
     入れ子を数えると `engineering/backend` という「コード」が通り、利用者属性（1 値のコード）と一致しない。
   - 後段は `IIdentityAdminClient.FindGroupByPathAsync`（新設の読み取り。Keycloak `GET /group-by-path/{path}`。セグメントごとにエスケープ）。
     **返ったパスが要求と序数一致しなければ null**（照合の大小文字を IdP の格納層に委ねない）。404 は null、それ以外の失敗は例外（＝ gRPC status）。
2. **DataSourceService はポート `IDepartmentDomainDirectory` を gRPC 実装だけで満たす。** `Services:AuthorizationServiceGrpc` が未宣言の配備は
   `UnavailableDepartmentDomainDirectory`（常に「引けなかった」）に倒れる。**REST の兄弟実装は作らない** —— 利用者トークンを転送して
   `/authz/groups/lookup` を引く形へ戻ることになる（IADR-0401 決定 2 と逆向き）。形は IADR-0431 の `UnavailableOwnerRetentionDirectory` と同じ。
3. **検証は `DepartmentDomainValidation.ValidateAsync` 1 か所に置き、登録・全置換・部分更新の 3 端点が呼ぶ。** 更新は部門が保存済みの値から変わったときだけ照会する（(d)）。 値域の外は 400（`ValidationProblem` の `errors`）、
   引けなければ 502（`message`）。どちらも 1 項目も保存しない。照会するのは**保存される値そのもの**（前後空白を落とさない）。
4. **予約値 `unassigned`・空白・未指定は照会しない**（(c)）。
5. **SC-06 の画面は自由入力のまま**、補助文で「入れるなら部門グループのコード（大小文字を区別）。無いコードは保存されない」と伝える。
   400 の理由は既存の問題本文パーサがそのまま出す。502 は `ApiError.fromStatus` が 5xx の本文を捨てるため、登録・更新の 502 に限って
   画面の言葉で「確認できなかったため保存されていない可能性がある」を添える（BFF の 502 は後段の応答が空のときにも返るので言い切らない）。
6. ［2026-09-26 追記 / #1557 監査］**照会は 5 秒の締切つき**で呼ぶ（Keycloak が応答しないときも管理者の書き込みを固めず 502 にする。写像先の実在照会 `CheckUsernames` も同じ締切にした）。Keycloak の応答が `path` を持たないときは**推測せず失敗**にする（`ToIdentityGroup` の `/名前` 補完は表示用であり、値域の判定には使わない）＝ 502。**配備順は認可サービスが先（または同時）**であり、運用仕様書にも書いた。

## 結果

- SC-06 で打ち間違えた部門・realm に無い部門は保存されなくなる（従前は保存され、誰にも開かない文書が静かに作られていた）。
- AuthorizationService の gRPC 面の問いが 3 つになる（`docs/api/east-west-grpc.md` に追記）。realm の構成・資格情報・ロールは変えない。
- **稼働環境への適用**: DataSourceService と AuthorizationService の両方を本変更の版へ上げる必要がある。片方だけ上げると、
  DataSourceService が知らない rpc を呼んで UNIMPLEMENTED を受け、明示した部門の書き込みが 502 になる（保存はされない＝安全側）。
  realm（Keycloak）への作業は要らない。

## 残るもの

1. ABAC が読む利用者属性 `department` とグループ所属の一致（計画 ADR-0115 決定 3）は別 issue で扱う。本決定の値域（グループの**名前**）と
   利用者属性が一致する前提は、決定 3 の是正が入って初めて機械で保たれる（#1557 の監査指摘 3）。
2. ① フォルダ → 部門の写像表は器が未確定のため入れない（IADR-0468 決定 4 のまま）。
