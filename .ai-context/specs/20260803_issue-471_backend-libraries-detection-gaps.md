---
title: 作業仕様書 — check-backend-libraries の検出漏れを塞ぐ（死にエントリ・未収載・props 非走査）
type: spec
status: done
related_ids: [NFR, ADR-0030]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
related_specs:
  - ./20260803_issue-455_backend-application-standard.md
  - ./20260803_issue-470_doc-links-code-extensions.md
  - "../../docs/tech/tech-requirements.md"
  - "../../docs/tests/TEST_STRATEGY.md"
---

# 作業仕様書: check-backend-libraries の検出漏れを塞ぐ

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — ライブラリ標準の逸脱を機械で止める）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: `ADR-0030`（バックエンドアプリケーション層のライブラリ標準。Accepted）と、
  その全量の棚卸し表である計画 `06_technical/12_backend-application-stack.md`。
  本作業は**検査器を ADR の内容へ一致させる**だけであり、新たな技術選定・ADR は伴わない
  （したがって IADR は起票しない）。
- 先行作業: [`20260803_issue-455_backend-application-standard.md`](./20260803_issue-455_backend-application-standard.md)
  （検査器 [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) と
  [`scripts/backend-library-baseline.json`](../../scripts/backend-library-baseline.json) の導入。PR #463）
