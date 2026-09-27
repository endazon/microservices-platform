---
title: IADR-0483 DocumentService の読み取りの内容の ABAC は、門が開いたときだけ認可サービスの read の分岐ただ 1 つで判定する。所有者・利用者共有のコード判定はその枝に残さず、門は要求の中で最初に読んだ値に固定する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-06, FR-19, NFR-09, UC-03, SC-03, SC-05, ADR-0121, ADR-0119, ADR-0122, ADR-0036, ADR-0034, ADR-0056, ADR-0086, ADR-0088, ADR-0109, IADR-0253, IADR-0416, IADR-0447, IADR-0476, IADR-0480, IADR-0481]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 2・4・5・6・フォローアップ 4
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・4・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md 決定 3・4
related_specs:
  - ../specs/20260928_issue-1615_content-abac-document-reads.md
---

# IADR-0483: DocumentService の読み取りの内容の ABAC（#1615）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-28
- 決定者: claude（#1615。計画 ADR-0121 決定 4 の 4 番目・決定 5 と、ADR-0119 決定 3 の実装側の形）

## 起点・関連

- 関連する計画書 ID: NFR-09、FR-05、FR-06 / UC-03 / SC-03 / SC-05、FR-19
- 関連する計画 ADR: **ADR-0121** 決定 4（4 番目: ポリシーの存在を確かめて有効にする）・**決定 5**（所有者・利用者共有のコード判定を認可サービスへ寄せる。
  fail-closed を受け入れる）・決定 2（門）、**ADR-0119** 決定 3（読み取りの全ての口で `read` 規則を自ら判定し、判定は認可サービスへ問う。主体の 3 種。
  読めない文書は一覧から除き個別は 404）・フォローアップ 2（AST の入れ直しが写しを見つけられること）、ADR-0122 決定 3・4（段 2 は本件の外。
  列挙の口ができるまで有効化へ進まない）、ADR-0036 D-01・D-08・D-14、ADR-0034 決定 9、ADR-0056、ADR-0088
- 関連する実装 ADR: [[IADR-0476]]（判定点 `DocumentReadAccess`・決定 7 の差し込み口。本 IADR が開いた枝を足す）、[[IADR-0481]]（門。決定 6 の「#1615 が `IsOpen` を読む」）、
  [[IADR-0480]]（所有者の read ポリシー・seed の応答の期待値のファイル）、[[IADR-0253]]、[[IADR-0447]]、[[IADR-0416]]

## コンテキストと課題

IADR-0476 の判定点は、組織文書を認証済みの全主体に返し、個人資料は所有者・利用者共有をコードで、グループ共有だけを認可サービスで判定していた。
計画は、門（IADR-0481）を通したときだけ内容の ABAC を有効にし、同時に判定を認可サービスへ寄せると定めた。決めることは次の 5 点だった。

1. 門が閉じている間の挙動（変えないことをどう担保するか）
2. 門が開いた枝の判定の形（何をコードに残すか）
3. 機械の主体を認可サービスへ何という名前で名指すか
4. 一覧の問い合わせを件数に比例させない形
5. 門の読み方（要求の途中で開いたとき）

## 検討した選択肢

| 論点 | 採用 | 退けた選択肢と理由 |
| --- | --- | --- |
| 1 | **判定点の中で 2 つの枝に分け、閉じた枝は IADR-0476 のコードをそのまま残す**（問い合わせの条件・回数も同じ） | 1 本の判定にして「門が閉じていれば組織文書は常に真」を足す —— 個人資料の判定まで認可サービスへ寄り、閉じている間の挙動（問い合わせの回数・認可サービスの不調で自分の資料が読めなくなる）が変わる |
| 2 | **分岐 1 つ（`AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant`）だけ。コードに残すのは「機械は個人資料を読まない」（ADR-0034 決定 9）と「主体名が決まらなければ読めない」だけ** | 所有者を先にコードで通す —— ADR-0121 決定 5 が退けた（判定器が 2 つになり、所有者の read ポリシーを消したときの挙動が BFF・検索と食い違う） |
| 3 | **`DocumentManageScope.MachineSubject`（`service-account-<clientId>`）**。gRPC の `user` なしは呼び出し元の資格情報から同じ関数で。中継された `service-account-…` はその名前 | `azp` の生値 —— 作成時に `owner` へ入れる名前（同じ関数）と一致せず、AST の写しが所有者の分岐に一致しない |
| 4 | **主体名で memo した 1 回の問い合わせ**（IADR-0476 決定 4 の memo。1 要求の主体は 1 人） | 分岐を DB の条件へ訳す —— 属性は jsonb の値変換で SQL へ訳せない（既存の一覧もメモリで絞る）。認可サービスへ文書ごとに問う —— 件数に比例する |
| 5 | **要求の中で最初に判定するときに 1 度だけ `IsOpen` を読み、以後はその値に固定する** | 判定のたびに読む —— 一覧の途中で開くと、1 つの応答に旧判定と新判定が混ざる。門は開く向きにしか動かない（ラッチ）ので、固定しても次の要求から開く（作業仕様書 §規則 11 の表） |

