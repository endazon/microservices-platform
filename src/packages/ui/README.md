# `@platform/ui` — 共有 UI パッケージ

2 ユニット（`platform/frontend` / `knowledge/frontend`）と将来の可変ユニットが共用する UI の共通部。
切り出し単位の決定は [IADR-0121](../../../docs/adr/IADR-0121_spa-stack-migration-staging.md) 決定 4、
計画側の根拠は
[ADR-0031](../../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) と
[13_frontend-stack](../../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)。

## 何を入れるか / 入れないか

| 入れる | 入れない |
| --- | --- |
| デザイントークン（Tailwind CSS v4 の `@theme`）と base スタイル | ドメイン語彙（ドキュメント・データソース等） |
| `cn()`（clsx + tailwind-merge） | BFF 通信（`apiFetch` / orval 生成フック） |
| shadcn/ui 派生のプリミティブ（Button / StatusBadge / 以降 Input・Dialog・Table…） | ルーティング・認証・ロール判定・実行時 config |
| アイコン（`lucide-react`。自己ホストバンドル） | 画面固有の複合コンポーネント |

**判定規則**: *「この部品は、このリポジトリの外の SPA へそのまま持って行っても意味が通るか」*。
通るならここへ、通らないなら feature 側へ置く。

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

## 未了（移行第 2 段 / #452）

- shadcn/ui コンポーネントの本移植（Dialog / Table / Form / Input …）。
- ダークテーマのトークン。画面が確定してから追加する。
- Storybook によるカタログ化。
