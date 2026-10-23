---
title: IADR-0596 Wiki とカタログの受け手は反映済みの版より古い更新を反映しない。Wiki は同期メタデータと別の版の印（削除も記録し、行を消しても残す）を持ち、保存の突き合わせで負けたら保存済みの最新の状態へ Wiki.js を合わせ直す。カタログは反映した NormalizedAt・アーカイブの時刻・削除の事実を持ち、アーカイブや削除より前に作られた再正規化で戻さない
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-13, FR-01, FR-19, UC-07, UC-04, ADR-0011, ADR-0027, ADR-0057, ADR-0141, ADR-0061, IADR-0595, IADR-0021, IADR-0541, IADR-0554, IADR-0563, IADR-0529, IADR-0296]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-01・FR-06・FR-13・FR-19）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（少なくとも 1 回・再試行／デッドレター）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1・2）
  - planning:projects/microservices-platform/07_adr/ADR-0141（決定 1 (a)）
related_specs:
  - ../specs/20261023_2102_wiki-catalog-ordering.md
---

# IADR-0596: Wiki とカタログの受け手の順序（#2102）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2102。親 #2047 の指摘 M-4〔Wiki・Catalog 分〕）

## 起点・関連

- 起点 issue: #2102（親 #2047）。境界: #2150（`UpdatedAt` を進めない変更の順序）・#2042（発行側の Outbox）・#2100（Wiki.js の書き込みの門をレプリカ間へ）・#2103（Document の楽観ロック）。
- 関連する計画書 ID: FR-13・FR-06（FR-01・FR-19）・UC-07・UC-04。計画 ADR: ADR-0027・ADR-0057・ADR-0141 決定 1 (a)・ADR-0011・ADR-0061。
- 引き継ぐ実装判断: [IADR-0595](./IADR-0595_ingestion-index-version-and-write-then-delete.md)（取り込みの版の印。同じ考え方を写す）、IADR-0021（Wiki.js の正準パス・同期メタデータ）、
  IADR-0541・IADR-0554（恒久的な失敗は ack）、IADR-0563（Wiki.js の書き込みの門）、IADR-0529（露出の門）、IADR-0296（削除は台帳の参照を辿る）。
- 対照: GraphService `GraphDocument.TryApply`（`if (updatedAt < UpdatedAt) return false;`）。
- 作業仕様書: [20261023_2102_wiki-catalog-ordering](../specs/20261023_2102_wiki-catalog-ordering.md)（母集合・受け入れ基準・変異・限界）。

## コンテキストと課題

- WikiService の `DocumentSyncConsumer` は `WikiPage.Sync` で題名・属性・タグ・状態を無条件に代入し、Wiki.js へも無条件に push していた。wiki-service は HPA で 2 レプリカ以上になり、
  同じプロセスの中でも更新（`DocumentUpdated`）と削除（`DocumentDeleted`）は別のリスナーで並ぶ。古い版が後から届くと新しい版を上書きし、機密区分を厳しくした後の古い版で Wiki.js のページが公開へ戻り得る。
  撤去（露出の門）・削除は同期メタデータの行を消すので、その後の古い版は行を作り直して書き戻す。
- DocumentService の `DocumentNormalizedConsumer` は `Document.ApplyNormalized` で題名・属性・状態を無条件に上書きしていた。古い再正規化が新しい再正規化を上書きし、
  アーカイブより前に作られた再正規化が遅れて届くとアーカイブを戻す。変換は文書 ID を決定的に採番するので、削除より前に作られた再正規化は削除した文書を台帳へ作り直す。

## 検討した選択肢

1. **受け手ごとに版の印を持ち、古い版を弾く。Wiki は保存の突き合わせ ＋ 負けたら合わせ直す（採用）**。
2. **Wiki の印を `WikiPage` の列に持つ**（issue の推奨の `SourceUpdatedAt`）。撤去・削除で行が消えると判定の材料を失い、その後の古い版を弾けない（IADR-0595 が点の `updated_at` だけで判定する案を採らなかったのと同じ理由）。採らない。
3. **Wiki の受け口全体を文書ごとのレプリカ間の排他（Postgres advisory lock 等）で直列にする**。並行を根から断てるが、Wiki.js の呼び出し（最大 15 秒）の間 DB の接続とロックを握り、
   試験の器（InMemory）で検証できない。Wiki.js の書き込みの門をレプリカ間へ広げる #2100 と同じ場所の変更であり、そちらで扱う。採らない（§結果の残余）。
