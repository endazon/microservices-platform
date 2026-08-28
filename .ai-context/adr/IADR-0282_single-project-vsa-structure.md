---
title: IADR-0282 サービスは単一プロジェクト＋VSA/DDD フォルダ構成とする（8 要素プロジェクト実体化の撤回）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
  - IADR-0027
  - IADR-0218
  - IADR-0229
  - IADR-0280
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0282 サービスは単一プロジェクト＋VSA/DDD フォルダ構成とする

## 起点・関連

- **オーナー裁定 2026-08-28（本 PR #1021 のセッション中）。** バックエンドの構成として
  「単一 csproj ＋ Features / Domain / Infrastructure / Common ＋ Tests」の樹形が具体例つきで
  指示され、続くヒアリングで次の 3 点が確定した:
  1. **器は例示どおり完全リネームする**（`<Name>.Api.csproj` → `<Name>.csproj`。`src/` 中間層を廃し
     `Services/<Name>/` 直下へ。テストは `Services/<Name>/Tests/`）。
  2. **波 1E で実体化した層プロジェクト（.Domain / .Application / .Infrastructure / .Contracts、58 個）は
     撤去し、実コードは単一プロジェクト内のフォルダへ統合する。**
  3. **Result / Error は Platform.Shared.Kernel を使い続ける**（IADR-0229 不変。サービス個別の
     `Common/Result.cs` は置かない）。
- 本 ADR は **IADR-0280（8 要素標準の実体化）の決定 1・2・3・4・5・7 を supersede する**。
  決定 6（DDD 基底型は `Platform.Shared.Kernel`）は**存続**する。
- IADR-0027 が「アセンブリ分割は過剰」と述べた判断は**結果として復活**するが、根拠が変わる —— 分割を
  やめる代わりに、**プロジェクト内部のフォルダ構造と参照方向を規範化**する（IADR-0027 は構造を
  規範化していなかった）。IADR-0218 の「.gitkeep の枠」は**全廃**する。

## コンテキストと課題

計画 `12_backend-application-stack.md`（fixed）の 8 要素プロジェクト標準は、波 1E で
56+ プロジェクトとして実体化され、FeedbackService（パイロット移送）・GraphService（#912 の
Domain/Application/Infrastructure 実コード）まで進んだ。実測で見えたのは:

- 14 サービス × 6〜8 プロジェクトはビルドグラフ・slnx・CI の負担が大きく、**ほとんどの
  プロジェクトが空のまま**である（枠のみ 50 個超）。
- 各サービスの実装は単一 `.Api` プロジェクトに集中しており、**移送の費用に対して境界の便益が
  出ていない**（境界はフォルダ＋機械検査でも守れる）。

オーナーは 2026-08-27 に案 C′（8 要素の物理配置）を裁定したが、**2026-08-28 の本裁定でこれを
改め**、単一プロジェクト＋フォルダ規範へ確定した。

## 決定

### 決定 1 — 標準樹形（サービス単位）

```
src/<unit>/backend/Services/<Name>/
├── <Name>.csproj                  # 単一プロジェクト（.Api 接尾辞を廃止）
├── Program.cs                     # 合成ルート
├── Features/<集約>/<操作>/        # Vertical Slice: Endpoint.cs / Command.cs|Query.cs / Handler.cs
├── Domain/                        # エンティティ・値オブジェクト・ドメインイベント（＋ Errors/）
├── Infrastructure/
│   ├── Persistence/               # DbContext・Configurations/・Migrations/
│   ├── Authentication/
│   ├── Messaging/                 # バス発行・購読のアダプタ
│   └── ExternalServices/          # HTTP クライアント等の外部接続アダプタ
├── Common/                        # サービス固有の横断関心（Exceptions/・Behaviors/）
└── Tests/
    ├── <Name>.Tests.csproj        # テストは独立プロジェクト（同一 csproj には入れられない）
    ├── Features/                  # スライス単位のテスト
    └── Domain/
```

- **Worker を持つサービス**（例: IngestionService / ConversionService）は、別デプロイ実体である
  Worker を `Services/<Name>/Worker/<Name>.Worker.csproj` として残す（デプロイ形状は本裁定の
  射程外）。内部フォルダは本樹形に準じる。
- **Result / Error・DDD 基底型は `Platform.Shared.Kernel`**（決定 3 / IADR-0280 決定 6 存続）。
  `Common/Result.cs` を作らない。
- **サービス間契約はユニットの Shared**（`Knowledge.Contracts` / `Platform.Shared.Contracts`）に
  置く（従来どおり）。サービス個別の `.Contracts` プロジェクトは置かない。
