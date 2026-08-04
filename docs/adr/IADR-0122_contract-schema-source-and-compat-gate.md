---
title: IADR-0122 契約スキーマの抽出方式（C# ソース構文解析）と後方互換ゲート — IADR-0049 §3 の部分繰延解除
type: impl-adr
status: Accepted
related_ids: [NFR, FR-14, ADR-0018, ADR-0027, ADR-0029, IADR-0028, IADR-0049, IADR-0115, IADR-0116, IADR-0120]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
---

# IADR-0122: 契約スキーマの抽出方式（C# ソース構文解析）と後方互換ゲート

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（保守性・可用性）／FR-14（コンポーザビリティ）／
  [10_composability-design](../../planning/projects/microservices-platform/06_technical/10_composability-design.md)
  §3「イベントスキーマはバージョン管理し、**後方互換の追加のみ許可**（フィールド削除・意味変更は
  新バージョン＋移行期間）とする。互換性は CI の契約テストで検証する。」／
  [ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)（Accepted・
  east-west = gRPC + Protobuf、north-south = REST + OpenAPI）／
  [ADR-0027](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md)（Accepted・
  非同期メッセージングは Wolverine）／ADR-0018（固定/可変区分。エンベロープ標準は固定部）
- 関連する実装 ADR: [IADR-0049](IADR-0049_composability-standards-phased-adoption.md)（本 IADR が
  **決定 1 のうち「CI 契約テスト」だけを繰延解除**する）／
  [IADR-0028](IADR-0028_declarative-pipeline-config.md)（起動時 fail-fast）／
  [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)（逃げ道の無いゲートは無視される）／
  [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4（issue 分割）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（検査対象外ユニットの導出）
- 関連する実装仕様書:
  [作業仕様書 20260804_issue-465](../specs/20260804_issue-465_contract-schema-compat-gate.md)／
  [テスト戦略](../tests/TEST_STRATEGY.md)
- 関連 issue: #465（親 #453 / #454）

## コンテキストと課題

全面再実装（#454）では 11 サービスを作り直す。このとき最も危険なのは、サービス間の契約
（`Shared.Contracts` のイベント・API スキーマ）が**両側同時更新で気付かれずに壊れる**ことである。
片方のサービスだけ先に再実装され契約の形が変わっても、発行側と購読側を同じ PR で直せば
コンパイルもテストも通る。**壊れたことが表に出るのは、旧版が動いている環境と混在した瞬間**である。

計画 §3 はこれを「後方互換の追加のみ許可、互換性は CI の契約テストで検証」と定めているが、
[IADR-0049](IADR-0049_composability-standards-phased-adoption.md) 決定 1 が**共通エンベロープと
CI 契約テストをセットで条件付き繰延**にしている。その根拠は同 IADR の「根拠 / 代替案」にある

> 契約テストのみ先行しても検証対象（エンベロープ）が無く空振りになる。

という判断である。本 issue はこの判断の見直しにあたる。

決めるべきことは 2 つある。

1. **スキーマの抽出方式**。リフレクション（型から生成）／OpenAPI（`docs/api/openapi.yaml`）／
   proto（[ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)
   の gRPC 契約）のどれを正本とするか。east-west（gRPC）・north-south（REST）・メッセージング
   （Wolverine）で正本が異なり得る。
2. **後方互換ゲートの判定と逃げ道**。どこまでを破壊的とし、意図した破壊的変更をどう通すか。

### 実測（着手時点）

| 対象 | 実測 |
| --- | --- |
| 契約プロジェクト | 2 件（`src/platform/backend/Shared/Platform.Shared.Contracts` / `src/knowledge/backend/Shared/Knowledge.Contracts`） |
| `.cs` ファイル | 20 件 |
| public 型 | **56 件**（platform 24 / knowledge 32。record 47・class 7・enum 2） |
| メンバー（位置引数・プロパティ・const・enum メンバー） | **269 件** |
| `.proto` ファイル | **0 件**（リポジトリ全体） |
| `docs/api/openapi.yaml` に現れるイベント型 | **0 件**（BFF の REST のみ・1267 行） |
| .NET SDK（**本 PR の実装セッション**） | **不在**（`dotnet: command not found`。取得も agent proxy の policy denial で拒否）。実測記録は [`docs/specs/20260804_issue-486_bff-csproj-comment-iadr0117.md`](../specs/20260804_issue-486_bff-csproj-comment-iadr0117.md)「ビルド検証の実行可否」。**SDK の在否はセッションの base image 構成に依存する**（`scripts/setup.sh` は SDK を入れない）。同じ PR のレビューセッションでは `dotnet 10.0.302` が存在し `Platform.Shared.Contracts` のビルドに成功した実測もある |

## 検討した選択肢

### 抽出方式

| | A. C# ソースの構文解析（採用） | B. リフレクション（ビルド済みアセンブリ） | C. OpenAPI（`docs/api/openapi.yaml`） | D. proto（ADR-0029 の gRPC 契約） |
| --- | --- | --- | --- | --- |
| イベント契約（最も危険な面）を覆えるか | **覆える** | 覆える | **覆えない**（0 件） | 現状は覆えない（0 件） |
| REST DTO を覆えるか | 覆える | 覆える | 一部（BFF 公開分のみ） | 覆えない |
| 正本としての妥当性 | 実装そのもの＝ドリフトしない | 実装そのもの | **手書き/雛形生成**でコードとドリフトし得る | **未生成**。将来 east-west の正本 |
| .NET SDK 依存 | **無し**（SDK の在否に左右されない） | **有り**（SDK を持たないセッションでは実行不能） | 無し | 無し（protoc は別途） |
| ローカルで自己試験・単体テストできるか | できる | **できない**（CI 専用になる） | できる | できる |
| 属性由来のシリアライズ差（`JsonConverter` 等）を見られるか | 属性の**記述**は見られる（意味論までは解決しない） | 見られる（解決済みの型情報） | 見られる（生成された JSON 表現） | 該当なし |
| 精度の限界 | 部分評価・生成コード・入れ子型を追えない | 限界は少ない | 生成の網羅性に依存 | proto の範囲のみ |

### 判定と逃げ道

| | α. 承認付き allowlist（採用） | β. 逃げ道なしの純ゲート | γ. warn のみ（fail させない） |
| --- | --- | --- | --- |
| 破壊的変更を止められるか | 止まる（承認が要る） | 止まる | **止まらない** |
| 意図した破壊的変更の通し方 | 承認エントリ＋`--update` | **無い**（検査を外すしかない） | 素通り |
| 承認の記録 | baseline の `$acceptedBreakingChanges` に残る | — | — |
| 既存 ratchet 群との作法 | 同型（`backend-library-baseline.json` 等） | 異質 | 異質 |

## 決定

### 決定 1: 抽出方式は **C# ソースの構文解析**とする（選択肢 A）

`src/<unit>/backend/Shared/*.Contracts` の `.cs` を構文解析し、public 型・メンバー・enum 値・
`const` 値・属性を**正規化 JSON スナップショット**（`scripts/contract-schema-baseline.json`）へ
落として比較する。検査器は `scripts/check-contract-schema.js`（外部依存ゼロ Node・`--self-test`）。

- **リフレクション（B）を採らない**のは、契約の抽出に .NET SDK でのビルドが要るためである。
  **本 PR の実装セッションでは SDK が不在**であり（上記実測。ただし在否はセッションの base image 構成に
  依存し、レビューセッションでは `dotnet 10.0.302` が存在した）、そのセッションでは検査器をローカルで
  実行・自己試験できない。CI 専用の検査は「壊れたときにしか動かない検査」になり、`scripts.test.js` からの
  単体テスト（#465 の受け入れ観点）も成立しない。**この制約は環境の都合であり、精度の優劣ではない**
  ——正直に記録する。
  なお**本決定は SDK の在否に依存しない独立の根拠でも維持される**: 検査器は外部依存ゼロ（Node 標準
  モジュールのみ）で自己完結し、**ローカルでも CI でも、SDK のあるセッションでも無いセッションでも同一に
  動く**。`--self-test` が一時ツリーを実走査でき、`scripts.test.js`（受け口 = repo テスト）から単体テスト
  できるのも、ビルド成果物に依存しないためである。SDK が**恒常的に**利用可能になった場合の再評価条件は
  下記フォローアップ 5 に定める（そのときも上記の自己完結性を上回る利得が要る）。
- **OpenAPI（C）を正本にしない**のは、`docs/api/openapi.yaml` が north-south（BFF ↔ SPA）の REST しか
  含まず、**イベント契約を 1 件も含まない**ためである。最も壊れやすいのは非同期のイベント
  （[ADR-0027](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md) の
  Wolverine 経路）であり、そこが空白の正本は目的を果たさない。加えて同ファイルは手書き／雛形生成
  （`scripts/gen-openapi-skeleton.js`）であり、コードとドリフトし得る＝**正本にできない**。
- **proto（D）を正本にしない**のは、リポジトリに `.proto` が **0 件**だからである。
  [ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)
  は east-west を gRPC + Protobuf と定めており、**将来 east-west の正本は proto になる**。
  本決定はそれを否定しない（後述「フォローアップ」）。存在しない成果物を正本にはできない、という
  現時点の事実に基づく決定である。
- 走査対象は `src/<unit>/backend/Shared/` 配下で名前が `.Contracts` で終わり `.csproj` を持つ
  ディレクトリとし、パスをハードコードしない（可変機能ユニットが増えたときに検査から漏れないため。
  ユニット第一構成: IADR-0056）。`src/ai-stock-trading` は
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md) のヘルパで除外する。

