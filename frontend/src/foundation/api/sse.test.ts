import { describe, it, expect } from 'vitest';
import { parseSseBlock } from './apiClient';

// IADR-0036, SC-01: SSE イベントブロックの解析（event 名・data 連結・非データ行の無視）。
describe('parseSseBlock', () => {
  it('parses event name and data', () => {
    expect(parseSseBlock('event: token\ndata: {"text":"hi"}')).toEqual({
      event: 'token',
      data: '{"text":"hi"}',
    });
  });

  it('defaults event to "message" when only data is present', () => {
    expect(parseSseBlock('data: hello')).toEqual({ event: 'message', data: 'hello' });
  });

  it('joins multi-line data with newlines', () => {
    expect(parseSseBlock('event: x\ndata: a\ndata: b')).toEqual({ event: 'x', data: 'a\nb' });
  });

  it('returns null for blocks without a data field (comments/empty)', () => {
    expect(parseSseBlock(': keep-alive')).toBeNull();
    expect(parseSseBlock('')).toBeNull();
  });
});