- 旧 `Foundation/` / `Composable/` の区分は**廃止し Features/ ほかへ吸収**する（区分の意図
  だった「合成点の明示」は Program.cs と Infrastructure/ が担う）。

### 決定 2 — 参照方向はフォルダ（名前空間）で規範化し、機械検査する

プロジェクト境界が無くなるため、参照方向は **using（名前空間）単位**で検査する:

- `Domain` は `Features` / `Infrastructure` / `Common.Behaviors` を using しない
  （外部ライブラリ不使用の規律も ADR-0030 選定基準 3 のまま）。
- `Features` は `Domain` / `Infrastructure` / `Common` を使ってよい。逆（`Infrastructure` →
  `Features`）は禁止。
- `check-unit-dependencies.js` の規則 3（プロジェクト参照の層方向）は、移送波で
  **名前空間走査版へ書き換える**（同スクリプトの検査対象を csproj 参照からフォルダ由来の
  名前空間 using へ）。

### 決定 3 — 撤去と移送

- 層プロジェクト 58 個（各サービスの .Domain / .Application / .Infrastructure / .Contracts）と
  SharedKernel の .gitkeep 枠を**撤去**する。slnx から登録を外す。
- 既に実コードが入った FeedbackService（Domain / Infrastructure・Migrations）と
  GraphService（Domain のパーサ・Application のポート・Infrastructure のアダプタ）は、
  単一プロジェクト内の対応フォルダへ**同一名前空間規範で移送**する。
- 命名の追随: ルート名前空間は `<Name>`（`.Api` を含まない）。既存の `<Name>.Api.*` 名前空間・
  `Tests` プロジェクト名（`<Name>.Api.Tests` → `<Name>.Tests`）・Dockerfile・compose / helm の
  ビルドコンテキスト・CI（ci.yml のビルド対象は slnx 経由のため原則不変）・検査器
  （パス・名前空間を仮定する check-*）を移送波で一括追随する。

### 決定 4 — 移行の段取り（進行中の波を壊さない）

1. **本 ADR・テンプレート（`templates/unit-template/backend`）の書き換えを先行**させる
   （新規ユニットの雛形が旧標準のまま増殖しない）。
2. **進行中の波 3 のトラックは現行配置のまま完了させる**（オーナー指示「実施中の作業はそのまま」）。
3. **移送は専用波（アーキ移送波）で一括実施**する: リネーム → 層プロジェクト撤去・実コード統合 →
   Features スライス化 → 検査規則の書き換え → 全検証。サービス単位で領域が非重複のため
   並列可。移送波の完了までは新規コードも現行配置で書き、移送波が一括変換する
  （「新規は新様式」を先行させると同一サービス内に二重構造が生じ、移送の照合が壊れる）。

### 決定 5 — 計画への環流

計画 `12_backend-application-stack.md`（fixed）の 8 要素プロジェクト標準とは**構成単位が異なる**
（8 つの**関心**は保つが、**物理プロジェクトにしない**）。planning へ環流 issue を起票し、
計画側の改定（新しい計画 ADR）を依頼する。planning#490（実体化の解釈）・planning#491 は
本裁定により**前提が変わった**旨を追記する。

## 検討した選択肢（要点）

- **案 C′（8 要素の物理配置・2026-08-27 裁定）**: 波 1E で着手済みだったが、空枠の維持費と
  ビルドグラフの複雑さが便益を上回った。オーナーが本裁定で撤回。
- **層プロジェクト温存＋内部フォルダのみ規範化**: 例示（単一 csproj）と食い違う。不採用
  （ヒアリング設問 2 で撤去が確定）。
- **Common/Result.cs をサービス毎に持つ**: Shared.Kernel と二重になる。不採用（設問 3）。

## 結果

- 新規ユニットの雛形は本 ADR の樹形で生成される（テンプレート改修は本 ADR と同一 PR）。
- 実サービス 14 個の移送はアーキ移送波が実施し、進捗は移送波の作業仕様書が持つ（ここへ書かない）。
- 8 要素の**関心の分離**は Features / Domain / Infrastructure / Common ＋ ユニット Shared ＋
  Platform.Shared.Kernel の**フォルダと共有プロジェクト**で維持される。

## 関連

- Supersedes: [IADR-0280](./IADR-0280_eight-element-standard-materialization.md)（決定 6 を除く）
- 存続: IADR-0229（Result / Error の公開）・IADR-0280 決定 6（DDD 基底型）
- 計画側: `06_technical/12_backend-application-stack.md`（環流 issue で改定依頼）・ADR-0030・ADR-0041
