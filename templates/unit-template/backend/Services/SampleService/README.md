# SampleService — サービスの標準構成（単一プロジェクト＋VSA/DDD フォルダ）

本雛形は **IADR-0282**（オーナー裁定 2026-08-28）の標準樹形に従う。

```
Services/<Name>/
├── <Name>.csproj        # 単一プロジェクト（層をプロジェクト分割しない）
├── Program.cs           # 合成ルート（束ねるだけ。判断を書かない）
├── Features/<集約>/<操作>/   # Endpoint.cs / Command.cs（または Query.cs）/ Handler.cs
├── Domain/              # エンティティ・値オブジェクト・ドメインイベント（＋ Errors/）
├── Infrastructure/      # Persistence/（DbContext・Configurations/・Migrations/）・
│                        # Authentication/・Messaging/・ExternalServices/
├── Common/              # サービス固有の横断関心（Exceptions/・Behaviors/）
└── Tests/               # <Name>.Tests.csproj ＋ **本体の鏡写し**（IADR-0334）
                         #   Features/<集約>/<操作>/ … 段まで写す（統合テストも種別で割らずここへ）
                         #   Domain/ ・ Infrastructure/<Sub>/ ・ Common/<Sub>/ … 相手が在るぶんだけ
                         #   Tests/ 直下 … 写す相手が無いもの（器・GlobalUsings.cs・Program.cs 由来）
```

- **参照方向はフォルダ（名前空間）で守る**: `Domain/` は `Features/`・`Infrastructure/`・
  `Common/Behaviors` を using しない。`Infrastructure/` は `Features/` を using しない。
- **Result / Error・DDD 基底型は `Platform.Shared.Kernel`**（サービス個別の `Common/Result.cs` を
  作らない —— IADR-0229）。
- **サービス間契約はユニットの Shared（`<Unit>.Contracts`）へ**。サービス個別の Contracts
  プロジェクトは置かない。
- 空のフォルダは作らない（必要になったスライス・アダプタから増やす。枠だけの構造を先に
  作らない —— IADR-0282 決定 3 が旧 .gitkeep 枠を全廃した経緯）。