## 決定

1. `DocumentReadAccess` は `IContentAbacGate` を受け取り、`ContentAbacEnabled`（最初に読んだ `IsOpen`）で枝を選ぶ。**閉じた枝は IADR-0476 のまま**。
2. **開いた枝**は、機械の主体が個人資料を読もうとすれば偽、主体名（`DocumentReadPrincipal.AbacSubject`）が無ければ偽、分岐が引けなければ偽、
   それ以外は共有先を重ねた像に対して分岐のどれかが一致し、かつ個人資料なら裁量の分岐であること。管理者ロールは特別扱いしない（D-08）。
3. `DocumentReadPrincipal` に `AbacSubject` を足す（人は利用者名、機械は `MachineSubject`、gRPC の `user` なしは呼び出し元の `MachineSubject`）。
   閉じた枝は読まない。
4. `GET /documents/page` は、門が開いたときだけ、切り出しの**前**に `DocumentReadAccess` で絞る（共有先は台帳の全件分を 1 クエリ）。閉じている間は通さない。
5. 書き込みの「読めるが書けない 403 ／ 読めない 404」は同じ判定点を使うので、門が開けば内容の ABAC で答える（コードの変更なし）。
6. 認可サービス・BFF・契約・配備の値・門そのもの（IADR-0481）は変えない。proto は注記だけを直した（`user_attributes` は引き続き読まない。ADR-0088）。

## 結果

- 良い影響:
  - 門を `On` にして所有者の read ポリシーが確かめられると、機械の呼び出し元（AST・east-west）も機密・制限の組織文書を属性なしでは読めなくなる（ADR-0119 実測 10・11 が閉じる）。
  - 判定器は認可サービスの分岐ひとつになる（BFF・検索・グラフと同じ述語・同じ分岐）。
  - AST の KB の書き手は、seed の応答（期待値のファイルにサービスアカウントの主体を足し、実物と突き合わせた）の下で自分の写しを見つけられる。
    実 Keycloak 24 の `users?username=…&exact=true` はサービスアカウントの利用者も返す（`UsersResource.searchForUser` が個別条件の枝で `includeServiceAccounts=true`）。
- 悪い影響 / トレードオフ:
  - 🔴 門が開いた後は、認可サービスが落ちると読み取りはすべて空・404 になる（自分の個人資料を含む。ADR-0121 決定 5 が受け入れた fail-closed）。
  - 🔴 門が開いた後に所有者の read ポリシーが消えると、所有者は自分の文書を読めない（ADR-0121 §結果。警報で気づく）。
  - `owner` の比較は分岐の述語（`AttributeFilterMatch`。単一値は大小文字を区別しない）になる。閉じた枝の `IsOwnedBy` は序数比較である。IdP の利用者名は小文字で揃うので実害は無い見込み。
  - 文書の保存された属性に `shared_with` が入っていると、共有台帳が空のときその値が判定の像に残る（BFF の像と同じ。書けるのは管理者と所有者の機械だけ）。本件では変えない。
  - BFF の gRPC 経路は、呼び出し元が機械のとき利用者文脈を運ばないので、門が開くと BFF 自身のアカウントで判定される（狭まる向き。REST 経路は機械自身の資格情報を中継する）。
    `/bff/documents` を機械で呼ぶ呼び出し元は現在無い。
- フォローアップ:
  1. 🔴 **門を `On` にするのは、ADR-0122 決定 3・4 の段 2（#1667 の列挙の口と古い写しの削除／0 件の確認）の後**。門は所有者の read ポリシーしか確かめない（ADR-0121 決定 2）。
  2. #1611 の段 2（DocumentService の MCP ツールの実行口）を本判定点に乗せる。
- 再検討の条件: 一覧の件数が増えて台帳をメモリで絞る形が遅くなったとき（分岐を DB の条件へ訳す形を改めて検討する）。

## 関連

- Supersedes: なし（[[IADR-0476]] の決定 3・4 は、門が開いたときに限り本 IADR の決定 2 が置き換える。閉じている間は IADR-0476 のまま）
- Superseded by: なし
