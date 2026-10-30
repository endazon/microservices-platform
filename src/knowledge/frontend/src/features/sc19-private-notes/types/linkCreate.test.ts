import { describe, it, expect } from 'vitest';
import { ApiError } from '@foundation/api/ApiError';
import { linkFailureOf } from './linkCreate';

// FR-17, SC-19, ADR-0034 決定 8 (#2343): リンク作成の失敗の分類。
describe('linkFailureOf', () => {
  it('404 は理由を問わず notFound（存在秘匿）', () => {
    expect(linkFailureOf(new ApiError('notFound', 'x', 404))).toBe('notFound');
  });

  it('409 edge_exists と 400 unknown_edge_type を本文で言い分ける', () => {
    expect(linkFailureOf(new ApiError('conflict', 'x', 409, [], { error: 'edge_exists' }))).toBe(
      'exists',
    );
    expect(
      linkFailureOf(new ApiError('validation', 'x', 400, [], { error: 'unknown_edge_type' })),
    ).toBe('unknownType');
  });

  it('状態コードだけでは言い分けない（本文の印が無ければ other）', () => {
    expect(linkFailureOf(new ApiError('validation', 'x', 400, [], { error: 'self_edge' }))).toBe(
      'other',
    );
    expect(linkFailureOf(new ApiError('conflict', 'x', 409))).toBe('other');
    expect(linkFailureOf(new Error('boom'))).toBe('other');
  });
});
