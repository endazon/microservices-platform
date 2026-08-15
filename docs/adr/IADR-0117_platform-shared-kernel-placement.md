---
title: IADR-0117 共有カーネル Platform.Shared.Kernel の配置（IADR-0056 決定 3 の部分改定）
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0019, ADR-0030, ADR-0041, IADR-0056, IADR-0057, IADR-0116, IADR-0196]
author: Claude
created: 2026-08-03
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
---

# IADR-0117: 共有カーネル `Platform.Shared.Kernel` の配置（IADR-0056 決定 3 の部分改定）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-03
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（保守性）／
  [ADR-0030](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（Accepted・
  アプリケーション層のライブラリ標準。選定基準 3「Domain は外部ライブラリ依存ゼロ、SharedKernel は自前実装（Result 型）」）／
  ADR-0019（ユニット構成）
- 関連する実装 ADR: [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（本 IADR が**決定 3 を部分改定**する）／
  [IADR-0057](IADR-0057_unit-dependency-machine-check.md)（依存方向の機械検査）／
  [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)（再実装の進行規約）
- 関連する実装仕様書:
  [作業仕様書 20260803_issue-455](../specs/20260803_issue-455_backend-application-standard.md)（未決事項 1）／
  [技術要件書](../tech/tech-requirements.md)「バックエンドアプリケーション層標準」
- 関連 issue: #455（親 #454 フェーズ 0）

## コンテキストと課題

計画 ADR-0030 は、Result / Error を**外部ライブラリ（OneOf・CSharpFunctionalExtensions）に頼らず
SharedKernel に自前実装する**と定めた（選定基準 3）。その構成図
（[12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)
「プロジェクト構成（サービス単位）」39 行目）は `SharedKernel` を**サービス単位**の 1 プロジェクトとして示している。

一方、本リポジトリは**ユニット第一構成**（[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)・ADR-0019）を採り、
同 IADR の**決定 3（依存方向）**で「knowledge → platform は `src/platform/backend/Shared/` の
**2 プロジェクト**（`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure`）のみ許可」と確定している
（機械検査は [IADR-0057](IADR-0057_unit-dependency-machine-check.md) の `scripts/check-unit-dependencies.js`）。

この 2 つを素直に重ねると次の不整合が起きる。

1. 構成図どおりにサービス単位で `SharedKernel` を作ると、**サービス 11 個ぶんの Result 型が分裂**する。
   Result / Error はサービス境界をまたいで同一の型である必要がある（BFF がサービスの結果を集約し、
   イベント契約が失敗を表現する）ため、分裂した型は `Platform.Shared.Contracts` の契約に載せられない。
2. かといって共有カーネルを `platform/backend/Shared/` 以外へ置くと、knowledge ユニットから参照できない
   （IADR-0056 決定 3 に違反する）。
3. `Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` のどちらかへ Result を同居させる案も、
   Domain 層が **Contracts（シリアライズ・イベント契約）や Infrastructure（EF Core・HTTP）へ依存する**ことになり、
   ADR-0030 選定基準 3「Domain 層は外部ライブラリ依存ゼロ」を破る。

すなわち「**Domain 層の外部依存ゼロ**」と「**ユニット外参照は platform/backend/Shared のみ**」を同時に満たす
置き場が、現行の 2 プロジェクトの中に存在しない。IADR-0056 は Accepted であり、その制約値（2 プロジェクト）を
新 IADR なしに書き換えることはできない（`CLAUDE.md` 禁止事項）。本 IADR でこの 1 点だけを改める。

## 検討した選択肢

| | A. `platform/backend/Shared/` に `Platform.Shared.Kernel` を新設し許容を 3 へ（採用） | B. サービス単位に `<Name>.SharedKernel` を置く（計画構成図どおり） | C. `Platform.Shared.Contracts` に Result を同居させる |
| --- | --- | --- | --- |
| Result 型の同一性 | 全サービスで単一の型 | **11 個に分裂**。契約に載せられない | 単一 |
| Domain 層の外部依存ゼロ（ADR-0030 基準 3） | 満たす（Kernel は .NET 標準のみ） | 満たす | **破る**（Contracts はシリアライズ/イベント契約を持つ） |
| IADR-0056 決定 3 との整合 | 部分改定が要る（2 → 3） | knowledge 側が platform の非 Shared を参照しない限り成立 | 改定不要 |
| ユニット単独ビルド（submodule ユニット） | Shared 参照は既に前提。追加負担なし | ユニット内で完結（有利） | 追加負担なし |
| 契約とドメイン語彙の分離 | 保たれる | 保たれる | **崩れる**（契約プロジェクトがドメイン基底を抱える） |

## 決定

**[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) の決定 3（依存方向）を部分改定し、
`src/platform/backend/Shared/` の**ユニット外から参照可能なプロジェクトを **2 → 3 とする。

1. 追加するのは **`Platform.Shared.Kernel`** の 1 プロジェクトのみである。改定後の許容は次の 3 つになる。
   - `Platform.Shared.Contracts`（既存）
   - `Platform.Shared.Infrastructure`（既存）
   - **`Platform.Shared.Kernel`（本 IADR で追加）** — ADR-0030 の SharedKernel。Result / Error・共通基底を置く。
2. `Platform.Shared.Kernel` は **.NET 標準以外の `PackageReference` を持たない**。ADR-0030 選定基準 3 の
   「Domain 層は外部依存ゼロ」を成立させるための置き場であり、ここが汚れると各サービスの Domain も汚れる。
   `scripts/check-backend-libraries.js` は `*.Domain.csproj` の `ProjectReference` を
   `Platform.Shared.Kernel` のみ許可する形でこの規律を機械強制する。

   > **［2026-08-04 改訂 / 追随 2026-08-15・#500］本決定 2 が引用する ADR-0030 選定基準 3 を、計画
   > [ADR-0041](../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md)
   > が部分改定した。上の本文は書き換えず残置する。現行値は ADR-0041 を正とする。**
   >
   > - 「**.NET 標準以外の `PackageReference` を持たない**」（ゼロ）は、「**Result 型の実装 1 つに限る**
   >   （現行: `CSharpFunctionalExtensions`）」へ改まる。**この 1 つ以外を追加してはならない。**
   > - 外部ライブラリは `Platform.Shared.Kernel` の**内部実装としてのみ**使う。`Domain` / `Application` /
   >   `Api` / `Infrastructure` は同プロジェクトが公開する自前の型（`Result` / `Result<T>` / `Error`）だけを
   >   参照し、外部ライブラリの型・名前空間を直接参照してはならない（ADR-0041 決定 2）。
   > - 機械強制は本決定が挙げる `ProjectReference` の許可に加え、`SHARED_KERNEL_ALLOWED`（許可リスト）
   >   による 2 系統になった。**許可リスト外が `Platform.Shared.Kernel` へ入れば fail する。**
   > - **決定 2 の趣旨（ここが汚れると各サービスの Domain も汚れる）は変わっていない。** 変わったのは
   >   汚れの許容量がゼロから「名指しの 1 つ」になった点だけである。詳細は
   >   [IADR-0196](./IADR-0196_shared-kernel-result-library-allowlist.md)。
3. **改定範囲はこの 1 点に限る。** IADR-0056 の他の決定（1 振り分け・2 ビルド・4 フロントエンド合成・
   5 命名・6 submodule 境界）と、決定 3 のうち「**platform → 可変ユニットは禁止**」「統合テストの例外」は
   引き続き有効である。したがって IADR-0056 は `Accepted` のまま残置する（`Superseded` にはしない）。
4. **実体プロジェクトは本 PR では作成しない。** `Platform.Shared.Kernel` は「最初にそれを必要とする
   サービス再実装 issue（#438〜#451）」が作る。#455 の範囲は標準の確立と機械的強制であり、
   使う当てのない空プロジェクトを先に置くのは [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)
   規約 4（レビュー可能な変更単位）に反する。本 IADR はその配置先を先に確定しておくものである。
5. 本決定は**計画 ADR との衝突ではない**。
   **［2026-08-15 追記 / #500］本項が述べる「ADR-0030 の意図」のうち「Result を外部ライブラリに頼らず自前で持つ」は、
   計画 ADR-0041 が改定した**（現在は「自前の公開型で包み、内部実装としてのみ外部ライブラリを使う」）。
   **配置に関する本決定の結論は変わらない**——変わったのは Kernel の中身であって置き場所ではない。
   ADR-0030 の意図（Result を外部ライブラリに頼らず自前で持つ・
   Domain を外部依存ゼロにする）はそのまま満たす。計画側の構成図はサービス内の**論理レイヤ**を示したもので
   あり、本決定はユニット第一構成における**物理配置の具体化**にあたる。この読み替えが妥当であることは
   `/plan-feedback`（「構成図はサービス内の論理レイヤであり物理配置は実装裁量」と明記する提案）で計画側へ環流する。

## 理由

- 選択肢 B を採れないのは、Result 型の同一性がサービス境界をまたぐ要件だからである。11 個の
  `<Name>.SharedKernel.Result` は互いに別型であり、BFF の集約・`Platform.Shared.Contracts` のイベント契約に
  載せた瞬間に変換コードが 11 通り必要になる。「過度な共通化は避ける」（計画構成図の注記）は
  **ドメインロジックの共通化**への戒めであって、Result のような言語基盤に近い型には当たらない。
- 選択肢 C を採れないのは、ADR-0030 選定基準 3 が明示的に禁じる形になるためである。Domain →
  Contracts の依存は、シリアライズ属性・イベント契約・（将来の）proto 生成物を Domain へ持ち込む。
- 許容を 2 → 3 に**増やす**改定であり、依存方向（platform → 可変ユニット禁止）の一方向性は変えていない。
  IADR-0056 決定 3 が守ろうとした「ユニットの submodule 切り出し可能性」は損なわれない
  （切り出したユニットが参照するのは引き続き platform の Shared 配下のみである）。
- 機械検査（[IADR-0057](IADR-0057_unit-dependency-machine-check.md) の `check-unit-dependencies.js`）は
  許容判定を `^src/platform/backend/Shared/` の**パス接頭辞**で行っているため、本改定によるスクリプト変更は
  不要である（増えたプロジェクトは自動的に許容される）。改定で更新が要るのは**件数を書いた文書**だけである。

## 結果

- 良い影響:
  - ADR-0030 の Result / Error 標準を、ユニット第一構成を壊さずに実装できる置き場が確定した。
  - `*.Domain.csproj` の許容 `ProjectReference` を `Platform.Shared.Kernel` 1 つに固定でき、
    `scripts/check-backend-libraries.js` が Domain 層の依存規律を機械強制できる。
  - 作業仕様書 20260803_issue-455 の未決事項 1 が解消し、後続 13 issue が同じ前提で着手できる。
- 悪い影響・トレードオフ:
  - ユニット外参照の許容が 1 つ増え、`platform/backend/Shared/` に何を置いてよいかの線引きが 1 段ゆるむ。
    歯止めとして、決定 2（Kernel は .NET 標準以外の `PackageReference` を持たない）を機械検査で固定する。
    **［2026-08-15 追記 / #500］現行の歯止めは「ゼロ」ではなく「許可リスト 1 件」である**——計画 ADR-0041 が
    決定 2 の引用する選定基準 3 を改定したため。機械検査は `SHARED_KERNEL_ALLOWED` の許可リストと、
    Kernel の `ProjectReference` を 0 件に固定する判定の 2 系統になった（[IADR-0196](./IADR-0196_shared-kernel-result-library-allowlist.md)）。
  - 件数「2 プロジェクト」を書いた文書が複数あり、改定のたびに追随が要る
    （本 PR では `CLAUDE.md` / `src/README.md` / `templates/unit-template/README.md` /
    `docs/how-to/adding-a-unit-submodule.md` / `docs/tech/tech-requirements.md` を更新した）。
  - 実体プロジェクトが未作成の期間は、文書上の「3」と実際に存在する「2」が一致しない。
    最初のサービス再実装 issue で解消する。
- フォローアップ:
  1. **`Platform.Shared.Kernel` の実体作成**（最初にそれを必要とするサービス再実装 issue。#438〜#451）。
     作成時に `platform/backend/backend.slnx` へ登録し、Domain 層からの参照を実地で確認する。
  2. `/plan-feedback` で計画側へ「構成図はサービス内の論理レイヤであり、物理配置は実装裁量」の明記を提案する。
  3. [IADR-0057](IADR-0057_unit-dependency-machine-check.md) の本文にある件数表記（「Shared 2 プロジェクト」）は
     Accepted の本文であり書き換えない。現行値は本 IADR を正とする。

## 関連

- Supersedes: なし（[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) §決定 3 の
  「許容プロジェクト数」のみを部分改定する。振り分け・ビルド・フロントエンド合成・命名・submodule 境界、
  および決定 3 のうち「platform → 可変ユニット禁止」「統合テスト例外」は同 IADR が引き続き有効なため
  `Accepted` を維持する）
- Superseded by: なし