- 本リポジトリの起点: [#471](https://github.com/endazon/microservices-platform/issues/471)（親: #455 / #454）
- キットとの関係: `check-backend-libraries.js` は `impl-handoff-kit` の配布物では**なく**本リポジトリ固有の
  スクリプトである（キット原本 `repo-template/scripts/` に存在しない）。したがって IADR-0115 の
  暫定デルタ／環流の対象外であり、計画側への `/plan-feedback` も要さない。

## 目的・背景

#455（PR #463）で導入した検査器の BANNED リストと走査範囲を、#454 フェーズ 0 監査で
計画 `12_backend-application-stack.md` の棚卸し表と 1 件ずつ突合したところ、3 種の検出漏れが見つかった。
いずれも現時点で実害（実リポの違反）は無いが、**後続のサービス再実装（#438〜#451）で混入経路になる**。
検査器が「検査しているつもりで何も見ていない」状態は、緑の CI が保証を偽装する点で無検査より悪い。

1. **`'Kiota'` が死にエントリ**。`matchesBanned` は「完全一致 or `banned + '.'` 前方一致」で判定するが、
   実在するパッケージ ID は `Microsoft.Kiota.Abstractions` 等であり、`'Kiota'` は 1 件も検出できない。
2. **棚卸し表の不採用・置換対象が BANNED から漏れている**（下表 4 種）。
3. **`Directory.Build.props` / `.targets` を走査しない**。MSBuild は `Directory.Build.props` の
   `<ItemGroup><PackageReference>` で配下の全プロジェクトへ一括注入できるため、そこに書けば
   `.csproj` のみの検査は素通りする。`.cs` の `using` 検査は二次防衛にしかならない
   （DI 拡張だけを使い `global using` を書かなければ抜ける）。

### 計画書の実読による確定（BANNED へ追加する 4 種）

`12_backend-application-stack.md` L76〜L85 と `ADR-0030` を実読し、次のとおり確定した。

| 追加する ID | 計画書の記述 | 確定の根拠 |
| --- | --- | --- |
| `Confluent.Kafka` | L77 「WolverineFx.Kafka ★採用 …（ADR-0028）。**Confluent.Kafka 直接利用はしない**」 | 素クライアントの直接参照を止める。`WolverineFx.Kafka` は採用側なので巻き込まない |
| `RabbitMQ.Client` | L76 「WolverineFx.RabbitMQ ★採用 … MassTransit / **素の RabbitMQ.Client を置換**」 | 同上。`WolverineFx.RabbitMQ` は採用側 |
| `Azure.Extensions.AspNetCore.Configuration.Secrets` ／ `Azure.Security.KeyVault` | L85 「**Azure Key Vault Provider**（Secret 管理）★不採用 — HashiCorp Vault（暫定は k8s Secret）」 | ADR が不採用としたのは「Azure Key Vault から secret を取る経路」である。.NET でこれを実現する実在パッケージは**構成プロバイダ** `Azure.Extensions.AspNetCore.Configuration.Secrets` と**クライアント SDK** `Azure.Security.KeyVault.{Secrets,Keys,Certificates}` の 2 系統であり、双方を対象にする。前方一致の起点は `Azure.Security.KeyVault` までとし、不採用ではない `Azure.Identity` 等の Azure SDK を巻き込まない |
| `Konscious.Security.Cryptography.Argon2` ／ `Isopoh.Cryptography.Argon2` | L79 「OpenIddict / BCrypt.Net-Next / **Argon2**（パスワードハッシュ）★不採用 — Keycloak が担う（ADR-0004 / ADR-0026）」 | Argon2 は .NET に単一の標準実装が無く複数パッケージが流通するため、実在 ID を列挙する。Konscious は Argon2 と Blake2 を別パッケージで出しており **Blake2 は ADR の不採用対象ではない**ため、前方一致の起点を `.Argon2` までに留め同一作者の別用途を巻き込まない |

`'Kiota'` は `'Microsoft.Kiota'` へ訂正する（`Microsoft.Kiota.Abstractions` /
`Microsoft.Kiota.Serialization.Json` 等に前方一致する）。

## 対象範囲

- 含むもの:
  1. [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js)
     - `BANNED`: `'Kiota'` → `'Microsoft.Kiota'`、上表 4 種（実在 ID として 6 エントリ）を追加。
     - `isScannedBuildFile()` を新設し、`scanTree()` の走査対象へ `*.props` / `*.targets`
       （雛形配布用の `.sample` 付きを含む）を追加する。`src/` は baseline 対象、`templates/` は
       従来どおり `template-banned` として即 fail。
     - `packageReferencesOf()` が `GlobalPackageReference`（CPM で全プロジェクトへ参照を注入する要素）も
       拾うようにする。**`PackageVersion` は拾わない**（後述「最重要の罠」）。
     - `walk()` / `scanTree()` に走査起点 `root` の引数を足し、自己試験が一時ツリーを実走査できるようにする。
     - 自己試験に検出漏れ 3 種の正例・負例を固定する。
  2. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js): 上記の回帰テストを追加。
  3. [`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md) ／
     [`docs/tests/TEST_STRATEGY.md`](../../docs/tests/TEST_STRATEGY.md): 走査範囲の記述を実装に一致させる。
- 含まないもの:
  - ratchet 方式・baseline の運用そのものの変更（`classifyAgainstBaseline` は無改変）。
  - Domain 依存規律・xUnit 版整合の検査ロジックの変更。
  - 実リポの不採用ライブラリの移行（各サービスの再実装 issue #438〜#451 の範囲）。
  - `src/ai-stock-trading`（別プロジェクトの submodule。`EXCLUDED_UNITS` のまま）。

## 方針

- **ADR に忠実な粒度で BANNED を書く**。計画書が不採用としたものだけを、実在するパッケージ ID で書く。
  前方一致の起点を伸ばしすぎると採用ライブラリ（`WolverineFx.RabbitMQ`）や無関係な同族パッケージ
  （`Konscious...Blake2`・`Azure.Identity`）を巻き込む。**正例と負例を対で自己試験に置く**ことで
  この境界を仕様として固定する。
- **⚠️ 最重要の罠: `PackageVersion` を違反にしない**。
  [`src/Directory.Packages.props`](../../src/Directory.Packages.props)（CPM）は、baseline を消化しきるまで
  不採用パッケージ（MassTransit / Serilog / FluentAssertions）の**版定義**を正当に持つ設計である
  （#455 の決定。baseline が空になった時点で削除する）。走査対象に props を加える変更で、もし
  `PackageVersion` を「参照」と見做すと**42 件の偽陽性**が一斉に出て検査が使い物にならなくなる。
  検査は `PackageReference`（＋注入経路として同値の `GlobalPackageReference`）**のみ**を対象とし、
  この不変条件を自己試験と回帰テストの両方で固定する。雛形の
  `templates/unit-template/backend/Directory.Packages.props.sample` も同じ性質を持つため対で試験する。
- **走査対象に入ったことを実走査で確かめる**。BANNED に足しても `scanTree()` が当該ファイルを
  開かなければ検出されない。関数単位の試験だけでは「走査対象に入っているか」を確かめられないため、
  `scanTree(root)` を一時ツリーに対して実走させる自己試験を置く（#471 の 3 種すべてをこの形で固定）。
- **baseline は動かさないのが期待値**。走査範囲と BANNED を広げた結果、既存コードにヒットが出た場合は
  「新規混入」ではなく既存の負債なので baseline へ追加する（ratchet の作法）。ヒット 0 なら baseline は不変。

## 実リポ走査の結果

変更後の検査器で `src/`（`ai-stock-trading` を除く）と `templates/` を全走査した結果、
**追加した 6 エントリと props / targets 走査による新規ヒットは 0 件**であった。
`classifyAgainstBaseline` の `added` / `stale` はいずれも空で、既知残件は 42 件 / 29 プロジェクトのまま変わらない。
よって [`scripts/backend-library-baseline.json`](../../scripts/backend-library-baseline.json) は**不変**である。

- `RabbitMQ.Client` は 6 箇所の**コメント**（`AspNetCore.HealthChecks.Rabbitmq` が RabbitMQ.Client 7 と
  非互換である旨の注記。#269）にのみ現れる。`using` 形ではないため `bannedInSource` は拾わない
  （既存の自己試験「コメント行内の語は using 形でなければ拾わない」が保証する挙動）。
- `src/Directory.Build.props` は `PropertyGroup` のみで `PackageReference` を 1 件も持たない。
- `src/Directory.Packages.props` と雛形の 2 つの `.sample` は `PackageVersion` のみで違反 0（上記の罠）。

## 受け入れ基準

issue [#471](https://github.com/endazon/microservices-platform/issues/471) の受け入れ基準（4 件）を転記する。

- [x] `Microsoft.Kiota.Abstractions` を含む csproj フィクスチャで違反検出（自己試験）
      — 自己試験「実地(a): csproj の Microsoft.Kiota.Abstractions を検出」で、一時ツリーを
      `scanTree()` に実走査させ `["Microsoft.Kiota"]` を得ることを固定した。
- [x] 上表 4 種を BANNED に追加し、自己試験に正例・負例
      — `Confluent.Kafka` / `RabbitMQ.Client` / Key Vault 2 種 / Argon2 2 種を追加。負例として
      `WolverineFx.Kafka`・`WolverineFx.RabbitMQ`・`Azure.Identity`・`Azure.Core`・
      `Konscious.Security.Cryptography.Blake2`・`Isopoh.Cryptography.Blake2b` が非該当であることを固定した。
- [x] `Directory.Build.props` 経由の混入をフィクスチャで検出（自己試験）
      — 自己試験「実地(b): Directory.Build.props 経由の一括注入を検出」。
- [x] 実リポ走査で新規違反 0 のまま（baseline 増減なし）
      — 上記「実リポ走査の結果」のとおり `added` / `stale` ともに空、baseline は不変。

本作業で追加した不変条件（issue の 4 件に加えて満たすことを確認した）:

- [x] CPM の `PackageVersion` は違反にならない（自己試験「実地(c)」＋実ファイル 4 本での回帰テスト）。
      これが無いと props 走査の追加だけで 42 件の偽陽性が出る。
- [x] `GlobalPackageReference`（CPM の全プロジェクト注入）は違反になる。
- [x] `node scripts/check-backend-libraries.js --self-test` が exit 0（33 件 → **49 件**）。
- [x] `node scripts/check-backend-libraries.js` が exit 0（新規混入 0 / Domain 依存規律 OK / 既知残件 42 件）。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑（180 件 → **186 件**）。
- [x] `node scripts/check-doc-links.js` ／ `check-commit-messages.js` ／ `check-test-traceability.js` が緑。

## 影響・リスク

- **偽陽性**: 追加した 6 エントリはいずれも実在パッケージ ID の完全一致 / 前方一致であり、
  負例（採用側の `WolverineFx.*`・同族の Blake2・`Azure.Identity`）を自己試験で固定した。
  走査範囲の拡大は `.props` / `.targets` に限られ、`obj` / `bin` は従来どおり `SKIP_DIRS` で除外される。
- **偽陰性の残り**: `using` 経路では `Konscious.Security.Cryptography` 名前空間（Argon2 と Blake2 で共通）を
  検出できない。ここを塞ぐには ADR が不採用としていない Blake2 まで巻き込む必要があるため、
  意図的に package ID 側の検査に委ねる（ソースコメントに理由を残した）。
- **CI**: `ci.yml` の `backend-libraries` ジョブは自己試験 → 本走査の順で従来どおり動く。
  ワークフローの変更は伴わない。
- **後続への影響**: #438〜#451 の再実装で Wolverine トランスポートを導入する際、素の
  `Confluent.Kafka` / `RabbitMQ.Client` を足すと **fail する**。これは意図した締め付けであり、
  正しい入口は `WolverineFx.Kafka` / `WolverineFx.RabbitMQ` である。