### 決定 2: 破壊的の分類を次のとおり固定する

| 変更 | 分類 |
| --- | --- |
| 型・メンバー・enum メンバーの削除、契約プロジェクトの消失 | **破壊的** |
| メンバー型の変更・型種別の変更 | **破壊的** |
| 省略可能 → 必須（既定値の除去） | **破壊的** |
| 位置引数（record の primary constructor）の並べ替え | **破壊的** |
| 位置引数 ↔ プロパティの移動 | **破壊的** |
| `const` 値・`enum` 値の変更（並べ替えによる暗黙序数の変化を含む） | **破壊的** |
| 型・メンバーの属性の変更（`JsonConverter` / `JsonPropertyName` 等） | **破壊的** |
| **既定値の無いメンバーの追加** | **破壊的** |
| 型・メンバー・enum メンバー・`const` の追加（既定値付き）、必須 → 省略可能 | 非破壊 |

「既定値の無いメンバーの追加」を破壊的側へ置くのが、素朴な分類との唯一の違いである。追加は計画 §3 が
許す形だが、**既定値が無ければ旧発行者のメッセージは必須項目を欠く**（かつ既存の位置引数
コンストラクタの呼び出しが壊れる）。既定値を付ければ非破壊に落ちるため、逃げ道は常にある。

