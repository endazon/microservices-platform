import { describe, it, expect } from 'vitest';
import { ApiError } from '@foundation/api/ApiError';
import { toMessages } from './apiErrors';

// Issue #183 / IADR-0040: ApiError→表示メッセージ写像の優先順位（details → message → fallback）を検証する。

describe('toMessages', () => {
  it('returns ApiError.details when present（最も具体的な詳細を優先）', () => {
    const err = new ApiError('validation', '入力内容に誤りがあります。', 400, ['タイトルは必須です。', 'X が不正です。']);
    expect(toMessages(err, 'fallback')).toEqual(['タイトルは必須です。', 'X が不正です。']);
  });

  it('falls back to ApiError.message when details is empty', () => {
    const err = new ApiError('conflict', '競合が発生しました。', 409, []);
    expect(toMessages(err, 'fallback')).toEqual(['競合が発生しました。']);
  });

  it('returns fallback for non-ApiError', () => {
    expect(toMessages(new Error('boom'), '既定文言')).toEqual(['既定文言']);
    expect(toMessages('str', '既定文言')).toEqual(['既定文言']);
  });
});
