import { afterEach, describe, expect, it } from 'vitest';
import type { MessageDescriptor } from '@lingui/core';
import { PlatformRole } from '@foundation/auth/roles';
import { activate, i18n, roleLabel } from './index';

// SC-17（#2214）: ロールの識別子 → 計画の表示名（05_screens §SC-17 主要素・計画 ADR-0026）。

const translate = (d: MessageDescriptor) => i18n._(d);

afterEach(() => {
  activate('ja');
});

describe('roleLabel（SC-17 のロールの表示名）', () => {
  it.each([
    [PlatformRole.Admin, 'システム管理者'],
    [PlatformRole.Operator, '運用者'],
  ])('%s を計画の表示名「%s」で出す', (value, label) => {
    expect(roleLabel(value, translate)).toBe(label);
  });

  // 値集合の外は生値のまま（知らない識別子を既知の名前へ丸めない）。
  // `toString` 等の継承プロパティ名も表に無い値として扱う（`Object.hasOwn`）。
  it.each(['wiki-editor', 'trading-owner', 'platform-user', '', 'toString', 'PLATFORM-ADMIN'])(
    '値集合の外の %j は生値のまま出す',
    (value) => {
      expect(roleLabel(value, translate)).toBe(value);
    },
  );

  // 訳は描画時に引く（ロケールの切替に追随する）。
  it('en ロケールでは英訳を出す', () => {
    activate('en');
    expect(roleLabel(PlatformRole.Admin, translate)).toBe('System administrator');
    expect(roleLabel(PlatformRole.Operator, translate)).toBe('Operator');
  });
});
