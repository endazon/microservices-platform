import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-09, FR-09: 管理者設定（ABAC）が属性辞書・ポリシーを一覧／登録／削除でき、保存前検証エラー（矛盾）を
// 表示すること、参照中属性の削除競合（409）を表示することを検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { AdminAbacSettingsPage } from './AdminAbacSettingsPage';

const ATTRS = [
  { id: 'a1', key: 'confidentiality', label: '機密区分', allowedValues: ['public', 'internal'], required: true, scope: 'document' },
];
const POLICIES = [
  { id: 'p1', name: '社内文書は社員が閲覧可', action: 'read', userConditions: {}, documentConditions: { confidentiality: ['internal'] }, isActive: true },
];

// 一覧ロード（attributes, policies の順）を既定で返す。
function primeLoad(attrs = ATTRS, pols = POLICIES) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/admin/authz/attributes') return attrs;
    if (path === '/admin/authz/policies') return pols;
    return undefined;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('AdminAbacSettingsPage (SC-09)', () => {
  it('lists attribute definitions and policies', async () => {
    primeLoad();
    render(<AdminAbacSettingsPage />);

    expect(await screen.findByRole('cell', { name: 'confidentiality' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: '社内文書は社員が閲覧可' })).toBeInTheDocument();
  });

  it('creates an attribute with parsed allowed values', async () => {
    primeLoad();
    const user = userEvent.setup();
    render(<AdminAbacSettingsPage />);
    await screen.findByRole('cell', { name: 'confidentiality' });

    const form = screen.getByRole('form', { name: '属性辞書登録' });
    await user.type(within(form).getByLabelText('キー（必須）'), 'department');
    await user.type(within(form).getByLabelText('ラベル'), '部門');
    await user.type(within(form).getByLabelText('許可値（カンマ区切り）'), 'hr, sales');
    await user.click(within(form).getByRole('button', { name: '追加する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/authz/attributes', {
        method: 'POST',
        json: { key: 'department', label: '部門', allowedValues: ['hr', 'sales'], required: false, scope: 'document' },
      }),
    );
  });

  it('shows server-side validation errors (policy contradiction) on save', async () => {
    primeLoad();
    // ポリシー登録だけ 400（矛盾検証エラー）を返す。
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/admin/authz/attributes' && !req) return ATTRS;
      if (path === '/admin/authz/policies' && (!req || req.method !== 'POST')) return POLICIES;
      if (path === '/admin/authz/policies' && req?.method === 'POST') {
        throw new ApiError('validation', '入力内容に誤りがあります。', 400, ['action と条件が矛盾しています。']);
      }
      return undefined;
    });
    const user = userEvent.setup();
    render(<AdminAbacSettingsPage />);
    await screen.findByRole('cell', { name: 'confidentiality' });

    const form = screen.getByRole('form', { name: 'ポリシー登録' });
    await user.type(within(form).getByLabelText('名前（必須）'), '矛盾ポリシー');
    await user.click(within(form).getByRole('button', { name: '追加する' }));

    expect(await screen.findByText('action と条件が矛盾しています。')).toBeInTheDocument();
  });

  it('rejects malformed condition JSON locally before calling the API', async () => {
    primeLoad();
    const user = userEvent.setup();
    render(<AdminAbacSettingsPage />);
    await screen.findByRole('cell', { name: 'confidentiality' });

    const form = screen.getByRole('form', { name: 'ポリシー登録' });
    await user.type(within(form).getByLabelText('名前（必須）'), 'p');
    const userCond = within(form).getByLabelText('利用者条件（JSON）');
    await user.clear(userCond);
    await user.type(userCond, 'not-json');
    await user.click(within(form).getByRole('button', { name: '追加する' }));

    expect(await screen.findByText(/正しい JSON/)).toBeInTheDocument();
    // ローカル検証で弾くため POST は呼ばれない。
    expect(mocks.apiFetch).not.toHaveBeenCalledWith('/admin/authz/policies', expect.objectContaining({ method: 'POST' }));
  });

  it('shows a 409 conflict message when deleting a referenced attribute', async () => {
    primeLoad();
    mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
      if (path === '/admin/authz/attributes' && !req) return ATTRS;
      if (path === '/admin/authz/policies' && !req) return POLICIES;
      if (path === '/admin/authz/attributes/a1' && req?.method === 'DELETE') {
        throw new ApiError('conflict', '競合が発生しました。', 409, ['ポリシー「社内文書は社員が閲覧可」が参照中です。']);
      }
      return undefined;
    });
    const user = userEvent.setup();
    render(<AdminAbacSettingsPage />);

    const attrSection = within(await screen.findByRole('region', { name: '属性辞書' }));
    await user.click(attrSection.getByRole('button', { name: '削除' }));

    expect(await screen.findByText(/参照中です/)).toBeInTheDocument();
  });
});
