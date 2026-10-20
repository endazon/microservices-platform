---
title: IADR-0563 Wiki.js へ同時に書き込む数を設定値 Wiki:MaxParallelWrites（既定 1・1〜4）にし、同期と撤去の両方の受け口がプロセスに 1 つの門を通り、両リスナーの数も同じ値へ明示する。既に非公開のページへアーカイブの update を送らない
type: impl-adr
status: Accepted
related_ids: [FR-13, UC-07, ADR-0011, ADR-0027, IADR-0021, IADR-0478, IADR-0557]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧・同期）
related_specs:
  - ../specs/20261020_1971_wiki-sync-write-gate.md
---

# IADR-0563: Wiki.js への書き込みの門（#1971）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（オーナーの依頼 #1971）

## 起点・関連

- 起点 issue: #1971（PoC・2026-10-16。一括操作の後、`pages.create` が 1 件約 8.4 秒・毎分約 7 件で、`wiki-service.DocumentUpdated` に約 4,800 件が溜まった）。
- 関連する計画書 ID: FR-13・UC-07。関連する計画 ADR: ADR-0011（Wiki.js）・ADR-0027（Wolverine・再試行・DLQ）。いずれも変えない。
- 関連する実装 ADR: [IADR-0557](./IADR-0557_wiki-sync-skip-unchanged-writes.md)（同じ内容の update を省く。並列度は上げない。本件はその残余リスク (d) を閉じる）・
  [IADR-0478](./IADR-0478_consumer-outbound-call-timeouts-under-wolverine-execution-timeout.md)（受け口の外への呼び出しの期限と起動時検査）・
  [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（GraphQL push）。
- 関連する実装仕様書: `.ai-context/specs/20261020_1971_wiki-sync-write-gate.md`（原因の読み取り・母集合・受け入れ基準）。
- 採番: push の直前に `origin/develop` と開いている PR の IADR の最大を確かめ、その +1 を採った（PR 本文に記録）。

## コンテキストと課題

- 律速は Wiki.js の中の処理である（IADR-0557 の実測: CPU 0.5 で create 9.0 秒・update 5.1 秒。wiki-service 側は 1 件 0.02 秒）。
- wiki-service の受け取り方を Wolverine 6.24.4 の端点で読んだ: `wiki-service.DocumentUpdated` と `wiki-service.DocumentDeleted` は
  **どちらも Mode=Inline・ListenerCount=1**（`MaxDegreeOfParallelism` は既定の 5 だが Inline では使われない）。1 キューは 1 通ずつ進む
  （PoC の毎分 7 件 ≒ 60 ÷ 8.4 と整合）。ただし**この並びは既定に頼っているだけで、コードに書かれていない。**
- **2 つのキューは別のリスナーなので、同期 1 本と撤去 1 本は並ぶ。** Wiki.js 2.5 の create と delete はどちらも木の再構築（`rebuild-tree`）を
  トランザクションを張らずに `pageTree` の truncate → insert で走らせる。並ぶと主キーで衝突して書き込みが失敗する
  （IADR-0557 の実測: 作成どうしで並列 2 で 2/8・並列 4 で 6/8）。一括アーカイブ・一括削除と再発行が重なると起き得る（IADR-0557 の残余リスク (d)）。
- 一括アーカイブ（#1931）の後の再発行では、既に非公開のページへ `ArchivePageAsync` が update を送り直す。この update は今の表題・本文・タグを
  送り返し `isPublished=false` にするだけで、既に false なら Wiki.js の状態は変わらない。それでも描画の子プロセスと `pageHistory` の版が増える。

## 検討した選択肢

1. **何もしない（既定の Inline・1 本に頼る）** —— 1 キューの中は今も直列だが、同期と撤去の並びは残る。既定が変わる（Wolverine の版・Buffered への切り替え）と黙って並ぶ。採らない。
2. **リスナーごとの並列度だけを明示する（`MaximumParallelMessages` / `ListenerCount`）** —— 1 キューの中は固定できるが、2 つのキューの間の並びは止められない。単独では採らない。
3. **Wiki.js のクライアント（`IWikiJsClient`）の中で直列にする** —— 待ちが Wiki.js 1 回の期限（既定 15 秒）を食う。CPU 0.5 で 9 秒の作成の後ろに並ぶと、
   自分の作成が期限を超えて時間切れになる。採らない。
4. **受け口がプロセスに 1 つの門を通ってから Wiki.js を呼び、門の待ちと呼び出しを別の期限で測る（採用）** —— 同期と撤去の両方の並びを 1 か所で抑える。
   待ちの期限は Wiki.js 1 回と同じ長さにする（前の書き込みはその期限で必ず切られるので、上限 1 の待ちはそれを超えない）。
5. **並列度の既定を 2 以上にする** —— IADR-0557 の実測で作成が壊れ、毎分も 1 割強しか増えない。採らない。
6. **バッチ化** —— Wiki.js 2.5 の GraphQL に複数ページの作成・更新の口は無い。木の再構築は作成 1 回ごとに走る。採れない。

## 決定

1. **Wiki.js へ同時に書き込む数の上限を設定値 `Wiki:MaxParallelWrites`（環境変数 `Wiki__MaxParallelWrites`）にする。既定は 1。許す範囲は 1〜4。**
   範囲の外・整数でない値は起動で止める（黙って丸めない）。
   - **既定 1 の根拠**: Wiki.js 2.5 の `rebuild-tree` は並ぶと `pageTree` の主キーで衝突する（IADR-0557 の実測。並列 2 で作成の 25%・並列 4 で 75% が失敗）。
     並列にしても律速の Wiki.js の CPU は増えず、毎分は 12.1 → 13.9 → 15.4 にしかならない。
   - **上限 4 の根拠**: 実測した範囲（並列 1・2・4）に閉じる。2 以上は、Wiki.js が並びに耐える版（木の再構築を直した版など）へ変わったと確かめたときだけ使う。
2. **同期（`DocumentSyncConsumer` の 3 口: アーカイブ・露出の門の撤去・upsert）と撤去（`DocumentDeletedConsumer`）は、プロセスに 1 つの門（`WikiJsWriteGate`）を通ってから Wiki.js を呼ぶ。**
   両リスナー（`wiki-service.DocumentUpdated` / `wiki-service.DocumentDeleted`）の数も同じ値へ明示する（`ListenerCount`）。
   Inline のリスナーで並びを決めるのはリスナーの数であり、門より多く受け取って門で待つ形にもしない。
3. **`ArchivePageAsync` は、Wiki.js のページが既に非公開（`isPublished=false`）なら update を送らない。** 往復は `singleByPath` の 1 回のまま（取る列に `isPublished` を足す）。
   `isPublished` が読めない（欠落・null）・true のときは従来どおり unpublish の update を送る（安全側）。`isPrivate` は IADR-0557 決定 2 と同じ理由で見ない。
4. **門の待ちには Wiki.js 1 回と同じ期限（`Wiki:WikiJsTimeoutSeconds`・既定 15 秒）を付ける。** 超えたら呼び出し先 `wiki-js-gate` の時間切れ
   （`ConsumerTimeoutException`・ログ `call to wiki-js-gate did not complete`）として投げ、再試行へ委ねる（IADR-0478 と同じ作法）。
   起動時の最悪の所要時間の検査に門の待ちを足す: 同期は本文 ＋ 門 ＋ Wiki.js（既定 20＋15＋15＝50 秒）、撤去は門 ＋ Wiki.js（30 秒）。どちらも既定の実行期限 60 秒に収まる。
5. 枠は呼び出しの成否・時間切れに関わらず必ず返す（`finally`）。

## 理由

- 同期と撤去の両方を 1 つの門で抑えることで、プロセスの中では Wiki.js の木の再構築が並ばない。1 キューの中の直列は既定と同じなので、**毎分の件数は変わらない**（速くも遅くもならない）。
- 既に非公開のページへのアーカイブの update は Wiki.js の状態を変えない。送らなくても同期の結果は同じで、冪等な同期の意味論は保たれる。
- 並列度は設定値として残るので、Wiki.js の版が変わって並びに耐えると確かめたときに、コードを変えずに上げられる。

## 結果

- 一括アーカイブの後の再発行で、アーカイブ済みの文書は 1 件 `singleByPath` 1 回（IADR-0557 の実測で約 0.014 秒）になる（従来は update 1 回で CPU 0.5 なら約 5 秒）。
- 同期と撤去が重なる一括操作でも、プロセスの中の Wiki.js への書き込みは 1 本ずつになる。撤去が同期の書き込みの後ろで待つ分、撤去 1 件の時間は延び得る（最大で Wiki.js 1 回の期限）。
- **作成は速くならない**（Wiki.js 側の子プロセス 2 本と木の再構築が残る。IADR-0557 の残余リスク (c) のまま）。

## 残余リスク

- (a) **門はプロセスの中だけで効く。** wiki-service のレプリカを 2 以上にすると、レプリカの数だけ並ぶ（helm の `services.wiki.replicas` は 1）。上げるときは本 IADR を改める。
- (b) Wiki.js 1 回の期限で呼び出しを切っても、Wiki.js の中の処理（木の再構築）は続く。切った直後に門を通った次の書き込みとは、Wiki.js の中で並び得る。
  失敗した側は一時障害として再試行される（従来どおり）。
- (c) アーカイブを省いたページは、Wiki.js の画面で本文・表題を直されていても、アーカイブのたびには描き直されない（状態は非公開のまま。IADR-0557 の残余リスク (a) と同じ型）。
- (d) 待ちは `SemaphoreSlim` で、順番（先着順）を保証しない。上限 1・リスナー各 1 本のとき、待つのは高々 1 本なので順番は問題にならない。上限を上げると待ちの順番は崩れ得る。

## 試験

- `WikiJsWriteGateTests`（上限の読み方・範囲の外の拒否・同期と撤去の重なりの上限〔上限 2 の対照つき〕・門の時間切れと枠の返却）・
  `WikiJsWriteGateWiringTests`（本番の配線: 既定の門 1・両リスナー Inline 1 本・構成した値が門と両リスナーへ入る）・
  `WikiSyncTimeoutTests`（門の待ちを最悪の所要時間に数える）・`WikiJsGraphQlClientTests`（既に非公開なら往復 1 回・それ以外は従来どおり update）。
  門を素通りにする・枠を返さない・省略を外す／常に省略する・リスナーの数を外す・検査から門を外す、の各変異で赤になることを確かめた（作業仕様書）。

## 関連

- #1971・#1979（IADR-0557）・#1931（一括アーカイブ）・#1640（IADR-0478）
