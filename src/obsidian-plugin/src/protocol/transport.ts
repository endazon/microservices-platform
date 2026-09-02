// FR-20, [[IADR-0331]] 決定 6: HTTP の出口をポートにして、プロトコル部を Obsidian 実体なしで
// テストできるようにする。実装は `src/transport/`（Obsidian の requestUrl ／ Node の fetch）の 2 つだけ。

export interface HttpRequest {
  method: 'GET';
  url: string;
  headers: Record<string, string>;
}

interface HttpResponse {
  status: number;
  text: string;
}

export type HttpTransport = (request: HttpRequest) => Promise<HttpResponse>;
