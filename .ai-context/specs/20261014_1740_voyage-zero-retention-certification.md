---
title: 作業仕様書 — Voyage AI のゼロ保持認定（学習不使用・ゼロ保持・所在）を運用仕様書へ記録し、未認定の記述を追随させる（#1740）
type: spec
status: done
related_ids: [NFR-17, FR-02, FR-03, ADR-0016, IADR-0025, IADR-0504, IADR-0085]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
issue: "#1740"
---

# 作業仕様書 — Voyage AI のゼロ保持認定の記録（#1740）

> 本仕様書は編集の着手前に作成した（着手 2026-10-14）。文書だけの変更で、コード・配備物・既定の挙動は変えない。
> 基点は MSP `origin/develop` `966c8d94`。計画は project-planning の隣接クローン（読み取り専用）を読んだ。

## 起点

- 起点 issue: **#1740**（#336 から分離。完了条件「認定の可否と根拠〔契約条項・日付〕が operations.md に記録されている」）。
- 根拠: #1740 のコメント 3 件（いずれも 2026-10-14）。
  1. オーナーの認定（ゼロ保持・学習不使用・所在の 3 点）。
  2. Voyage AI Terms of Service（Last Updated: May 27, 2026）との照合の表、opt-out（2026-10-14 20:22 JST ごろ）、opt-out 前の送信 960 回（すべて 200）。
  3. オーナー判断: 所在の保証が無い点を受容（「今の状態を許容する」）。opt-out 前の 960 回は学習に使われ得るものとして受容。
- 要求: **NFR-17**（データ越境統制）。計画 ADR-0016（Voyage を既定・ティアB。ティアB 要件の確認）・08_data-egress-policy（ティアB の定義: ZDR・学習不使用・レジデンシー／越境保証）。
- 実装 ADR: IADR-0025（フォローアップ (b) が本件）・IADR-0504（§残るもの が本件）。

## 決めたこと

1. `docs/operations/operations.md`「ゼロ保持認定状況の記録」に、規約の版・3 条件の照合の表・opt-out の日時・opt-out 前の送信量と受容・結論を書く。
   旧記述（未認定）は取り消し線で残し、日付つきで改める。
2. **認定の射程**を足す: opt-out は Voyage のアカウントの設定であるため、認定は opt-out を実施したアカウントの鍵に限る。
   別のアカウントの鍵を使う環境は「未認定の環境へデプロイする場合」の手順（Voyage 経路の明示的な無効化）が引き続き当たる。
   ⚠️ これは規約の照合からの導出であり、オーナーの文言ではない（規約の opt-out が Web サイトの設定であることからの推論）。
3. 所在の保証が無い点は「受け入れたリスク」として運用仕様書・セキュリティ仕様書に残す。
4. `docs/` の表示テキストへ issue 番号・IADR を新たに書かない（trace ブロックへ入れる）。
5. 凍結記録（IADR）は本文を書き換えず、末尾へ日付つきの追記を足し `updated:` を進める。

## 母集合（規則 9・10。2026-10-14 `966c8d94` 時点）

### 規則 9（誤りの側の文字列で走査してから追随先を挙げる）

