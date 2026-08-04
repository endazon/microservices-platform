import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { Notifications, notify, notificationLabel } from './notifications';

// INDEX 決定 21 / IADR-0124 決定 7: 通知は「色だけで意味を持たせない」。
// severity ごとにテキストのラベルが必ず描画されることを固定する（色を除いても意味が成り立つこと）。

afterEach(() => cleanup());

describe('notifications (INDEX 決定 21: 色だけで意味を持たせない)', () => {
  it.each(['success', 'info', 'warning', 'error'] as const)(
    'renders a text label for %s',
    async (severity) => {
      render(<Notifications />);
      notify[severity](`${severity} のメッセージ`);
      expect(await screen.findByText(notificationLabel(severity))).toBeInTheDocument();
      expect(screen.getByText(`${severity} のメッセージ`)).toBeInTheDocument();
    },
  );

  it('exposes exactly the four severities (no colour-only variant can be added by callers)', () => {
    expect(Object.keys(notify).sort()).toEqual(['error', 'info', 'success', 'warning']);
    expect(
      (['success', 'info', 'warning', 'error'] as const).every((s) => notificationLabel(s).length > 0),
    ).toBe(true);
  });
});
