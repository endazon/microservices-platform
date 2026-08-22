---
title: 作業仕様書 — 雛形 SampleService.Tests を xUnit1051 から剥がす（17 本目）（#882）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0060
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_xunit1051-staged-adoption-harness.md"
issue: "#882"
---

# 作業仕様書 — 雛形 SampleService.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で**最小（1 箇所）**。`Refs #882`（`Closes` は最後の 1 本だけ）。

雛形は**新規ユニットへ配られる種**である。ここが未移行のままだと、
新しいユニットは**最初から負債を持って生まれる**。件数が小さいこと以上に、
**早く剥がすこと自体に価値がある**。

## 🔴 なぜ雛形が「17 本目」なのか（実測で確認した）

雛形は `templates/` に在り `src/` の外だが、**CI の `template-backend-build` が
`src/.template-buildcheck-<name>/backend/` へ複製してビルドする**ため、
複製先では `src/Directory.Build.props` が効く（`.sample` は複製時に削除される）。

**実測**（`dotnet msbuild -getProperty`）:

| 評価した場所 | `NoWarn` | `WarningsAsErrors` | `XUnit1051Migrated` |
| --- | --- | --- | --- |
| `src/.template-buildcheck-unit-template/...`（CI と同じ複製） | **`;xUnit1051`** | `;NU1605;SYSLIB0011` | **見える** |
| `templates/unit-template/...`（その場） | `1701;1702`（SDK 既定） | `;NU1605` | （無い） |

→ **複製先では確かに `src/Directory.Build.props` が届いており**、雛形を勘定に入れる必要が
あるという着手時の指摘は正しかった。その場のビルドでは届かない（`Directory.Build.props.sample`
が単独用のフォールバックとして働く）。

## 対象の母集合

`-p:NoWarn=` 付きで CI と同じ複製をビルドして引いた。**1 箇所**。

| ファイル | 行 | 呼び出し |
| --- | ---: | --- |
| `Integration/HealthEndpointTests.cs` | 17 | `GetAsync("/health")` |

`Unit/CreateSampleHandlerTests.cs` は**同期テストで `await` を持たず**、診断は 0 件（触らない）。
`GlobalUsings.cs` が `global using Xunit;` を持つので `using` の追加は不要。

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を直す（1 箇所）
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true` にし、
   **`deferReason` を削除する**（剥がしたので据え置きの理由が無くなる）
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ追加

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 1 箇所が移行済み | ✅ CI と同じ複製を `-p:NoWarn=` でビルドし **xUnit1051 が 0 件** |
| **再発したらビルドが落ちる**（複製先で） | ✅ 変異試験 M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ 雛形の `[Fact]` は 2 件のまま、実行も 2 件 Passed |
| その場（`templates/`）のビルドを壊さない | ✅ **元から その場ではビルドできない**（後述）。develop と同じ失敗のまま変わらない |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |
| CI の後片付けが効く | ✅ 複製を消した後 `git status --short --ignored -- src/` が空 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | 移行済みの 1 箇所を元へ戻して**複製先で**ビルド | **失敗** | **`error xUnit1051` / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 1` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

🔴 **M-1 は複製先でしか落ちない。雛形の回帰を守っているのは CI の `template-backend-build` だけである。**

🔴 **`templates/` はその場ではビルドできない**（着手時に「単独ビルドは成功する」と書いたが**誤りだった**。
実測で訂正する）。`.sample` が付いたままなので `TargetFramework` が未定義になり、
**変更前の develop でも同じ失敗をする**:

```
$ dotnet build templates/unit-template/backend/backend.slnx    # develop（未変更）でも
error : 無効なフレームワーク識別子 ''。
```

`.sample` は**単独リポジトリとして切り出すときに rename して使う**もの（[[IADR-0060]] 決定 4）。
したがって本リポジトリにおける雛形の**唯一のビルド経路は CI の複製**であり、
そこに `src/Directory.Build.props` が効く。**「その場でも警告が出る」という予想も外れていた** ——
その場ではコンパイルまで到達しないので、警告も出ない。

## 申し送り

- **`Directory.Build.props.sample`（単独ビルド用）には手を入れていない。**
  切り出して `.sample` を rename した先では xUnit1051 は**抑止も昇格もされない**素の警告になる。
  そこは切り出し先リポジトリの裁量であり、本リポジトリの CI の判定には関わらない。
  新規ユニットが `src/` 配下へ配置された時点で本体の props が効く
- 残件は **920 → 919**。次は `Knowledge.IntegrationTests`（実測 4）
