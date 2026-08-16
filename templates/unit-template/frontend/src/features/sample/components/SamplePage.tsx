import { Trans } from '@lingui/react/macro';
import { Alert, Card, CardContent, CardHeader, CardTitle, Input, Label } from '@platform/ui';
import { useSampleList } from '../api/useSampleList';
import { useSampleFilter } from '../hooks/useSampleFilter';

// テンプレート: feature の画面。計画 13_frontend-stack §ディレクトリ構成 の `components/`。
//
// 守るべき点（雛形が体現しているもの）:
// - **UI プリミティブは `@platform/ui` から取る**（Tailwind v4 ＋ shadcn/ui 派生。IADR-0121 決定 4 /
//   IADR-0125 決定 1）。ユニット側で同等品を作り直さない。深い参照（`@platform/ui/src/...`）は
//   ESLint が禁止する。
// - **表示文言は Lingui のマクロで書く**（ADR-0031 = ja / en・コンパイル時抽出）。カタログは
//   `pnpm run i18n` で再生成し、未翻訳キーは `check-i18n-catalogs.js` が止める（IADR-0125 決定 4）。
// - **状態を色だけで表さない**（色 ＋ アイコン ＋ テキスト。INDEX 決定 21）。`Alert` は `label` を
//   必須にすることでこれを型で強制する——省略できる API にすると必ず省略されるため。
// - **外部 CDN・Web フォント・analytics を使わない**（08_data-egress-policy。アイコンは npm 同梱の
//   lucide-react、フォントはシステムフォント）。ビルド成果物を `check-static-egress.js` が走査する。

export function SamplePage() {
  const { filter, setKeyword } = useSampleFilter();
  const { data, isPending, isError } = useSampleList(filter);

  return (
    <section>
      <h1>
        <Trans>サンプル</Trans>
      </h1>

      <Label htmlFor="sample-keyword">
        <Trans>キーワード</Trans>
      </Label>
      <Input
        id="sample-keyword"
        value={filter.keyword}
        onChange={(e) => setKeyword(e.target.value)}
      />

      {isError ? (
        // 操作の結果として現れるため role="alert" を選ぶ（既定では付かない。呼び出し側の責務）。
        <Alert tone="danger" label={<Trans>エラー</Trans>} role="alert">
          <Trans>一覧を取得できませんでした。</Trans>
        </Alert>
      ) : isPending ? (
        <p>
          <Trans>読み込み中です。</Trans>
        </p>
      ) : data.length === 0 ? (
        // IADR-0009（存在秘匿）: 「権限が無い」と「存在しない」を UI で区別しない中立表示。
        <p>
          <Trans>該当するものはありません。</Trans>
        </p>
      ) : (
        <ul>
          {data.map((item) => (
            <li key={item.id}>
              <Card>
                <CardHeader>
                  <CardTitle>{item.title}</CardTitle>
                </CardHeader>
                <CardContent>{item.id}</CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
