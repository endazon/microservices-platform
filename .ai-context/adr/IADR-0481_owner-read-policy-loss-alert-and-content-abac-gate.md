---
title: IADR-0481 所有者の読み取りのポリシーの消失は、認可サービスの定期の検査のゲージと警報 2 本（無い／見ていない）で知らせ、内容の ABAC の門は文書サービスが認可サービスへ件数を問い、1 件以上を確かめたときだけ開いてラッチする
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-19, NFR-09, NFR-21, UC-05, SC-09, ADR-0121, ADR-0119, ADR-0036, ADR-0006, IADR-0165, IADR-0253, IADR-0379, IADR-0473, IADR-0476, IADR-0480]
author: claude
created: 2026-09-27
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 2・4・6・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-01・D-02
related_specs:
  - ../specs/20260927_issue-1665_owner-read-policy-guard-and-content-abac-gate.md
---

# IADR-0481: 所有者の読み取りのポリシーの消失の検知・通知と、内容の ABAC の有効化の門（#1665）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（#1665。計画 ADR-0121 決定 2 が「検知の手段は実装の IADR で決める」とした点）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-19 / NFR-09 / NFR-21 / UC-05 / SC-09
- 関連する計画 ADR: **ADR-0121 決定 2**（ポリシーの存在を門とする・消えたら検知して知らせる・検知と通知は有効化より前・削除は止めない・
  検知の手段は IADR で決める）、決定 4（配備の順序の 3 番目と 4 番目の前提）、決定 6、フォローアップ 2。ADR-0119 決定 3・4（内容の ABAC の本体）、
  ADR-0036 D-01・D-02、ADR-0006（アラートは Alertmanager）
- 関連する実装 ADR: [[IADR-0480]]（ポリシーの形・投入の手順）、[[IADR-0253]]（1 ポリシー = 1 分岐）、[[IADR-0379]]（proto の置き場と versioning）、
  [[IADR-0473]]（認可サービスの定期処理・計器・警報の前例）、[[IADR-0476]]（DocumentService の読み取りの判定）、[[IADR-0165]]（Grafana の暫定アラート）

## コンテキストと課題

所有者の読み取りのポリシー（IADR-0480）は SC-09 から自由に削除・無効化できる。消えると所有者は自分の文書を読めなくなるが、誤りとしては表に出ない。
内容の ABAC（#1615）はこのポリシーの存在を前提にする。計画は、(a) 有効化の門、(b) 消えたときの検知と通知、を求め、手段を IADR に委ねた。

決める必要があったのは次の 5 点である。

1. 「在る」の判定（形が近いが違うものを数えない）
2. 検知の手段（起動時・定期・変更時のいずれか）
3. 通知の経路
4. 門がポリシーの存在を知る手段
5. 門の開閉の規則（数えられないとき・開いた後に消えたとき）

## 検討した選択肢

| 論点 | 採用 | 退けた選択肢と理由 |
| --- | --- | --- |
| 1 | **有効・`read`（序数）・利用者の条件 0 キー・文書の条件がちょうど `owner` 1 キー（序数）・値の集合が `{${current_user}}`**。重複した束縛変数だけ同値と認める | 名前で判定 —— 管理者が変えられる（IADR-0480 と同じ理由）。評価器を実際に回して「本人の文書が通るか」で判定 —— 利用者の条件や他の値で**広い**ポリシーも通ってしまい、所有者の分岐より広いものを「在る」と数える |
| 2 | **認可サービスの常駐が起動時と 1 分ごと（構成可）に DB を数える。opt-in にしない** | 起動時だけ —— 稼働中の削除を拾えない。削除・無効化・更新の口で即時に検査 —— 口は 3 つあり、どこかで漏れる。複数レプリカの間の揃えも要る。周期の検査は口に依らず全ての経路（DB の直接の編集を含む）を拾う。遅れ（周期 ＋ 警報の `for`）は最大およそ 6 分 |
| 3 | **計器 → 警報（Alertmanager と Grafana の暫定の 2 系統。既存の 4 か所の定義）と Error ログ** | 通知サービス（SC-10）のアプリ内通知 —— 運用の警報の経路は ADR-0006・IADR-0165 で決まっており、「システム管理者」宛の宛先解決も無い（新設は計画外の機能追加）。削除を止める —— 決定 2 が退けた |
| 3' | **ゲージは直近の件数。数えられない・未検査のときは系列を出さない。警報は「無い（`< 1`）」と「見ていない（`absent()`）」の 2 本** | 失敗時に 0 を出す —— DB の障害を「ポリシーが無い」と偽る。失敗時に前の値を残す —— 消えたことを隠す。警報を 1 本（`(… < 1) or absent(…)`）—— 「無い」と「見ていない」で対応が違う（投入し直す／サービスを直す）。Grafana の式の検査（検査 6）も読めない形になる |
| 4 | **既存の `platform.authz.v1.AuthzScope` に rpc `GetOwnerReadPolicyStatus`（応答 `active_count`）を足す。受け手は呼ばれるたびに DB を数える** | 新しい gRPC サービス —— チャネル・登録・通信仕様の面が増える（呼び出し元の DocumentService は既に同じサービスを使っている）。REST の `GET /authz/policies` —— AdminOnly で、全ポリシーの中身を s2s へ出すことになる。常駐の直近の値を返す —— 門が古い値で開き得る |
| 5 | **`ContentAbac:Mode=On` かつ 1 件以上を確かめたときだけ開く。数えられないも閉じる。開くまで 1 分ごとに確かめ直す。1 度開いたらプロセスの寿命の間は閉じない（ラッチ）** | 動的に閉じ直す —— 消えた瞬間に内容の ABAC が外れ、機械クライアントへの読み取りの許可が**広がる**。ADR-0121 §結果 は「消すと所有者が読めなくなる」をトレードオフとして受け入れ、消えた後は通知で気づく設計である。起動時に 1 度だけ確かめる —— 投入が配備より後になると再起動まで開かない |

