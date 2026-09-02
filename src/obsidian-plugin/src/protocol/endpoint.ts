// FR-20, 08_data-egress-policy 許容条件 2・3, [[IADR-0338]] 決定 4: 接続先 URL の正規化。
//
// 同期トークンは Bearer で平文のまま載るので、**https 以外では送らない**。例外は loopback
// （port-forward で叩くローカル検証）だけである。末尾の `/` は落とし、`/private-notes/sync/...`
// をそのまま連結できる形にそろえる。

export class EndpointError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'EndpointError';
  }
}

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

export function normalizeEndpoint(raw: string): string {
  const trimmed = raw.trim();
  if (trimmed === '') throw new EndpointError('接続先 URL が未設定です。');

  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    throw new EndpointError(`接続先 URL の形が不正です: ${trimmed}`);
  }

  if (url.protocol === 'http:' && !LOOPBACK_HOSTS.has(url.hostname)) {
    throw new EndpointError(
      '接続先は https でなければなりません（http を許すのは localhost / 127.0.0.1 だけです）。',
    );
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    throw new EndpointError(`接続先のスキームが不正です: ${url.protocol}`);
  }
  if (url.search !== '' || url.hash !== '') {
    throw new EndpointError('接続先 URL にクエリやフラグメントは付けられません。');
  }

  const path = url.pathname.replace(/\/+$/, '');
  return `${url.origin}${path}`;
}
