import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Alert } from './Alert';

// INDEX 決定 21 / IADR-0125 決定 1: 「色だけで意味を持たせない」の回帰。
// StatusBadge・notify と同じ作法で、Alert も tone ごとに固定のアイコンと必須のテキストラベルを持つ。
describe('Alert（色だけで意味を持たせない）', () => {
  it.each(['info', 'success', 'warning', 'danger'] as const)(
    'tone=%s でテキストラベルとアイコンの両方を描画する',
    (tone) => {
      const { container } = render(
        <Alert tone={tone} label="注意">
          同期に失敗しました。
        </Alert>,
      );

      expect(screen.getByText('注意')).toBeInTheDocument();
      expect(screen.getByText('同期に失敗しました。')).toBeInTheDocument();
      const icon = container.querySelector('svg');
      expect(icon).not.toBeNull();
      // アイコンは装飾。意味はラベルと本文が担う。
      expect(icon).toHaveAttribute('aria-hidden', 'true');
    },
  );

  it('tone ごとに異なるアイコンを使う（モノクロでも区別できる）', () => {
    const { container: warn } = render(
      <Alert tone="warning" label="注意">
        本文
      </Alert>,
    );
    const { container: err } = render(
      <Alert tone="danger" label="エラー">
        本文
      </Alert>,
    );
    expect(warn.querySelector('svg')?.outerHTML).not.toBe(err.querySelector('svg')?.outerHTML);
  });

  it('role は既定で付けず、呼び出し側が選ぶ（静的な注記と通知でライブリージョンが異なる）', () => {
    const { container } = render(<Alert label="情報">認証情報は Vault 管理です。</Alert>);
    expect(container.querySelector('[role]')).toBeNull();

    render(
      <Alert tone="danger" label="エラー" role="alert">
        保存に失敗しました。
      </Alert>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('保存に失敗しました。');
  });

  it('既定の tone は info', () => {
    render(<Alert label="情報">本文</Alert>);
    expect(screen.getByText('情報')).toBeInTheDocument();
  });
});
