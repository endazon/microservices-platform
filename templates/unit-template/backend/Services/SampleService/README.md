# SampleService — サービスの標準構成（単一プロジェクト＋VSA/DDD フォルダ）

本雛形は **IADR-0282**（オーナー裁定 2026-08-28）の標準樹形に従う。

```
Services/<Name>/
├── <Name>.csproj        # 単一プロジェクト（層をプロジェクト分割しない）
├── Program.cs           # 合成ルート（束ねるだけ。判断を書かない）
├── Features/<集約>/<操作>/   # 操作の処理: Endpoint.cs / Command.cs（または Query.cs）/ Handler.cs /
│                        #   *Consumer.cs / 常駐ジョブ。**契機の形では決めない**（下の注記）
├── Domain/              # エンティティ・値オブジェクト・ドメインイベント（＋ Errors/）
├── Infrastructure/      # Persistence/（DbContext・Configurations/・Migrations/）・
│                        # Authentication/・Messaging/・ExternalServices/
├── Common/              # サービス固有の横断関心（Exceptions/・Behaviors/）
└── Tests/               # <Name>.Tests.csproj ＋ **本体の鏡写し**（IADR-0334）
                         #   Features/<集約>/<操作>/ … 操作フォルダまで写す（契機の形によらない。
                         #     統合テストも種別で割らずここへ）
                         #   Domain/ ・ Infrastructure/<Sub>/ ・ Common/<Sub>/ … 相手が在るぶんだけ
                         #   Tests/ 直下 … 写す相手が無いもの（器・GlobalUsings.cs・Program.cs 由来）
```

- 🔴 **「操作」は契機の形では決めない**（`ADR-0077`。オーナー裁定 2026-09-03）。
  **操作とは、そのサービスが外部からの 1 つの契機に応えて行う 1 つのユースケースである。**
  HTTP 要求・イベント購読・スケジュール実行のどれで駆動されるかは、操作の切り方を変えない。
  **本雛形は HTTP の例（`Samples/Create/`）しか持たないが、`Features/<集約>/<操作>/` は HTTP 専用の段ではない**
  —— `Endpoint.cs` を持たずスケジュールだけで駆動される操作フォルダも、`Endpoint.cs` と常駐ジョブが
  同居する操作フォルダも、本体リポジトリに実在する（実例は
  [`templates/unit-template/README.md`](../../../README.md)「『操作』とは何か」）。
  **「操作＝登録表に登録された HTTP 端点」と読まないこと** —— `ADR-0077` 決定 3 が退けた読みであり、
  HTTP 端点を持たないサービスで**操作フォルダが 1 つも生まれなくなる。**
  分界は「入口の配線」と「操作の処理」である（`ADR-0068` 決定 1 の延長）——
  **入口の配線は現在の置き場に残し、操作の処理だけを 3 段目へ下ろす。**
- **参照方向はフォルダ（名前空間）で守る**: `Domain/` は `Features/`・`Infrastructure/`・
  `Common/Behaviors` を using しない。`Infrastructure/` は `Features/` を using しない。
- **Result / Error・DDD 基底型は `Platform.Shared.Kernel`**（サービス個別の `Common/Result.cs` を
  作らない —— IADR-0229）。
- **サービス間契約はユニットの Shared（`<Unit>.Contracts`）へ**。サービス個別の Contracts
  プロジェクトは置かない。
- 空のフォルダは作らない（必要になったスライス・アダプタから増やす。枠だけの構造を先に
  作らない —— IADR-0282 決定 3 が旧 .gitkeep 枠を全廃した経緯）。