## 決定

1. 形の判定は `AuthorizationService.Domain.OwnerReadPolicyShape` の 1 か所に置き、常駐の検査と gRPC の受け手の両方がそれを使う（判定を 2 つにしない）。
2. 常駐 `OwnerReadPolicyCheckHostedService`（周期 `OwnerReadPolicyCheck:Interval`、既定 `00:01:00`、`hh:mm:ss`・下限 1 分、値域外は起動時に落とす）。
   計器 `authz.owner_read_policy.active`（ゲージ）と `authz.owner_read_policy.checks.total{outcome=present|absent|failed}`。無いあいだは Error ログ。
3. 警報 `OwnerReadPolicyMissing`（critical。Prometheus `authz_owner_read_policy_active < 1`、Grafana は生の値を `lt 1`、`noDataState: OK`）と
   `OwnerReadPolicyCheckSeriesAbsent`（warning。`absent(…)`）を 4 か所に置く。C# のゲージ名と式の一致は `scripts/scripts.repo.test.js` が見る。
4. proto に rpc と 2 つの message を**追加**する（非破壊。baseline を更新）。`active_count` の既定値 0 は「無い」と読まれ、取り違えは閉じる側へ倒れる。
5. DocumentService に `IContentAbacGate`（`IsOpen`・`State`）と常駐 `ContentAbacGateHostedService` を置く。状態は
   `disabled` / `not_evaluated` / `owner_read_policy_absent` / `owner_read_policy_unknown` / `open`。計器 `documents.content_abac.gate.open`（1/0、属性 state）、
   閉じている理由は Warning ログ。問い合わせの失敗（全 status・s2s トークン・5 秒の時間切れ・宛先の未構成）は「数えられない」、呼び出し元の取り消しは取り消し。
6. **本件では門を読む判定を置かない。** #1615 が `DocumentReadAccess` から `IsOpen` を読む。

## 結果

- 所有者の読み取りのポリシーが消える・無効になる・形が変わると、最大およそ 6 分で critical の警報が鳴り、認可サービスが Error を出す（ADR-0121 決定 4 の 3 番目）。
- 検査が止まる・失敗し続けると、「見ていない」の警報が鳴る（無音で見逃さない）。
- `ContentAbac:Mode=On` にしても、ポリシーを確かめるまで門は開かない。理由はログと計器に出る。
- 🔴 **残るもの**:
  - 内容の ABAC の本体（#1615）は未実装であり、門が開いても読み取りの判定は変わらない。
  - 誰が消したかは記録されない（ポリシーの API は監査の記録を持たない）。
  - 開いた後に消えても門は閉じない（決定 5）。所有者はその間自分の文書を読めない（ADR-0121 が受け入れたトレードオフ）。気づく手段は警報だけである。
  - 警報の受信先（メール・チャット）は運用環境ごとの設定であり、既定では誰にも届かない（Alertmanager の `default-null`・Grafana の画面を見に行く）。
    これは本件の前から全ての警報に共通する制約である（運用仕様書 §監視・アラート）。

## 関連

- Supersedes: なし
- Superseded by: なし

> ［2026-09-28 追記 / #1615］決定 6 の「本件では門を読む判定を置かない。#1615 が `DocumentReadAccess` から `IsOpen` を読む」と、結果の「残るもの」の
> 「内容の ABAC の本体（#1615）は未実装であり、門が開いても読み取りの判定は変わらない」は、**本体が入った**（[[IADR-0483]]）。門は `DocumentReadAccess` が
> 要求の中で最初に判定するときに 1 度だけ読み、開いていれば読み取りの判定を認可サービスの分岐だけで行う。門そのもの（構成・確認・ラッチ）は変えていない。

> ［2026-09-28 追記 / #1676］監査の非ブロッキングの指摘を試験と文書で閉じた（決定は変えていない）。
> (1) 門のラッチを並行の評価で固定した（テスト仕様 FR-05 T-68。ロック内の再確認を外す変異が生き残っていた）。
> (2) dev seed と運用仕様書の投入の本文をファイルから読んで `OwnerReadPolicyShape` に通し、「在る」と判定されることを固定した（T-67）。
> (3) 結果の「検査が止まる・失敗し続けると『見ていない』の警報が鳴る」までの遅れの目安（検査の周期 ＋ lookback 約 5 分 ＋ `for: 5m` ＝ 最大およそ 11 分）を運用仕様書に書いた。
> 作業仕様書 `.ai-context/specs/20260928_issue-1676_adr0121-audit-followups.md`。