**非破壊であっても baseline との差分がある限り検査は fail する**（＝スナップショットテスト）。
`--update` で baseline を更新し、**差分そのもの（契約の変更）を PR のレビュー対象に載せる**のが
本ゲートの主眼である。契約変更が「diff に現れないまま入る」経路を残さない。

### 決定 3: 逃げ道は「承認付き allowlist」とし、承認は baseline へ記録する（選択肢 α）

破壊的変更は `scripts/contract-breaking-allowlist.json` の `approvals` に
`{ key, reason, approvedBy, issue, date }`（**すべて必須**）を書き、`node scripts/check-contract-schema.js --update`
を実行する。`--update` は承認エントリを baseline の `$acceptedBreakingChanges` へ**移し**、allowlist を
空へ戻す。定常状態の allowlist は空であり、対応する変更が無い承認が残っていれば fail する
（既存 ratchet 群と同じ 3 判定: 新規は fail・既知は通す・消えたのに残っていれば fail）。

逃げ道を置くのは [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md) の知見
（逃げ道の無いゲートは無視される・「成果物は正しいのに赤」の常態化が検査の目的を逆から壊す）による。
必須項目を強制するのは、**理由・承認者・issue・日付の残らない承認はただの無効化スイッチ**だからである。

### 決定 4: [IADR-0049](IADR-0049_composability-standards-phased-adoption.md) 決定 1 のうち **CI 契約テストのみ**を繰延解除する

