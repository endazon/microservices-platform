---
title: IADR-0542 ナレッジ健全性の報告（H-1）は 1 要求の全量置換のまま、受け口の受信上限を rpc 単位で 16 MiB（構成キー）に明示し、送信側の期限を 30 秒へ延ばす。分割・client streaming は採らない（#1907）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-17, FR-18, NFR-16, ADR-0029, ADR-0075, IADR-0379, IADR-0389, IADR-0408, IADR-0533]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements（NFR-16 east-west gRPC）
related_specs:
  - ../specs/20261015_1907_h1-grpc-receive-limit.md
---

# IADR-0542: H-1 の受信上限を rpc 単位で 16 MiB に明示し、期限を 30 秒へ延ばす（#1907）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（オーナーの依頼 #1907。案 1〜3 の選択を委ねられた）

## 起点・関連

- 起点 issue: #1907（#1895 の残課題）。親: #1895（#1906 で受け口を一括置換にした）。前例: #1899（D-1 の受信上限を構成キーで明示）。
- 関連する計画書 ID: FR-10・FR-17・FR-18（健全性の指標と閲覧）・NFR-16（east-west gRPC）。
- 関連する計画 ADR: ADR-0029（サービス間通信）・ADR-0075（east-west の輸送）。
- 関連する実装 ADR: [IADR-0379](./IADR-0379_east-west-grpc-preconditions.md)・[IADR-0408](./IADR-0408_east-west-grpc-knowledge-health-report.md)（H-1 の gRPC 面）・
  [IADR-0389](./IADR-0389_knowledge-health-producers-and-observation-dimension.md) 決定 5（受理されたときだけ数える）・
  [IADR-0533](./IADR-0533_east-west-rest-retirement.md)（輸送は gRPC だけ）。
- 関連する実装仕様書: `.ai-context/specs/20261015_1907_h1-grpc-receive-limit.md`（実測・母集合・変異試験）。
  #1895 の判断（期限 5 秒は延ばさない・分割は別 issue）は `.ai-context/specs/20261011_1895_knowledge-health-bulk-replace.md` §決定 3・5 にある。
- 採番: `origin/develop` の最大は IADR-0540。開いている PR #1936 が IADR-0541 を足す（push の直前に確かめた）。本件は **0542**。

## コンテキストと課題

- H-1（graph → dashboard の `KnowledgeHealthReport/Report`）は、指標ごとに観測の**全量**を 1 要求で送り、受け口は 1 トランザクションで置換する（#1895）。
- 受け口の受信上限は grpc-dotnet の既定 4 MB である。1 件の符号化は形により約 40〜94 バイトで、軽い形でも約 9.5 万件で `ResourceExhausted` になる。
  PoC の台帳は再索引で伸び続けている（切り替え後だけで 39,780 件、台帳は 4.5 万件超）。
- 受け入れ基準: 15 万件規模で H-1 が**期限と受信上限の両方**に収まること。
- 実測（仕様書 §実測）で、最も重い形（解決できないリンク）の 15 万件は **14,100,018 バイト**、受け口の処理は**負荷の高いホストで 2.5〜6.4 秒**だった。
  受信上限だけを直しても、期限 5 秒では毎周期の成否が揺れる。

## 検討した選択肢

1. **この rpc だけ受信上限を上げる（構成キー）＋ 期限を延ばす（採用）** —— 契約・送信側の組み立て・置換の原子性は変わらない。
   代償は 1 要求あたりのメモリ上限が上がること（下の §結果）。
2. 世代つきの分割書き込み —— 分割して送り、最後の要求で世代を切り替える。受信上限に依らず伸びるが、
   契約（世代番号・完了の印）・受け口の表（世代列と切り替え）・送信側の分割と失敗時の後始末の 3 つを同時に変える。
   途中で送信側が落ちた世代の掃除も要る。4 万件規模の今、短期で入れる危険に見合わない。
3. client streaming —— 契約の変更（rpc の形）。受け口は全件を受けてから 1 トランザクションで置換するので、
   メモリの最大は案 1 と変わらない（受け口で溜める）。原子性を保ったまま受け口を逐次書きにすると、案 2 と同じ世代の仕組みが要る。

## 決定

1. **受信上限は受け口のサービス（`KnowledgeHealthReportGrpcService`）にだけ 16 MiB を与える。**
   `GrpcServiceOptions<KnowledgeHealthReportGrpcService>.MaxReceiveMessageSize`。サーバ全体の既定（4 MB）は他の面のために据え置く。
   構成キー `Grpc:KnowledgeHealthReportMaxReceiveMessageSize`（env `Grpc__KnowledgeHealthReportMaxReceiveMessageSize`。単位はバイト）。
   未設定は既定 16 MiB。**0 以下・数値でない値は起動時に落とす**（#1899 の D-1 と同じ。黙って既定へ戻すと構成の誤りが「閲覧が古い」として再発する）。
   chart（values）には描画しない —— 既定で足りており、上げる必要が出たときに env で与える（#1899 と同じ）。
