---
title: 作業仕様書 — 個人資料を Wiki.js の同期対象から外す（ADR-0046 D-01）
type: spec
status: done
related_ids: [FR-19, FR-13, UC-07, UC-11, ADR-0011, ADR-0036, ADR-0046, ADR-0054]
author: claude
created: 2026-08-22
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 作業仕様書: 個人資料を Wiki.js の同期対象から外す（`ADR-0046` D-01）

## 走査基準

| 対象 | ref |
| --- | --- |
| 実装 | `origin/develop` = `f6b458f3` |
| 計画 | `origin/main` = `fbd4dda` |

🔴 **隣接クローンは fetch のうえ `git ls-tree` / `git show` で ref を明示して読む**（作業ツリーは黙って遅れる。本日 22 コミット遅れで空振りした実例あり）。

## 1. これは「実装に閉じた判断」か

**部分的に委任されている。決めてよい範囲と、決めてはいけない範囲を分ける。**

| 論点 | 判定 | 根拠 |
| --- | --- | --- |
| 個人資料を Wiki.js へ同期するか | **計画が決定済み。実装は従うだけ** | `ADR-0046` D-01「`private-note` は WikiService の push 対象に含めない」 |
| 判定軸（属性キーと値） | **計画が決定済み** | `ADR-0054` 決定 1・2（`doc_scope` = `private-note` / `organization`） |
| 除外を実装のどこへ置くか | **実装の裁量** | 計画に指定なし |
| 🔴 **組織文書 → 個人資料へ転換したとき、既存の Wiki.js ページをどうするか** | **どちらでもない。計画に記述が無い** | §5 で扱う。**実装が勝手に決めない** |

## 2. 決定（`ADR-0046` D-01 の原文）

> `private-note` は WikiService の push 対象に含めない。**Wiki.js 上に個人資料のページは作られない。**
> 組織文書の同期は従来どおり行う（`IADR-0021` の方式を変えない）。

## 3. 🔴 実装上の罠 —— 集合帰属で書く。否定で書かない

**除外は `doc_scope == "private-note"` で書く。`doc_scope != "organization"` で書いてはならない。**

| 書き方 | 結果 |
| --- | --- |
| `doc_scope == "private-note"` なら除外 | ✅ 正しい |
| `doc_scope != "organization"` なら除外 | 🔴 **既存文書が全滅する** |

**理由（実測）**: `doc_scope` は 2026-08-22 新設で**実データ 0 件**であり、**既存 2,368 件へ遡及付与しない**方針（`ADR-0054` §結果）。属性を持たない文書はすべて「`organization` でない」に該当し、**組織文書の同期が一斉に止まる**。

計画側の根拠でもある —— `ADR-0036` D-04 が「評価の性質は変えない —— **集合帰属**・deny-by-default」と定め、`ADR-0054` 決定 3 が**片側だけに値を置く設計を明示的に却下**している。

### この 2 つは「個人資料を同期しない」という点では区別できない

**個人資料が実データに 1 件も存在しない現在、どちらの実装も「個人資料を同期しない」。** したがって
**否定形テスト（テスト 1・4）だけでは両者を分けられない。**

> 🔴 **［2026-08-22 実測による訂正］本節は当初「分けられるのは陽性対照テスト**だけ**である」と書いたが、
> **変異試験の結果それは誤りだった。** `!= "organization"` の変異を入れると **45 件中 10 件が落ちる**。
> **既存テスト 7 件が巻き添えで落ちる**ためで、それらのフィクスチャが `doc_scope` を持たないからである
> （`Consumer_SetsWikiJsPrivacy_FromConfidentiality` 等が `new() { ["confidentiality"] = ... }` を
> 明示指定しており、`doc_scope` が入らない）。
>
> **つまり検出そのものは既存テストでも起きる。** 新設した陽性対照 `Consumer_SyncsDocument_WhenDocScopeMissing`
> の値は**検出力ではなく診断力**である —— 既存テストは「push が 1 件のはずが 0 件」という
> **機密区分の話をしているテストが落ちる**形で失敗し、**なぜ落ちたのかを言わない**。
> 新設テストだけが「`doc_scope` の欠落を個人資料と誤判定してはならない（実データ 2,368 件がこの形）」
> という理由つきで落ちる。
>
> **「証明力のない変異」と「検出漏れ」を混同しないため、実測値をそのまま残す。**
> **過大評価していたのは新設テストの必要性であって、罠そのものは実在する**（変異は現に赤くなる）。

## 4. 実装

`WikiService/Composable/Steps/DocumentSyncConsumer.cs` の `Consume` に除外を足す。

**置く位置**: `status` フィルタの後、メタデータ upsert の前。**`archived` の分岐より後に置く** —— 個人資料であってもアーカイブ伝播（Wiki.js ページの非公開化）は**通す**。ページが存在しない場合も `ArchivePageAsync` は冪等であり、**deny-closed の向き**である。

判定は `Knowledge.Contracts` ではなく WikiService 内の定数で持つ（他サービスが要るようになった時点で共有へ上げる。先回りしない）。

## 5. 🔴 計画に記述が無い論点（実装で決めない）

**組織文書として同期済みの文書が、あとから `doc_scope = private-note` へ変わったら、Wiki.js 上の既存ページはどうなるか。**

- 本仕様書の実装では、**その文書は以後スキップされるだけで、既に作られた Wiki.js ページは残る。**
- `ADR-0046` D-01 は「**ページは作られない**」と書いており、「**既にあるページを消す**」とは書いていない。
- `doc_scope` が文書の生涯で変わり得るのかも、計画は述べていない（`ADR-0054` は既定と値域を定めるのみ）。