- **共通エンベロープは引き続き繰延する。** [IADR-0049](IADR-0049_composability-standards-phased-adoption.md)
  決定 1 の繰延解除条件は次の 3 つであり（同 IADR 本文の (a)〜(c)）、いずれも**エンベロープについては
  未成立**である。
  - **(a)** 共通のバージョン項目・ソースメタが無いために後方互換性の判定を機械化できず障害・退行が生じた。
  - **(b)** 新しい段の追加・差し替えで既存イベント型への横断的変更が繰り返し必要になった。
  - **(c)** ABAC 属性ヒント／トレース ID をイベント本体で標準搬送する要件（監査・相関）が確定した。

  このうち **(a) の「後方互換性の判定の機械化」は、本ゲートが個々のイベント／API 型のスキーマについて
  先取りした**（型・メンバー・enum 値・`const` 値・属性の単位で機械判定できる）。ただしそれは
  **エンベロープの共通バージョン項目・ソースメタが得られたことを意味しない**——それらは依然として
  存在せず、「イベント全体に共通する版の軸で互換性を語る」ことは今もできない。したがって (a) は
  エンベロープの観点では未成立のままであり、(b)・(c) も未成立である。エンベロープ項目の確定は上流
  `07_abac-attribute-model.md` との整合を要する。本 IADR はそこへ手を付けない。
- **IADR-0049 の「契約テストのみ先行しても空振りになる」は成立しない**と判断する。同 IADR は
  契約テストの検証対象を*エンベロープ*と読んでいたが、実際の検証対象は**個々のイベント／API 型の
  スキーマそのもの**であり、それは着手時点で 56 型・269 メンバーが**現に存在する**（上記実測）。
  空振りではない。むしろ全面再実装（#454）で 11 サービスを作り直す間、エンベロープの導入を待つと
  **契約が最も壊れやすい期間を無防備で通す**ことになる。
- [IADR-0049](IADR-0049_composability-standards-phased-adoption.md) は `Superseded` にせず
  **`Accepted` のまま残置**し、決定 1 に日付付き追記を入れる（改定範囲を 1 点に限る先例:
  [IADR-0117](IADR-0117_platform-shared-kernel-placement.md) による IADR-0056 決定 3 の部分改定）。
  §5（ステージング → 本番の適用順序）と決定 3（起動時 fail-fast の維持）は本 IADR の範囲外で、
  引き続き有効である。

## 理由

- 抽出方式の選定は「どれが最も精緻か」ではなく「**今この期間に、実在する契約面を覆えるのはどれか**」で
  決まる。イベントを覆えない正本（OpenAPI）と存在しない正本（proto）は候補から落ち、残る 2 案のうち
  リフレクションは SDK を持たないセッション（本 PR の実装セッションがそうだった）で実行できず、
  ビルド成果物に依存する分だけ検査器の自己完結性を失う。C# ソース解析は精度で劣るが、
  **唯一いま全契約面を覆え、かつどのセッションでも同一に動く**。
- スナップショット方式にしたのは、契約の変更を**レビュー可能な diff**にすることが目的だからである。
  「両側同時更新で気付かれない」という失敗は、レビューアが変更に気付けないことで起きる。判定より前に
  「契約が変わったことが PR の diff に必ず現れる」ことが効く。
