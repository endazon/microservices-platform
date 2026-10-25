---
title: IADR-0631 live-rw の管理リソースは何も新しく許さない形で作り、削除の口が無いデータソースと MCP クライアントは無効化までを後始末とし、sweep は無効化済みを残骸に数えない
type: impl-adr
status: Accepted
related_ids: [NFR, SC-02, SC-05, SC-06, SC-09, SC-12, FR-05, FR-16, IADR-0627, IADR-0629, IADR-0630]
author: claude
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_2032_live-rw-admin-resources.md
---

# IADR-0631: live-rw の管理リソースの作り方と、無効化だけのリソースの後始末（#2032）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2032 / E-52。条件はオーケストレーターの指示 2026-10-25: 書き込みは使い捨てのスタックの中だけ・e2e-revocable で作らない・
  sweep の dry-run で残骸 0 と見ていない視点 0）

## 起点・関連

- 起点 issue: #2032（親 #2013 の網羅表 M-076・M-082・M-092・M-104）。関連: [IADR-0629](./IADR-0629_live-cleanup-and-disposable-only-sweep.md)（後始末と sweep）・
  [IADR-0630](./IADR-0630_live-rw-disposable-stack-gate-and-run-id.md)（live-rw の門・runId・発行値）・[IADR-0627](./IADR-0627_live-ro-abac-contrast-internal-seed-and-search-hits-gate.md)（検索のヒットの門）。
- 作業仕様書: [20261025_2032_live-rw-admin-resources](../specs/20261025_2032_live-rw-admin-resources.md)。

## コンテキストと課題

網羅表は M-082（データソース）と M-104（MCP クライアント）を「登録 → 無効化 → 削除」とするが、API に削除が無い。
データソースの `DELETE /bff/datasources/{id}` は実体を消さず status を `disabled` にし（`DataSource.Disable()`）、MCP クライアントの BFF は
無効化・有効化だけを持つ。どちらも行が残るので、sweep（IADR-0629）は試験の後に毎回それを「残骸」と数える。
また ABAC ポリシーを作ると、作った間は同じ実行の他の試験（M-063 の「共有の前は見えない」など）の認可が変わりうる。

## 決定

1. **削除の口が無い種類は無効化までを後始末とする**。sweep の種類に `settled`（データソースは `status=disabled`、MCP クライアントは
   `enabled=false`）を持たせ、`e2e-` の一致のうち `settled` の行は「後始末済み（削除の口が無い）」として列挙し、**残骸に数えない**。
   `settled` でない行は残骸であり、`--delete` は無効化（データソースは DELETE、MCP クライアントは `POST …/disable`。何度送っても 200）を送る。
   試験の後始末（`cleanup.ts`）も同じ要求を積む。網羅表の M-082・M-104 の「削除」は API に無いので流さず、網羅表の訂正を #2013 に残す。
2. **ポリシーは何も新しく許さない形で作る**: 利用者 `clearance=restricted` × 文書 `confidentiality=restricted` の read。seed の
   「restricted 取扱者は全区分を読める」に含まれるので、作った間も認可の結果が変わらない。
3. **M-076 は SC-02（検索）ではなく SC-03（`/docs/<id>`）で見る**（公開の後に開けて見出しがタイトル、削除の後に BFF 404 で「文書が見つかりません
   でした」）。SC-05 で登録した文書は本文の所在（`MarkdownUri`）を持たず、取り込みは本文の所在が無ければ索引に何も書かない
   （`IngestionService` の `DocumentUpdatedConsumer`「MarkdownUri is null, skipping ingestion」。メタデータ点は本文の所在があって本文が空の
   ときだけ）。PR #2238 の初回の e2e-live で実測した（120 秒待っても検索に出ず、試験の 60 秒で打ち切られた）。網羅表の「SC-02 で見える」は
   #2013 で訂正する。当初の本決定（タイトルの runId で検索に出る）は誤りだった。
4. **既存のポリシーと辞書に差分が無いことを各 test の前後で見る**（ポリシー・属性辞書・タグ辞書・関係の型の辞書の GET。`e2e-` の行と
   件数の項目〔`usageCount` など〕は除き、ID で並べて比べる）。
5. **無人の MCP クライアントは属性を所属部門（e2e-admin 自身の `department=engineering`）だけで登録し、client secret は記録しない**。
   無人は ABAC 属性が 1 つ以上要る（05_screens §SC-12。画面の検証が送信の前に止める。PR #2238 の初回の e2e-live で実測: 属性なしでは要求が
   送られなかった）。`clearance` が無いので何も読めず、`clearance`・タグではないので登録者の部分集合の判定（認可サービス）も呼ばれない。secret は後始末を積んだ後に `E2E_ISSUED_MCP_CLIENT_SECRET` として記録し（IADR-0630 決定 6。成果物の走査が
   探す）、その spec のファイルは trace・スクリーンショット・動画を切る。

## 理由

- 決定 1: 使い捨てのスタックではジョブの終わりにすべてが消えるので、無効化済みの行が残っても害は無い。数えたままにすると sweep の残骸が
  毎回 2 件になり、本物の消し損ねが埋もれる。「無効化されていない e2e- の行」だけを残骸にすれば、後始末の失敗は引き続き見える。
- 決定 2: rw は workers 1 で順に流れるが、同じ実行の中でポリシーが一時的に効く。許す範囲を広げない条件なら、順序に依らず他の試験を揺らさない。

6. **ポリシーの検証の結果は「矛盾なし」に固定する**。検証（`AbacValidation.ValidatePolicy`）は要求と属性辞書だけを見て、既存のポリシーとは
   比べない。条件は定義済みのキーと許可値で、文書の条件のキーは 1 つ、動的束縛は無いので、辞書が変わらない限り `valid=true`・`errors` 空に決まる
   （辞書が変わらないことは前後の突き合わせが見る）。
7. **sweep は後始末済み（無効化済み）の件数を残骸とは別に出す**（実行のたびに増えるので、使い捨てのスタックの外で増えていれば気付ける）。

## 影響

- MCP クライアントは Keycloak 側にも無効のクライアントが残る（BFF に消す口が無い）。常設の PoC では流さないので積み上がらない。
- 削除の口が足されたら、その種類の `settled` を外して `remove` を削除へ替える（本 IADR を改める）。
