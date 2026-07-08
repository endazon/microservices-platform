import { describe, it, expect, vi, afterEach } from 'vitest';
import { parseSseBlock, apiStream } from './apiClient';

vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ bffBaseUrl: 'http://test' }),
}));

// IADR-0037, SC-01: SSE イベントブロックの解析（event 名・data 連結・非データ行の無視）。
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

// SC-01: ヘッダ受信前に中断した場合、apiStream は AbortError を保持して再スローする
// （ネットワークエラーへ丸めない）。呼び出し側が連投質問の意図的中断を無視できるようにするため。
describe('apiStream abort handling', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('rethrows AbortError from the initial fetch instead of wrapping it', async () => {
    const abortErr = new DOMException('aborted', 'AbortError');
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(abortErr));

    await expect(apiStream('/analysis/ask/stream', { json: {} }, () => {})).rejects.toBe(
      abortErr,
    );
  });

  it('wraps non-abort fetch failures into a network ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('failed to fetch')));

    await expect(
      apiStream('/analysis/ask/stream', { json: {} }, () => {}),
    ).rejects.toMatchObject({ kind: 'network' });
  });
});