2. **送信側の期限を 5 秒から 30 秒へ延ばす**（`GrpcKnowledgeHealthReporter.SendTimeout`）。#1895 §決定 3「期限は延ばさない」を改める。
   - 15 万件の置換は負荷の低いときで約 2.5 秒、高いとき（ホストの負荷平均 30〜50）で最大 6.4 秒を実測した。5 秒は境界の内側に入らない。
   - 延ばす代償は「受け口が本当に詰まったとき、1 時間周期の定期処理がその間止まる」ことだけである。5 指標が全部詰まっても 2.5 分で、
     受け口は取り消しでロールバックする（旧い値が残り、半端にならない）。再送はしない（#1895 §決定 4 を維持）。
3. **分割（案 2）・client streaming（案 3）は採らない。** 契約（proto）は変えない。全量置換の原子性（1 トランザクション）を保つ。
4. **送信側は要求の符号化サイズを送る前に測り、受信上限の既定の 75%（12 MiB）以上なら Warning を出す**（`PayloadWarnBytes`）。
   受理されたら件数・バイト数・所要を Information で 1 行残す（1 時間に指標の数だけ）。失敗の Error にもバイト数を載せる。
   受け口の上限は構成で変えられるが、閾値は既定に対する比で固定する（サービスを跨ぐため定数を共有できない。既定値の一致は両側の試験で固定する）。

## 理由

- 案 1 は契約も原子性も変えずに、15 万件（最も重い形）を 16 MiB の 84% に収める。軽い形なら約 40 万件まで入る。台帳の伸び（数か月で 4 万 → 4.5 万件超）に対して、
  分割を設計し直す時間を稼ぐには十分である。
- 上げ幅を 16 MiB に留めるのは、受け口のメモリが限られるため（limits 512Mi、GC ヒープの上限は 60% ≈ 307 MiB。[IADR-0539](./IADR-0539_dotnet-gc-workstation-conserve-memory-default.md)）。
  15 万件の 1 要求で受け口が割り当てるのは約 74〜80 MiB（解析・写し・エンティティ・配列）で、上限いっぱいの約 18 万件でも 100 MiB 前後と見積もる。
  32 MiB 以上にすると、1 要求で GC ヒープの上限の半分を超え得る。
- 期限は、受信上限を上げて届くようになった要求が**期限で落ちる**のを避けるために要る。受信上限だけを直すと、症状が `ResourceExhausted` から
  `DeadlineExceeded` に変わるだけである（#1895 と同じ「毎周期ロールバック」）。
- 警告を送信側に置くのは、サイズを**送る前に**知っているのが送信側だけだからである。受け口は上限を超えた要求の中身を読まずに拒む。

## 結果

- PoC: H-1 の 5 指標は受理のたびに `ナレッジ健全性の報告が受理された。indicator=… count=… bytes=… elapsedMs=…` を 1 行残す。
  `bytes` が 12 MiB を超えると Warning が出る —— それが案 2（分割）へ進む合図である。
- 試験: `KnowledgeHealthReportReceiveLimitTests`（実 Kestrel の h2c。15 万件の陽性・上限超えの `ResourceExhausted`・構成の読み方）、
  `KnowledgeHealthSnapshotPostgresTests.十五万件の報告も受信上限と期限の両方に収まる`（実 PostgreSQL。サイズと所要）、
  `GrpcKnowledgeHealthReporterTests`（成功の 1 行・警告の閾値の上下・期限 30 秒・閾値と既定の一致）。
- 変異: 受け口の登録（`AddKnowledgeHealthReportReceiveLimit`）を外すと 15 万件の陽性が `ResourceExhausted` で赤になる（仕様書 §検証）。

## 残余リスク

- **伸び続ければいずれ 16 MiB に当たる。** 送信側の Warning（12 MiB）が出始めたら、上限をさらに上げる前に案 2（世代つき分割）を設計する。
  上限は env で一時的に上げられるが、受け口のメモリ（上の §理由）とのトレードオフである。
- 1 要求あたりのメモリの最大は従前の 4 倍になった。受け口の limits は 512Mi のまま。上限いっぱいの要求が来ても GC ヒープの上限の 1/3 程度である。
- 閲覧の 2 文の間の食い違い・同じ指標の同時報告（#1895 §残課題）は変わらない。

## 関連

- #1895・#1906（受け口の一括置換）・#1899（D-1 の受信上限の明示）