**この振る舞いを実装で決めない。** 現状の振る舞いをテストで固定し（§6 のテスト 6）、**計画へ問う**（§8）。

**なぜ勝手に「消す」側へ倒さないか**: 消す実装は安全側に見えるが、**`doc_scope` が変わり得ないなら起こり得ないケースへの防御的実装**であり、CLAUDE.md が禁じている。**変わり得るかどうかを知っているのは計画側である。**

★［2026-09-05 追記 / #449］**計画は答えた。`doc_scope` は変わり得ない。**
`ADR-0058`（planning#472 の裁定・2026-08-23）が決定 1 で「作成時に確定し、以後変更できない」、決定 2 で「更新経路は変更要求を拒否する」、決定 3 で「SC-05 の属性編集フォームで編集不可」と定めた。実装も着地している（`DocumentAttributes.ValidateDocScopeUnchanged` / `IADR-0278`。値の変更・既存値の削除・後からの新規付与の 3 つを 1 本の一致判定で閉じる）。

**上の判断（消す側へ倒さない）は結果として正しかった。** 本節が「変わり得るかどうかを知っているのは計画側である」として保留したのは、まさにこの裁定を待つ形になっていた。**テスト 6 は消さず、上流の門が回帰したときの二層目として意味を読み替える**（`DocumentSyncConsumer` と `DocumentSyncConsumerTests` のコメント、および `docs/tests/FR-19_private-note-wikijs-exclusion.md` のケース 6 を同 PR で書き直した）。**本文の他の記述は当時のまま残す。**

## 6. 受け入れ基準（テスト）

🔴 **否定形テストには陽性対照を対で置く。** 「常に早期 return する実装」「`doc_scope` を一切見ない実装」は否定形だけを通す。

| # | テスト | 種別 |
| --- | --- | --- |
| 1 | `doc_scope = "private-note"` → **Wiki.js へ push されない**かつ**メタデータが作られない** | 否定形 |
| 2 | `doc_scope = "organization"` → **従来どおり push とメタデータ upsert が起きる** | **陽性対照（1 と対）** |
| 3 | 🔴 **`doc_scope` を持たない**（属性欠落）→ **従来どおり同期される** | **陽性対照。§3 の罠を検出する唯一のテスト** |
| 4 | `doc_scope = "private-note"` の再配信で状態が変わらない | 冪等（否定形） |
| 5 | `status = "archived"` かつ `private-note` → **アーカイブ伝播は通る** | 境界 |
| 6 | 既に同期済みのページを持つ文書が `private-note` になった → **既存ページは残り、以後更新されない** | **現状の固定**（§5。仕様ではなく観測） |

### 変異試験（必須・完了条件）

**実測済み（2026-08-22）。** いずれも「変異が当たったこと」を先に確認している
（`git diff` が当該箇所のみ・ビルド `EXIT=0`）。

| 変異 | 予想 | **実測** |
| --- | --- | --- |
| 除外条件を外す（`IsPrivateNote(...)` → `false`） | 1 が落ちる | ✅ **3 件が落ちた**（1・4・6＝個人資料をスキップする系すべて）。陽性対照 2・3 は緑のまま＝**除外が広がっていない**ことも同時に示す |
| `== "private-note"` → `!= "organization"` | 3 だけが落ちる | ⚠️ **10 件が落ちた**（新設 3 を含む）。**予想が外れた** —— 既存 7 件のフィクスチャが `doc_scope` を持たないため巻き添えで落ちる |

**2 つ目の予想が外れたことを、値の側を直さず記録する**（§3 の訂正ブロック）。**検出は既存テストでも起きる。
新設テストの値は診断力である。**

追加で確かめたこと: 変異 2 を保ったまま**共有ヘルパ `Event()` の既定へ `doc_scope = "organization"` を
足しても、落ちるのは 9 件**だった（1 件減っただけ）。多くのテストが `attrs` を明示指定して既定を
上書きするためで、**「既定を直せば既存テストが緑になる」という当初の想定も成り立たなかった。**

## 7. 母集合（規則 6 —— 引いた結果と除外の理由）

**誤りの側（`private-note` / `IsPrivate` / Wiki.js への push 経路）で `origin/develop` を走査した。**

| 面 | 扱い |
| --- | --- |
| `WikiService/Composable/Steps/DocumentSyncConsumer.cs` | **変更対象**（push とメタデータの単一経路） |
| `WikiService/Composable/Adapters/WikiJsGraphQlClient.cs` | 変更しない（`IsPrivate` は機密区分由来の粗粒度設定であり、本件とは別の軸） |
| `WikiService/Foundation/Ports/IWikiJsClient.cs` | 変更しない（契約は変わらない） |
| `GraphService`（`GraphTypeGateArchitectureTests.cs` の `IsPrivate`） | **除外**。グラフ側の型ゲートであり Wiki.js 同期経路ではない |
| `src/ai-stock-trading`（submodule） | **除外**。別プロジェクトの名前空間。`private-note` を持たないことを走査で確認済み |

## 8. フォローアップ（環流）

1. 🔴 **§5 の論点を計画へ問う** —— `doc_scope` は文書の生涯で変わり得るか。変わり得るなら、組織文書 → 個人資料への転換時に Wiki.js 上の既存ページを非公開化するのは誰の責務か。

## 9. 射程外

- `doc_scope` の付与そのもの（システム投入経路の既定は `DataSourceService`。#447 / #516）
- `doc_scope` の必須検証（`DocumentAttributes` への追加。planning#465 の結果待ち）
- 閲覧側の個人スコープ（`ADR-0046` D-06 の 3 部品。#989）
- 既存 2,368 件への遡及付与（#457 で破棄）
