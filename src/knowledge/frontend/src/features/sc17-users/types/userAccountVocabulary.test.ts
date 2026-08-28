import { describe, it, expect } from 'vitest';
import type {
  AttributeDefinitionDto,
  PlatformUserDto,
} from '@foundation/api/generated/bff.schemas';
import {
  REQUIRED_ATTRIBUTE_KEYS,
  assignableAttributes,
  departmentsInUse,
  filterUsers,
  optionalAttributes,
  requiredAttributes,
  validateAssignment,
} from './userAccountVocabulary';

// SC-17, UC-05, FR-05, FR-09 (#452): 入力規則と値域の判定を**描画なしで**固定する
// （IADR-0129 決定 6: 画面テストは値集合の欠落を捕まえない）。

const definitions: AttributeDefinitionDto[] = [
  {
    id: 'a1',
    key: 'department',
    label: '部門',
    allowedValues: ['engineering', 'finance'],
    required: false,
    scope: 'user',
    createdAt: '',
    updatedAt: '',
  },
  {
    id: 'a2',
    key: 'clearance',
    label: '機密区分上限',
    allowedValues: ['internal', 'restricted'],
    required: false,
    scope: 'user',
    createdAt: '',
    updatedAt: '',
  },
  {
    id: 'a3',
    key: 'tags',
    label: 'タグ',
    allowedValues: ['経理'],
    required: false,
    scope: 'user',
    createdAt: '',
    updatedAt: '',
  },
  // 文書スコープ・許可値ゼロは選択肢に出さない。
  {
    id: 'a4',
    key: 'doc_scope',
    label: '文書区分',
    allowedValues: ['organization'],
    required: false,
    scope: 'document',
    createdAt: '',
    updatedAt: '',
  },
  {
    id: 'a5',
    key: 'projects',
    label: 'プロジェクト',
    allowedValues: [],
    required: false,
    scope: 'user',
    createdAt: '',
    updatedAt: '',
  },
];

const users: PlatformUserDto[] = [
  {
    id: 'u1',
    username: 'a',
    displayName: 'A',
    enabled: true,
    roles: ['platform-operator'],
    attributes: { department: 'finance', clearance: 'internal' },
  },
  {
    id: 'u2',
    username: 'b',
    displayName: 'B',
    enabled: true,
    roles: ['platform-admin', 'platform-operator'],
    attributes: { department: 'engineering', clearance: 'restricted' },
  },
  { id: 'u3', username: 'c', displayName: 'C', enabled: false, roles: [], attributes: {} },
];

describe('userAccountVocabulary (SC-17)', () => {
  it('offers only user-scoped dictionary entries that have allowed values', () => {
    expect(assignableAttributes(definitions).map((d) => d.key)).toEqual([
      'department',
      'clearance',
      'tags',
    ]);
  });

  // 計画の「部門・機密区分上限は必須／タグは任意」を、必須集合とその補集合で表す。
  it('splits the dictionary into the required pair and everything else', () => {
    expect(requiredAttributes(definitions).map((d) => d.key)).toEqual(['department', 'clearance']);
    expect(optionalAttributes(definitions).map((d) => d.key)).toEqual(['tags']);
    expect([...REQUIRED_ATTRIBUTE_KEYS]).toEqual(['department', 'clearance']);
  });

  it('filters by department, by role, and by both (AND)', () => {
    expect(filterUsers(users, { department: 'finance', role: '' }).map((u) => u.id)).toEqual([
      'u1',
    ]);
    expect(filterUsers(users, { department: '', role: 'platform-admin' }).map((u) => u.id)).toEqual(
      ['u2'],
    );
    expect(filterUsers(users, { department: 'finance', role: 'platform-admin' })).toEqual([]);
    // 陽性対照: 条件なしは素通し（絞り込みが常に空を返す実装を落とす）。
    expect(filterUsers(users, { department: '', role: '' })).toHaveLength(3);
  });

  it('derives the department filter options from the data, not from the dictionary', () => {
    // 辞書に在っても誰も所属していない部門（'hr' 等）は出さない。属性を持たない利用者も数えない。
    expect(departmentsInUse(users)).toEqual(['engineering', 'finance']);
  });

  it('requires at least one role', () => {
    expect(
      validateAssignment({
        roles: [],
        attributes: { department: 'finance', clearance: 'internal' },
        definitions,
      }),
    ).toEqual(['roles-required']);
  });

  it('requires department and clearance but not the optional tag', () => {
    expect(
      validateAssignment({
        roles: ['platform-admin'],
        attributes: { department: 'finance' },
        definitions,
      }),
    ).toEqual(['required-attribute-missing']);
    // 🔴 過剰拒否の否定側: タグ無しは妥当である。
    expect(
      validateAssignment({
        roles: ['platform-admin'],
        attributes: { department: 'finance', clearance: 'internal' },
        definitions,
      }),
    ).toEqual([]);
  });

  it('does not blame the operator for a required key the dictionary never defined', () => {
    // 辞書が未整備なら画面に入力欄が出ない。出せない項目を「未入力」と責めない
    // （辞書側の未整備は後段が 400 で述べる）。
    const partial = definitions.filter((d) => d.key !== 'clearance');
    expect(
      validateAssignment({
        roles: ['platform-admin'],
        attributes: { department: 'finance' },
        definitions: partial,
      }),
    ).toEqual([]);
  });
});
