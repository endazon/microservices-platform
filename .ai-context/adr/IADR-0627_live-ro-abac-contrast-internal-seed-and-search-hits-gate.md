---
title: IADR-0627 live-ro の ABAC の対照は internal の seed 文書を 1 件足して両側で固定し、検索のヒットは E2E_SEARCH_HITS の門で判定する
type: impl-adr
status: Accepted
related_ids: [NFR, FR-03, FR-05, IADR-0255, IADR-0284, IADR-0570, IADR-0624, IADR-0625, IADR-0626]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md（clearance の階段・deny-by-default）
related_specs:
  - ../specs/20261025_2028_live-ro-knowledge-abac-contrast.md
---

# IADR-0627: live-ro の ABAC の対照と検索のヒットの門（#2028）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2028。親 #2013 の E-41）

## 起点・関連

- 起点 issue: #2028（親 #2013）。関連する計画書 ID: FR-03（検索）、FR-05（ABAC）。
- 関連する実装判断: [IADR-0255](./IADR-0255_edge-smoke-step-loss-gate-and-unobservable-search.md)（200 ＋空と「見えない」は区別できない）・
  [IADR-0284](./IADR-0284_search-observability-seed.md)（検索用 seed）・[IADR-0570](./IADR-0570_e2e-accounts-placeholder-credentials.md)（試験アカウント）・
  [IADR-0626](./IADR-0626_live-ro-auth-errors-on-revocable-fresh-session.md)（live-ro の役割の使い分け）。
- 作業仕様書: [20261025_2028_live-ro-knowledge-abac-contrast](../specs/20261025_2028_live-ro-knowledge-abac-contrast.md)。

## コンテキストと課題

E-41 の受け入れ基準は「`e2e-noaccess` で seed 文書が見えない」である。しかし `e2e-noaccess` は clearance=public を持ち（IADR-0570）、
ABAC の階段は clearance=public に public の文書を読ませる。検索用 seed は public の 1 件だけだったので、**noaccess にも seed 文書が見える**。
また検索のヒットは索引（取り込み・埋め込み）が在る環境でしか判定できず、索引が空の環境で「出ない」を見ると、故障と権限の区別が付かない（IADR-0255）。

## 検討した選択肢

| 論点 | 選択肢 | 判断 |
| --- | --- | --- |
| 対照の作り方 | A. noaccess の clearance を外す | 採らない（noaccess は「clearance=public の最小権限」として定義され、他の層の前提がずれる。オーケストレーターの方針） |
| | **B. confidentiality=internal の seed 文書を 1 件足す** | **採る**（user に見え noaccess に見えない。public の 1 件が noaccess 側の陽性対照になる） |
| | C. e2e 専用の seed ファイルを別に持つ | 採らない（投入器と単一情報源が 2 つになる。B で足りる） |
| 門 | A. 門なしで常にヒットを判定する | 採らない（索引の無い常設 PoC で常に赤） |
| | **B. `E2E_SEARCH_HITS=1` のときだけ判定し、無ければ理由つき skip（到達の test は常に流す）** | **採る**（`verify-oidc-edge-flow.sh` の `SEARCH_HITS=1` と同じ意味） |

## 決定

1. 検索用 seed（`deploy/local/search-seed/documents.json`）に **confidentiality=internal・department=sales の 2 件目**を足す。先頭は public のまま、
   2 件目も合言葉を含む。不変条件（`scripts.repo.test.js`）は「先頭は public・internal の対照が 1 件以上・public と internal 以外を置かない」とする。
2. live-ro の ABAC の対照は**両側**で見る: user には 2 件とも一覧に出て、internal を文書詳細で開ける。noaccess には public だけが出て
   （陽性対照）、internal は一覧に出ず、ID を直接開くと「文書が見つかりませんでした」・BFF の `GET /bff/documents/{id}` は 404。
   候補（`POST /bff/attribute-values`）は、internal にだけ在る部門（sales）が user に出て noaccess に出ない（public の部門は両方に出る）。
3. noaccess 側の陰性の判定の前に、**user の別 context で 2 件とも索引に入ったことを確かめる**。取り込みは非同期なので、ヒットは画面を
   開き直しながら `toPass`（上限 120 秒）で待つ。
4. 門の変数は **`E2E_SEARCH_HITS`**（値域は `1` か未設定・空。`E2E_LIVE=1` 無しは読み込みで例外）。ヒット・候補・文書 ID を要る test は
   `test.skip(!searchHits, 理由)`。到達（200 ＋描画）の test は門なしで流す。
5. `e2e-live.yml` は readiness の後に seed 投入器（ABAC・検索）を明示的に再実行し、live の段に `E2E_SEARCH_HITS=1` を渡す（使い捨てスタックは
   `LOCALEMBED=1`・`SEARCHSEED=1` で索引が在る）。paths に両 seed の置き場を足す。

## 理由

- 見える側との対比でしか「見えない」は言えない（IADR-0255）。同じ合言葉に当たる 2 件のうち、区分だけが違う 1 件が片側にだけ出ることが、最も小さい対照である。
- 門を変数にすることで、索引の無い環境の skip が「理由つきの前提未整備」として数えられ、全 skip は `check-e2e-skips.js` が赤にする。

## 影響

- `SEARCHSEED=1` で起こしたスタック（使い捨て）には internal の文書が 1 件増える。`verify-oidc-edge-flow.sh` の検索の門は合言葉を含む文書を
  `find`/`some` で探すので結果が変わらない。poc-operator（属性なし）の負の対照はどちらの文書も見えないので崩れない。
- 網羅表の M-030（「noaccess では候補が空」）は「internal にだけ在る値が出ない」へ読み替える（public の文書の部門は noaccess にも出る）。
  M-048 の「接続先未設定」の文言は #1200 で撤去済みなので、「ページツリーか空の表示が出て、失敗の表示は出ない」と読む（#2013 へ注記する）。
- 常設 PoC は `SEARCHSEED` を立てない（使い捨て専用）。PoC で live-ro を流すときは `E2E_SEARCH_HITS` を立てない限り、ヒットの test は skip する。

## 残余リスク

- department=sales は realm の部門グループ（開発用 realm の engineering / sales / hr）に依存する。realm から sales が消えると 2 件目の投入が
  400 で落ちる（投入器が赤になる）。
