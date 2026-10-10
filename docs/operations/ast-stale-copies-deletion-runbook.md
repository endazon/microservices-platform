---
title: 運用 Runbook — 内容の ABAC の前に、株取引の外部システムの古い写しを列挙して消す
type: runbook
status: fixed
author: claude
created: 2026-09-28
updated: 2026-10-11
---
<!-- trace:
ids: [FR-06, FR-05, FR-19, SC-05, NFR-09, UC-03]
adrs: [ADR-0122, ADR-0121, ADR-0057, ADR-0119]
iadrs: [IADR-0484, IADR-0483, IADR-0459, IADR-0529, AST:IADR-0436]
specs: [20261011_1891_ast-draft-not-duplicate, 20260928_issue-1667_ast-stale-copies-enumeration]
issues: [#1891, #1886, #1667, #457, #1679, planning#696, planning#784, AST#1301]
-->

# 運用 Runbook: 内容の ABAC の前に、株取引の外部システムの古い写しを列挙して消す

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 上位の節は運用仕様書の「所有者の読み取りのポリシー」と「内容の ABAC の有効化の門（文書サービス）」である。

## この手順を実行する条件（いつ走らせるか）

- 文書サービスの内容の ABAC を有効にする作業（`ContentAbac__Mode=On`）の **2 番目**（所有者の読み取りのポリシーの投入の後、有効化の前）に、**1 度だけ**行う。
- 基盤の DB などを破棄して作り直す切替が**まだなら**: 列挙 → 件数の確認 → 削除 → 報告書の入れ直し → 確認、を行う（下の「手順 A」）。
- 切替が**済んでいれば**: 列挙が 0 件であることを確かめるだけにする（下の「手順 B」）。
- 🔴 **どちらの場合も、列挙を済ませるまで `On` にしない。** 門は古い写しの有無を確かめない。

### 消す対象（何を「古い写し」と呼ぶか）

株取引の外部システム（`ai-stock-trading`）の KB の書き手が作った文書のうち、その**現在のサービスアカウント**
（`service-account-ai-stock-trading-kb-writer`）が所有していないもの。所有者（`owner`）が `system` のものと、所有者が無いものの両方である。
内容の ABAC の下ではこれらは外部システムから見えなくなり、外部システムが同じ報告書を作り直して重複する。

- **確定報告書の写し**: 外部システムが入れ直せる（作り直される）。
- **収集記事の写し**: 入れ直す経路が無い。**消すと検索（RAG）から失われる。これは受け入れ済みである。** 消す前に件数と期間を確かめる。
- 個人資料・取り込みの経路の文書（所有者が `system` のものを含む）・他の project の文書・人が作った文書は**対象にならない**（列挙の口が除く）。
- **属性 `reportState=draft` の文書は、外部システムの承認待ちの報告書の写し（ドラフト）である。古い写しの対象にも、重複の解消の対象にもならない**
  （列挙の口が `not-ast-shape` に数えて除く。表題は `報告書ドラフト {種別} {期間キー}`）。ドラフトは確定や入れ直しのときに外部システムが自分で消す。
  確定版と同じ種別・期間キーを持つが、列挙の口は確定報告書の写しとして数えないので、`currentAccountReports` にも現れない。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | `platform-admin` ロールを持つ主体のアクセストークン。対象クラスタへの `kubectl`（`microservices-platform` の `port-forward`）。報告書の入れ直しには、外部システムの所有者の権限（外部システム側の操作） |
| 必要なツール | `kubectl`・`curl`・`jq` |
| 所要時間の目安 | 列挙と確認 15 分。削除は件数による（数千件規模になりうる）。入れ直しは報告書の件数による |
| 事前に確かめること | 所有者の読み取りのポリシーが投入済み（運用仕様書）。**内容の ABAC がまだ `Off`**（`documents_content_abac_gate_open` が 0 で、状態が `disabled`） |

## 列挙の口

文書サービスの **`GET /documents/ast-stale-copies`**（管理者だけ・読み取り専用。何も書き換えない）。クラスタの内側から叩く。

```bash
kubectl -n microservices-platform port-forward svc/document-service 18092:8080 &
export TOKEN="<platform-admin を持つ主体のアクセストークン>"
curl -sS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:18092/documents/ast-stale-copies > stale-copies.json
jq '{scanned, targets, excluded, currentAccountReports}' stale-copies.json
```

応答の読み方:

| 項目 | 意味 |
| --- | --- |
| `scanned` | 見た文書の件数（台帳の全件）。`targets.total` と `excluded` の合計に等しい |
| `targets.total` / `targets.reports.count` / `targets.articles.count` | 消す対象の件数（報告書・記事） |
| `targets.reports.createdFrom` / `createdTo` | 対象の報告書の作成の期間 |
| `targets.articles.createdFrom` / `createdTo` / `publishedFrom` / `publishedTo` | 対象の記事の作成の期間と、記事の公開日時の期間（**失う記事の期間**） |
| `excluded` | 除いた件数を理由ごとに（0 件も出る）。`private-note`（個人資料）・`not-created-via-post`（取り込みの経路など、作成の口で作られていない）・`other-project`（別の project）・`not-ast-shape`（報告書・記事の属性の形でない。承認待ちの報告書の写し〔`reportState=draft`〕もここに数える）・`owned-by-current-account`（現在のサービスアカウントが所有。消さない）・`other-owner`（他の主体が所有） |
| `currentAccountReports` | 現在のサービスアカウントが所有する報告書の写しの件数と、同じ種別・期間キーの写しが 2 件以上ある組（`duplicates`） |
| `items[]` | 対象の各件（`id`・`title`・`category`〔`report` / `article`〕・`owner`〔`missing` / `system`〕・`hasProject`・`status`・作成と更新の時刻・`kind`・`periodKey`・`publishedAt`）。作成の古い順 |

## 手順 A: 切替がまだのとき（消す）

1. **列挙する。** 上のとおり `stale-copies.json` を取る。`scanned` が `targets.total` と `excluded` の合計に等しいことを確かめる。
2. **件数と期間を確かめ、記録する**（下の「記録」）。とくに次を読む。
   - `targets.articles.count` と `publishedFrom` 〜 `publishedTo`（消すと失われる記事の件数と期間）。
   - `items[]` の表題と属性を眺め、人が作った文書が紛れていないか（報告書は表題が `確定報告書 {種別} {期間キー}`、記事は外部の出典の見出し）。
     紛れていれば、その `id` を削除の一覧から外し、記録に残す。
   - 件数が想定と大きく違う（例: 数万件）ときは止めて、起点の issue で相談する。
3. **消す。** 経路は**文書管理の画面の管理者の削除**、またはその画面が中継する先の文書サービスの削除の口 `DELETE /documents/{id}`
   （同じ口で、伝播の範囲〔本文・図表・索引・グラフ・Wiki〕も同じ）。件数が多いときは口を使う:

   ```bash
   jq -r '.items[].id' stale-copies.json > stale-ids.txt   # 2 で外した id はここから除く
   while read -r id; do
     code=$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE -H "Authorization: Bearer $TOKEN" \
       "http://127.0.0.1:18092/documents/$id")
     echo "$id $code"
   done < stale-ids.txt | tee delete-result.txt
   awk '{print $2}' delete-result.txt | sort | uniq -c   # 204 の件数が対象の件数に等しいこと
   ```

   - 204 以外（404 は既に無い、5xx は失敗）は `delete-result.txt` に残る。5xx の `id` だけを選んで再実行する。
   - トークンの期限が切れたら（401）、取り直して続きから再実行する（削除済みは 404 になるだけで害は無い）。
4. **もう 1 度列挙し、`targets.total` が 0 であることを確かめる。**
5. **確定報告書を 1 度だけ入れ直す。** 外部システムの所有者に、報告書の KB の入れ直し（`POST /reports/knowledge-base/reingest` に `{"all": true}`）を
   **1 回だけ**実行してもらう。外部システムは写しが見つからない報告書を本文つきで作り直す（所有者は現在のサービスアカウントになる）。
   記事は作り直されない（上の「消す対象」）。
6. **重複が出ていないことを確かめる。** もう 1 度列挙し、次を満たすことを確かめる。
   - `targets.total` が 0。
   - `currentAccountReports.duplicates` が空。
   - `currentAccountReports.count` が、外部システムの確定済みの報告書の件数（入れ直しの応答の件数）と矛盾しない。

## 手順 B: 切替が済んでいるとき（0 件の確認）

1. 上のとおり列挙し、**`targets.total` が 0** であることを確かめる（切替で古い写しは破棄されている）。
2. 0 でなければ、切替の後に古い形で作られた写しがある。手順 A の 2 から続ける。
3. 結果を記録する（0 件の確認も記録する）。

## 確認（この手順が成功したと言える条件）

- 最後の列挙で `targets.total` が 0、`currentAccountReports.duplicates` が空。
- 記録（下）に、消す前の件数と期間・消した件数・最後の列挙の結果が残っている。
- ここまでを済ませて初めて、内容の ABAC の有効化（運用仕様書の門の節）へ進む。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 列挙が 403 | トークンの主体が `platform-admin` を持たない（運用者だけ・外部システムの書き手は通らない） | 管理者の主体で取り直す |
| 列挙が 401 | トークンが無い・期限切れ・別の realm | 取り直す |
| `targets.total` が想定より大きい | 人が作った文書が同じ属性の組を持つ、または古い写しが本当に多い | 手順 A の 2 で `items[]` を確かめ、紛れた `id` を除く。判断できなければ止めて相談する |
| 削除が 5xx | オブジェクトストレージ・メッセージの一時的な障害（削除は本文を先に消し、失敗すれば行は残る） | 障害の解消を待って、その `id` だけ再実行する |
| 入れ直しの後に `duplicates` が出る | 入れ直しを 2 回以上実行した、または消し漏れた古い写しを入れ直しが写しとして数えた | 重複した組の新しい方を、文書管理の画面の管理者の削除で消す（外部システムの報告書 1 件につき写し 1 件にする）。**消す前に、組の 2 件がどちらも `reportState` を持たない（確定版の写しである）ことを確かめる**。`reportState=draft` の文書は承認待ちの写しであり、重複の解消の対象外である（列挙の口は数えないので、組に出たら口の不具合として止めて相談する） |
| 入れ直しで本文を入れられない写しが残る | 本文を入れる口の所有者の判定の別件（別の作業で是正中） | 本手順では扱わない。起点の issue に記録する |

## 記録

起点の issue にコメントで残す。

- 実施日・実施者・対象のクラスタ・切替の前か後か。
- 消す前の列挙の `scanned`・`targets`（件数と期間）・`excluded`（理由ごと）。除外した `id` があればその理由。
- 削除の結果（204 の件数、それ以外の件数と `id`）。
- 入れ直しの応答の件数（手順 A のみ）と、最後の列挙の `targets.total`・`currentAccountReports`。

## 限界（この手順で担保できないこと）

- **列挙は属性の形で見分ける。** 表題を変えた、project を持たない報告書は見つけられない（`not-ast-shape` に数えられる）。
  表題を変えられるのは基盤の管理者だけである。
- **版を持たない文書は作成の経路が分からないので対象にしない**（`not-created-via-post` に数えられる）。
- **承認待ちの報告書の写し（`reportState=draft`）は、所有者によらず対象にしない**（`not-ast-shape` に数えられる）。所有者が `system` や無しのドラフトが残っても、
  この手順では消さない。後始末は外部システムの確定・入れ直しに委ねる。
- **所有者を遡って付けることはしない。** 列挙の口も削除の口も所有者を書き換えない。
- **削除は手作業である。** 口は列挙だけを行い、自動では消さない。門も古い写しの有無を確かめないので、
  **この手順を済ませたかどうかは記録でしか分からない**。有効化の前に記録を確かめる。