4. **カタログで `UpdatedAt` と比べる**（利用者の編集より前に作られた再正規化を弾く）。取り込みは属性の正本であり、新しい本文の再正規化を利用者の編集 1 つで捨てることになる。採らない（§結果の未決）。
5. **判定の部品を共有層（`Knowledge.Contracts` か共有のライブラリ）に置く**。下の決定 4。採らない。

## 決定

**決定 1（Wiki の版の印）**: WikiService は文書ごとに**版の印**（表 `SyncMarks`。主キー `DocumentId`・`AppliedAt`・`Deleted`・`Revision`）を持つ。同期メタデータ（`Pages`）と別に置き、撤去・削除で `Pages` の行を消しても残す。
`AppliedAt` は反映した版（`DocumentUpdated.UpdatedAt`、削除なら `DocumentDeleted.DeletedAt`）を保存の精度（マイクロ秒）に丸めた値である。
`DocumentUpdated` は最初に印を読み、**`UpdatedAt < AppliedAt`、または削除済みで `UpdatedAt <= AppliedAt` なら何もせずに ack する**（Wiki.js にもメタデータにも書かない。本文も読まない）。等しい版と印の無い文書は反映する。
書き込みの 3 分岐（アーカイブ・撤去・同期）は、メタデータの変更と同じ 1 回の保存で印を進める（メタデータの行が無いアーカイブ・撤去でも進める）。印を進めない分岐: 対象外の状態・個人資料（何も書かない）・本文の欠落と値の長さの拒否（IADR-0541 / IADR-0554 の ack。状態を変えない）。

**決定 2（Wiki の削除）**: `DocumentDeleted` は印に削除を記録する（`Deleted = true`・`AppliedAt = DeletedAt`）。**`DeletedAt < AppliedAt` の削除は適用しない** —— 変換は文書 ID を決定的に採番するので、
削除の後に同じ ID で作り直された文書がある。その版（削除より新しい）を反映した後に遅れて届いた古い削除で、作り直したページを消さない。削除の再配送（等しい時刻）は適用する。

**決定 3（Wiki の並行: 保存の突き合わせと合わせ直し）**: 受け口は「印を読む → Wiki.js へ書く → メタデータと印を保存する」の順で動き、保存は印の `Revision`（並行トークン）で突き合わせる。
印を読んだ後に他の受け口が印を保存していれば、後から保存する側は必ず負ける（`DbUpdateConcurrencyException`。同じ文書の印の同時作成は主キー `PK_SyncMarks` の 23505）。
負けた側は印を読み直し、**自分が古くなっていたら**（決定 1・2 の判定で）、保存済みの状態 —— メタデータの行が active なら本文を読んで push・archived ならアーカイブ・行が無ければ削除 —— へ Wiki.js を合わせ直して ack する。
自分の Wiki.js への書き込みが相手の書き込みより後に着いた場合でも、古い版の内容を Wiki.js に残さない。古くなっていなければ（相手が古い・同じ版の並行）投げて再試行へ委ねる（次の試行は保存済みの印を読んで判定し直す）。
合わせ直しで本文が読めなければ（NoSuchKey）ページを非公開にする（deny-closed）。

**決定 4（共有層に置かない）**: 判定は 1 行の比較で、受け手ごとに判定キー（`UpdatedAt` / `NormalizedAt`）・保存の精度（Qdrant のミリ秒 / Postgres のマイクロ秒）・印の置き場所（Qdrant のコレクション / 各サービスの DB）が違う。
共通の部品にすると受け手の実装詳細（精度・置き場所）を一つの型へ寄せることになり、契約（`Knowledge.Contracts`）に振る舞いを持ち込む。**各受け手が自分の Domain に置く**（WikiService `DocumentSyncOrdering`・DocumentService `Document.IsNormalizationStale`）。
共通するのは規則の文言（「厳密に古い版は反映しない・等しい版は反映する・印は射影と別に持ち撤去や削除の後も残す」）であり、それは本 IADR と IADR-0595 が持つ。

**決定 5（カタログの順序の門）**: `Document` は**反映した再正規化の版**（`LastNormalizedAt`。`DocumentNormalized.NormalizedAt` をマイクロ秒に丸めた値）を持つ。
`DocumentNormalizedConsumer` は**本文を読む前に**台帳を引き、`NormalizedAt < LastNormalizedAt` なら本文を読まず・台帳を書かず・`DocumentUpdated` も出さずに ack する。等しい版（再配送）と版を持たない文書（本欄の追加以前・取り込み経路以外）は反映する。
本文を読む前に置くのは、本文の URI が文書 ID 固定（`<id>/document.md`）で再変換のたびに上書きされるため —— 古い版のまま本文を読むと新しい本文の指紋を古い題名・属性に付ける。