走査（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md` を除く）:

1. `git grep -nE "未認定|認定待ち|認定まで|認定の前|認定され|認定前|認定でき"`
2. `git grep -n -i voyage | grep -E "ゼロ保持|認定|学習|opt-out|オプトアウト|レジデンシー"`（`src` を除く）
3. `git grep -nE "ゼロ保持|1740"`

| 当たり | 追随 |
| --- | --- |
| `docs/operations/operations.md` §埋め込みプロバイダ の「ゼロ保持設定（必須）」（未認定の間は止められる） | 追随（認定したことを日付つきで足す。止め方の記述は残す） |
| 同 「ゼロ保持認定状況の記録」（未認定 2026-07-19 / 2026-10-06 未認定のまま / 分離 #336） | **追随（本件の主対象）** |
| 同 §`DocumentUpdated` の再発行「ゼロ保持の認定は本節の上の注記のとおり未了」（初回の走査で表示幅の切り詰めにより見落とし、再走査で拾った） | 追随 |
| 同 ⚠️「未認定の環境へデプロイする場合」 | 残す（射程の外の環境に当たる旨を日付つきで足す） |
| `docs/operations/voyage-embedding-key-runbook.md:31-32`（認定は未了・認定が済むまで本書を当てない） | 追随 |
| `docs/security/security.md:682`（Voyage の保持・学習利用の行） | 追随（認定と受け入れたリスク〔所在〕を足す） |
| `docs/security/security.md:683`（クエリ文の外部送信。ゼロ保持未認定の間は無効化して受容） | 追随 |
| `docs/security/security.md:705`（未決事項。その認定までクエリ文の保護は書面上） | 追随 |
| `.ai-context/adr/IADR-0504`（決定者欄と §残るもの「認定は未了」） | 追随（末尾の日付つき追記。決定者欄は当時の裁定の引用なので書き換えない） |
| `.ai-context/adr/IADR-0025:114`（フォローアップ (b) Voyage 契約のティアB 認定） | 追随（末尾の日付つき追記） |
| `.ai-context/adr/IADR-0085:102-106`（認定は稼働環境依存として分離・記録枠を追記） | **追随しない**: 分離の決定とその記録枠の新設を述べた当時の判断で、今も正しい（枠に記録が入ったのが本件） |
| `.ai-context/adr/IADR-0422:157`（測れないもの。#336 は OPEN） | **追随しない**: 当時の残余の列挙。#336 全体の状態の記述であり、Voyage の認定が未了とは断定していない |
| `.ai-context/adr/IADR-0506:31`（blocked の 8 件に #1740） | 追随しない（当時の実測の記録） |
| `.ai-context/adr/IADR-0022:153`（未認定経路へ越境させかねない） | 対象外（LLM の経路の話で Voyage ではない） |
| `deploy/docker-compose.yml:761`・`VoyageEmbeddingProvider.cs:11`（契約時にゼロ保持を設定してから本番データを流す） | 追随しない（手順の一般則で、認定後も正しい） |
| `EmbeddingRoutingOptionsValidatorTests.cs:49`（未認定時の運用シナリオ） | 追随しない（Voyage を無効にした構成の試験。射程の外の環境で今も起こる） |
| `docs/how-to/deployment.md:171`・`scripts/README.md:63`・`scripts/republish-document-updated.js:8`・`IADR-0503:145`（節名の参照） | 追随しない（節名は変えていない） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 節名「埋め込みプロバイダの設定・ゼロ保持・再索引」は変えない → 節名で引く参照（上表の最終行）は壊れない。
- 「いつ読むか」の表の行（ゼロ保持・fail-closed 挙動を確認したい）は節を指したままで正しい。
- 規約の版が改まると照合が古くなる → 運用仕様書に「版が改まったら照合し直す」を書いた。

## 環流（planning）

完了条件の「認定できない場合は planning へ環流」は、認定したので当たらない。
ただし 08_data-egress-policy のティアB の定義は「データレジデンシー／越境保証」を含み、Voyage はこの 1 点を規約で満たさない（オーナーが受容）。
計画の文面との差になるため、環流の要否は報告で上げる（本 PR では起票しない）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | 運用仕様書に規約の版・3 条件の照合の表・opt-out の日時・opt-out 前の 960 回と受容・結論がある | 目視（diff） |
| A2 | 母集合の追随先がすべて改まり、未認定のまま残る記述が無い | 規則 9 の走査の再実行 |
| A3 | `docs/` の表示テキストに新たな issue 番号・IADR・計画 ID が無い | `check-trace-blocks`・diff の目視 |
| A4 | 凍結記録の本文を書き換えていない（追記だけ） | diff |
| A5 | 文書系の検査器が通る | `check-trace-blocks`・`check-doc-links`・`gen-knowledge-graph --check`・`check-doc-updated --base origin/develop`・`check-commit-messages`・`check-plan-id-qualification` |
