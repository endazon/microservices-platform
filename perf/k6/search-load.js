// NFR (#196): 横断検索の負荷試験（k6）。SLO: 検索 p95 ≤ 1.5s。
// 実行例:
//   BASE_URL=http://localhost:5000 TOKEN=<jwt> k6 run perf/k6/search-load.js
//   （または KC_TOKEN_URL/KC_USERNAME/KC_PASSWORD で Keycloak パスワードグラント）
// 閾値（thresholds）を満たさない場合 k6 は非ゼロ終了する（CI/ゲート化に利用可能）。

import http from 'k6/http';
import { check, sleep } from 'k6';
import { BASE_URL, obtainToken, authHeaders, SEARCH_QUERIES } from './lib/config.js';

export const options = {
  scenarios: {
    // 段階的に VU を増やし定常負荷をかける（小〜中規模想定）。実測時に規模へ合わせて調整する。
    search: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '30s', target: 10 },
        { duration: '2m', target: 25 },
        { duration: '30s', target: 0 },
      ],
      exec: 'search',
    },
  },
  thresholds: {
    // NFR: 検索 p95 ≤ 1.5s。ラベルでシナリオを絞る。
    'http_req_duration{scenario:search}': ['p(95)<1500'],
    'http_req_failed{scenario:search}': ['rate<0.01'],
  },
};

export function setup() {
  return { token: obtainToken() };
}

export function search(data) {
  const query = SEARCH_QUERIES[Math.floor(Math.random() * SEARCH_QUERIES.length)];
  const res = http.post(
    `${BASE_URL}/bff/search`,
    JSON.stringify({ query, topK: 10 }),
    Object.assign({ tags: { scenario: 'search' } }, authHeaders(data.token)),
  );
  check(res, {
    'status is 200': (r) => r.status === 200,
  });
  sleep(1); // 利用者の思考時間（think time）を模す
}
