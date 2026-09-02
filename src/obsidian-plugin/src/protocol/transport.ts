// FR-20, [[IADR-0338]] 決定 6: HTTP の出口をポートにして、プロトコル部を Obsidian 実体なしで
// テストできるようにする。実装は `src/transport/`（Obsidian の requestUrl ／ Node の fetch）の 2 つだけ。
// [[IADR-0352]]: 第 2 段で POST（push / delete）を運ぶため method と body を足した。

export interface HttpRequest {
  method: 'GET' | 'POST';
  url: string;
  headers: Record<string, string>;
  /** JSON 文字列。GET では undefined。 */
  body?: string;
}

interface HttpResponse {
  status: number;
  text: string;
}

export type HttpTransport = (request: HttpRequest) => Promise<HttpResponse>;
