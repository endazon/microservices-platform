import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { Monitor, Moon, Sun } from 'lucide-react';
import type { ComponentType } from 'react';
import { Button } from '@platform/ui';
import { nextTheme, type ThemeChoice } from '../../lib/theme/theme';
import { useTheme } from '../../lib/theme/useTheme';

// ADR-0031 / 利用者裁定（2026-09-12）: 共通シェルのヘッダに置くテーマ切替。
// **3 状態を 1 つのボタンが巡回する**（`system` → `system` の反転 → もう一方 → `system`）。
//
// INDEX 決定 21「色だけで意味を持たせない」の敷衍として、**アイコンだけのボタンにしない**。
// 現在のテーマは「アイコン ＋ 見える文言」の 2 つで示す——月や太陽の絵柄は文化・習慣に
// 依存する手掛かりであり、それ単独では「今どちらなのか」も「押すとどうなるか」も伝わらない。
//
// 文言は `@platform/ui` ではなくここが持つ（プリミティブは文言を持たない。IADR-0125 決定 1）。
//
// **`Layout.tsx` への設置と `main.tsx` の起動時適用は本ファイルの担当ではない**
// （起動時適用の入口は `lib/theme/theme.ts` の `applyStoredTheme()`）。

/** 巡回の 3 状態とアイコン。`Record` が網羅を強制する（状態を増やせばここも必ず埋まる）。 */
const CHOICE_ICONS: Record<
  ThemeChoice,
  ComponentType<{ className?: string; 'aria-hidden'?: boolean }>
> = {
  system: Monitor,
  light: Sun,
  dark: Moon,
};

/** ボタンに見えている文言。 */
const CHOICE_LABELS: Record<ThemeChoice, MessageDescriptor> = {
  system: msg`表示: システム`,
  light: msg`表示: ライト`,
  dark: msg`表示: ダーク`,
};

/** 読み上げ用の短い名前（`aria-label` の中で「現在」「押した後」を並べるのに使う）。 */
const CHOICE_NAMES: Record<ThemeChoice, MessageDescriptor> = {
  system: msg`システム`,
  light: msg`ライト`,
  dark: msg`ダーク`,
};

export function ThemeToggle() {
  const { choice, prefersDark, cycleTheme } = useTheme();
  const Icon = CHOICE_ICONS[choice];
  const upcoming = nextTheme(choice, prefersDark);
  // マクロへ渡す式は**単純な識別子に限る**（`lingui/no-expression-in-message`）。
  // 翻訳済みの名前を先に変数へ落としてから埋める。
  const currentName = i18n._(CHOICE_NAMES[choice]);
  const upcomingName = i18n._(CHOICE_NAMES[upcoming]);

  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={cycleTheme}
      // **押した後どうなるかまで読み上げる。** 巡回ボタンは押すまで次が分からないため、
      // 名前が現在の状態だけだと「押してよいのか」を判断できない。
      aria-label={i18n._(
        msg`表示テーマの切替。現在は ${currentName}、押すと ${upcomingName} になります`,
      )}
    >
      {/* アイコンは装飾。意味は隣の文言が担う。 */}
      <Icon className="size-4" aria-hidden />
      <span>{i18n._(CHOICE_LABELS[choice])}</span>
    </Button>
  );
}