**決定 6（カタログのアーカイブ）**: `Document` は**アーカイブした時刻**（`ArchivedAt`）を持つ。再正規化は ADR-0141 決定 1 (a) のとおりアーカイブを normalized へ戻すが、
**`NormalizedAt <= ArchivedAt` の再正規化（アーカイブより前に作られた版）は内容を反映し、状態はアーカイブのまま**にする（正しい順に届いていれば「再正規化 → アーカイブ」で、内容は再正規化の値・状態はアーカイブになる）。
戻したら `ArchivedAt` を消す。本欄の追加以前のアーカイブ（`ArchivedAt` が無い）は `UpdatedAt`（アーカイブ以後に動いた時刻であり、アーカイブの時刻以上）で読む —— 戻しにくい側に倒れ、マイグレーションで値を埋めない。

**決定 7（カタログの削除）**: 組織文書の削除（`Delete/Endpoint`）は**削除の事実**（表 `DocumentDeletionMarks`。文書 ID と時刻だけ）を同じ保存で残し、下流へ伝える `DocumentDeleted.DeletedAt` にも同じ時刻を使う。
台帳に行が無く `NormalizedAt <= DeletedAt` の再正規化は台帳へ作り直さずに ack する。削除より後の再正規化（データソースの再同期）は従来どおり作る。
個人資料の削除は `DocumentNormalized` の経路に乗らない（文書 ID は変換の決定的な ID ではない）ので改めない。削除の事実と Wiki の印は題名・本文・属性の値を持たず、本文の復元に使えない（ADR-0057 決定 2 が許す「削除の事実の記録」と同じ性質）。

## 結果

- 良い: 到着順の逆転・再配送・デッドレターからの再投入で、Wiki.js のページ・同期メタデータ・カタログの台帳が古い版で上書きされず、機密区分を厳しくした後に緩い側へ戻らない。
  撤去・アーカイブ・削除の後の古い版で復活しない。Wiki の 2 者の並行（2 レプリカ・更新と削除のリスナー）で、古い版の push が後に着いても保存済みの最新の状態へ戻る。
  GraphService・IngestionService（IADR-0595）と同じ規則が 4 つの受け手に揃う。
- 悪い（受容）:
  - **`UpdatedAt` を進めない変更の順序は判定できない**（露出トグル・共有の付与と取り消し・タグの改名・再発行）。Wiki には等しい版として届き、後着が勝つ（従前と同じ）。#2150。
  - 版は発行側の時計である（Wiki は DocumentService、カタログの `NormalizedAt` は ConversionService）。カタログのアーカイブ・削除の時刻（DocumentService の時計）と `NormalizedAt` を比べる箇所は、時計の差が再同期の間隔より大きいと誤る（GraphService・IADR-0595 と同じ前提）。
  - **Wiki の 3 者以上の並行**: 合わせ直しの push 自体は突き合わせない。合わせ直しの最中にさらに新しい版が push と保存を終えると、合わせ直しの push（1 つ前の版）が後に着く窓が残る。合わせ直しの Wiki.js 呼び出しが一時的に失敗したときも、
    再試行は印を読んで「古い」と判定して ack するので、古い版の push が次の更新まで残り得る。いずれも Wiki.js への書き込みをレプリカ間で直列にする #2100 で閉じる（**#2100 の解消まで、wiki-service の複数レプリカは 2 者の並行までを本決定で受け持つ**）。
  - **カタログの並行**: 2 つの再正規化が同時に台帳を書く窓（check-then-write）は残る。`Version` の並行トークン化（#2103）で閉じる。
  - **カタログで利用者の編集と古い再正規化が交差する**: 再正規化が作られた後・届く前の利用者の属性の編集は、届いた再正規化で上書きされる（従前と同じ。選択肢 4）。取り込みが属性の正本である前提の下での欠落であり、扱いは未決（報告に残す）。
  - wiki_svc に表が 1 つ、document_svc に表が 1 つと列が 2 つ増える（起動時のマイグレーション）。配備時点で Wiki の印を持つ文書は無く、最初の更新までは従前の挙動（印が無ければ反映する）。
- 境界: 発行側の原子性（#2042）は扱わない。Outbox が入っても到着順の逆転・再配送は残るので、本決定は Outbox と独立に要る。
