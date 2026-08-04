# `@platform/ui` — 共有 UI パッケージ

2 ユニット（`platform/frontend` / `knowledge/frontend`）と将来の可変ユニットが共用する UI の共通部。
切り出し単位の決定は [IADR-0121](../../../docs/adr/IADR-0121_spa-stack-migration-staging.md) 決定 4 と
[IADR-0125](../../../docs/adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 1・2、
計画側の根拠は
[ADR-0031](../../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) と
[13_frontend-stack](../../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)。

## 何を入れるか / 入れないか

| 入れる | 入れない |
| --- | --- |
| デザイントークン（Tailwind CSS v4 の `@theme`）と base スタイル | ドメイン語彙（ドキュメント・データソース等） |
| `cn()`（clsx + tailwind-merge） | BFF 通信（`apiFetch` / orval 生成フック） |
| shadcn/ui 派生のプリミティブ（下表） | ルーティング・認証・ロール判定・実行時 config |
| アイコン（`lucide-react`。自己ホストバンドル） | 画面固有の複合コンポーネント |
| — | **表示文言**（IADR-0125 決定 1。持つと i18n の入口が 2 つに割れる） |

**判定規則**: *「この部品は、このリポジトリの外の SPA へそのまま持って行っても意味が通るか」*。
通るならここへ、通らないなら feature 側へ置く。

## 収録しているプリミティブ

移植の根拠（計画のどこが要求しているか）は
[#496 作業仕様書 §1](../../../docs/specs/20260804_issue-496_ui-i18n-storybook.md) の選定表を正とする。

| 部品 | 主な用途（計画上の根拠） |
| --- | --- |
| `Button` | 全画面 |
| `StatusBadge` | 状態表示（SC-06 同期状態・SC-07 ジョブ状態）。色 ＋ アイコン ＋ テキストを型で強制 |
| `Input` / `Textarea` / `Select` / `Label` | SC-01 質問入力・SC-02 検索ボックス・SC-05 タイトル / 機密区分・SC-08 分析内容・SC-09 対象属性 |
| `Table` 一式（`Table` / `TableCaption` / `TableHead` / `TableBody` / `TableRow` / `TableHeaderCell` / `TableCell`） | SC-02 結果テーブル・SC-05 文書一覧・SC-06 ソース一覧・SC-07 ジョブ一覧 |
| `Card` 一式（`Card` / `CardHeader` / `CardTitle` / `CardContent`） | SC-03 属性・タグ / バージョン履歴パネル・SC-08 結果パネル・SC-10 統計（モックの `panel`） |
| `Alert` | SC-05 / SC-06 の注記・SC-06 同期異常の警告（琥珀）（モックの `note` / `warn` / `err`）。色 ＋ アイコン ＋ テキストを型で強制 |
| `Tabs` 一式 | SC-09 属性体系 / タグ辞書 / 辺の型 / ポリシー定義（モックの `seg`） |

**`Select` はネイティブ `<select>`、`Tabs` は `@radix-ui/react-tabs`** である（IADR-0125 決定 1）。
前者は「定義済みの値を選ぶ」以上のことを計画が求めていないため、後者はロービングタブインデックスと
`aria-*` の整合を自前で書くと誤りやすいためである。

## 使い方

```ts
// 各ユニットの SPA から
import { Button, StatusBadge, cn } from '@platform/ui';
```

```ts
// アプリのエントリで 1 度だけ（platform/frontend/src/main.tsx）
import '@platform/ui/styles.css';
```

深い参照（`@platform/ui/src/...`）は ESLint で禁止している。公開面は `src/index.ts` と
`src/styles.css` の 2 つだけである。

## 制約

- **外部 CDN・Web フォント・analytics を使わない**（[08_data-egress-policy](../../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)）。
  フォントは OS のシステムフォントスタック、アイコンは npm パッケージ同梱のものを使う。
- **色だけで意味を持たせない**（INDEX 決定 21）。状態表現は色 ＋ アイコン ＋ テキストの 3 点セットにする。
  `StatusBadge` はこれを API で強制する実例である（テキストは必須、アイコンは tone ごとに固定）。

## カタログ（Storybook）

```bash
pnpm --filter @platform/ui run storybook        # 開発サーバ（http://localhost:6006）
pnpm --filter @platform/ui run build-storybook  # 静的ビルド（storybook-static/。gitignore 済み）
```

テレメトリとクラッシュレポートの外部送信は `.storybook/main.ts` で無効化している
（[08_data-egress-policy](../../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)
§非LLM外部送信の統制「既定テレメトリをオプトアウトする」）。**設定だけに頼らない**——
ビルド成果物に外部オリジンへの参照が無いことを
[`scripts/check-static-egress.js`](../../../scripts/check-static-egress.js) が機械検査する。

```bash
node scripts/check-static-egress.js --require src/packages/ui/storybook-static
```

## 未了（引き受け先を明記する）

- **`Dialog` の移植 → #452**（FR-19 / FR-20 の着手保留が解けたあと）。計画が確認ダイアログを要求するのは
  SC-19 / SC-20 だけであり、両画面は [IADR-0119](../../../docs/adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)
  決定 1 が着手を保留している。**繰り延べであって放棄ではない**（IADR-0125 決定 2）。
- **ダークテーマのトークン → #452**。画面が確定してから追加する。
- **プリミティブの画面への適用 → #452**。本パッケージの利用者は現時点で Storybook と単体テストだけである。