- 分類を破壊的側へ寄せた（属性変更・既定値なし追加・並べ替え）のは、**逃げ道があるため過剰検出の
  コストが小さい**からである。逃げ道の無いゲートなら偽陽性は致命的だが、承認 1 行で通る設計では
  「疑わしきは止める」が正しい側である。
- 既存 ratchet 群（`backend-library-baseline.json` / `test-traceability-allowlist.json` /
  `coverage-floor.json`）と同じ作法（外部依存ゼロ Node・`--self-test`・`scripts.repo.test.js` 受け口・
  `ci.yml` 独立ジョブ・`lib/excluded-units.js`）に揃えたのは、検査器ごとに操作方法が違うと
  「直し方が分からない赤」が増え、無視される検査になるためである。

## 結果

- 良い影響:
  - `Shared.Contracts` の後方互換を壊す変更が CI で止まる。全面再実装（#454）の 11 サービスが
    同じ契約面を共有していることを機械で保証できる。
  - 契約の変更が**必ずスナップショットの diff として PR に現れる**。レビューアが目視で拾う必要がなくなる。
  - 意図した破壊的変更に、理由・承認者・issue・日付の残る通し方ができた。記録は baseline に残り
    git 履歴で追える。
  - 計画 §3 の「互換性は CI の契約テストで検証する」が、エンベロープを待たずに部分的に満たされた。
- 悪い影響・トレードオフ:
  - **構文解析の限界**（作業仕様書に詳細）。部分評価（`partial`）・ソースジェネレータ生成型・入れ子型・
    `[JsonSerializerOptions]` 等の**外部設定によるシリアライズ差**は追えない。属性は「書かれているか」を
    見るだけで意味論は解決しない。着手時点でこれらは 0 件だが、増えれば盲点になる。
  - baseline（1865 行）を人が読む場面は少なく、**hand-edit すれば検査は素通りする**。他の baseline 系
    ratchet と同じ性質であり、歯止めはコードレビューである。
  - 非破壊の追加でも一度 CI が赤くなる（`--update` が要る）。意図的な設計だが、初見では戸惑う。
    失敗出力に手順を書き、`scripts/README.md` に節を設けて緩和する。
- フォローアップ:
  1. **east-west が gRPC へ移行した時点で、proto を east-west の正本に切り替える**
     （[ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)）。
     そのとき本検査はメッセージング契約と proto 未対応の面に縮退させ、新 IADR で改定する。
  2. 共通エンベロープの繰延解除条件（IADR-0049 決定 1）の充足監視。解除時は本検査の対象へ
     エンベロープを加える（型が増えるだけなので機構の変更は要らない見込み）。
  3. フロントエンドの契約（`orval` 生成物と OpenAPI の整合）は #446 系の管轄であり本件では扱わない。
  4. `Platform.Shared.Kernel`（[IADR-0117](IADR-0117_platform-shared-kernel-placement.md)）が
     `Result` / `Error` を持つようになったら、契約に載る型かどうかを判断し走査対象を見直す。
  5. **リフレクション方式の再評価**。次の 3 つが**すべて**成立したときに限り再評価する:
     (a) .NET SDK が実装・レビュー・CI のいずれのセッションでも恒常的に利用可能である（base image で
     保証される、または `scripts/setup.sh` が導入する）、(b) 構文解析の限界（本 IADR「悪い影響」と
     作業仕様書「限界」の表）が実際に 1 件以上顕在化した、(c) ビルド成果物に依存しても `--self-test` と
     `scripts.test.js` からの単体テストが維持できる目処が立つ。(a) だけでは切り替えない——SDK が
     使えることと、検査器がビルドに依存**すべき**であることは別だからである。

## 関連

- Supersedes: なし（[IADR-0049](IADR-0049_composability-standards-phased-adoption.md) 決定 1 のうち
  「CI 契約テスト」の部分だけを繰延解除する。共通エンベロープの繰延・決定 2（ステージング適用順序）・
  決定 3（起動時 fail-fast の維持）は同 IADR が引き続き有効なため `Accepted` を維持する）
- Superseded by: なし
