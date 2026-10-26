import type { ReactNode } from 'react';
import { i18n } from '@lingui/core';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useParams, useSearch } from '@tanstack/react-router';
import {
  Alert,
  EmptyState,
  Kv,
  KvItem,
  Panel,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
// SC-03, UC-07, #1200 / IADR-0365 決定 1: 「Wiki で閲覧」は**権限内の Wiki 台帳にこの文書が載っているとき**だけ出す
// （実行時 config `wikiBaseUrl` の有無で出し分ける形は廃止。stg/prod では同値が供給されないため一度も出なかった）。
import { useWikiPageIndex } from '../../../lib/wiki-pages';
// SC-03 / #446: 共通シェルのパンくずの**動的な葉**（文書タイトル）を渡す。
import { useBreadcrumbLeaf } from '@foundation/routing/breadcrumbLeaf';
// ADR-0031 §採用技術一覧（日付 = dayjs）/ #788: 同じ整形を自前で持っていたが、
// **同じ整形規則を 2 か所に置かない**ため foundation の 1 本へ寄せた。
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { confidentialityLabel } from '@foundation/i18n';
import { CONFIDENTIALITY_KEY } from '../../../lib/abac';
import { attributeLabel, orderedAttributes } from '../types/attributes';
import { isNotFound, useDocumentQueries } from '../api/useDocumentQueries';
// SC-03, FR-18 (#450): AI 提案の承認欄。**承認の主導線は本画面である**（SC-21 は棚卸しの従）。
import { AiSuggestionPanel } from './AiSuggestionPanel';
// SC-03 §個人資料の表示, FR-19, 計画 ADR-0102 / [[IADR-0451]] (#1455): 個人資料のときだけ描く欄。
import { PrivateNoteSummary } from './PrivateNoteSummary';
// SC-03, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type {
  DocumentContentDto,
  DocumentDto,
  DocumentVersionDto,
} from '@foundation/api/generated/bff.schemas';
import type { UseQueryResult } from '@tanstack/react-query';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細／プレビュー（05_screens: ルート /docs/:id）。
// 正規化文書（Markdown）本文と属性・タグ・版履歴を表示し、出典元・Wiki（SC-04）への導線を提供する。
// ABAC はサーバ側で適用され、権限外・不在はいずれも 404（存在秘匿・IADR-0009）→ UI は中立に表示する。
//
// **［2026-09-12 / UI/UX 改善］hi-fi モック（sc-03）の 2 カラム構造へ寄せた。**
//   左（`flex-[1.7]`）= `.ttl` ＋ `.sub` ＋ 本文の `Panel` ＋ 導線の行 ＋ AI 提案の `Panel`、
//   右（`flex-1`）= 属性・タグの `Kv` ＋ 版履歴の `Table`。`Card` は**カード（一覧に並ぶ独立した単位）**
//   の部品であり、本文の**区画**はモックの `.panel`＝`Panel` である。
//   待ち・失敗は `QueryState` へ寄せた。**ただし 404 だけは `QueryState` の手前で分ける**——
//   404 は「無い」であって「失敗」ではなく、`ErrorState`（`role="alert"` ＋ 再試行）で出すと
//   **存在秘匿が壊れ**、押しても必ず同じ 404 になる再試行を押させることになる（SC-18 と同じ作法）。
//
// SC-03, FR-17, SC-18 (#1240): **計画が本画面へ置くと定めた 2 つは、これで両方とも揃った。**
// 05_screens §SC-03「知識グラフ」（2026-08-02 の利用者裁定）は
// 「**SC-03 に置くのは次の 2 つのみである**: ①SC-18 への導線、②AI 提案の承認欄」と確定している。
// ②は #450（[[IADR-0300]]）で、①は本変更（[[IADR-0386]]）で着地した。
//
// 🔴 **これは「忘れていたものを思い出した」のではない。** IADR-0119 決定 1 が
// 「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」と定め、
// 決定 2 の着手条件（前提 ADR の Accepted 化）が満たされるまで繰り延べていた。
// 条件は **2026-08-07（#586）に成立**し（ADR-0033 / 0034 / 0035 が Accepted へ移った）、
// 繰り延べの相手だった SC-18 の画面も **#917 で着地**した。**両方が揃ってなお導線だけが
// 残っていたのは、解除を持つ者が居なかったからである**（判断先に名指しされていた #504 が
// 判断を残さずに閉じた）。同型を繰り返さないため、**保留を書くときは解除の条件だけでなく
// 解除を実行する主体まで書く**（[[IADR-0386]] 決定 3）。
//
// 🔴 **併置しないものは併置しない。** 同じ裁定が「バックリンク欄・ローカルグラフは
// **Wiki.js 側（SC-04）のみ**に置き、SC-03 には併置しない」と定めている。
// 不在は `DocumentDetailPage.test.tsx` と `e2e/sc03-document.smoke.spec.ts` が固定する
// （**併置しないことは恒久の決定ではない**ので、足したときに落ちて気づける形にしておく）。

export function DocumentDetailPage() {
  const { t } = useLingui();
  // SC-03, IADR-0124 決定 3: パスパラメータはルート ID のリテラルを渡す形だけが厳密に型付く。
  const { id } = useParams({ from: '/_shell/docs/$id' });
  // FR-19, planning#841 裁定 2 / [[IADR-0612]] (#2046): 緊急アクセスの導線から来たか（表示の文脈だけ。許可ではない）。
  const viaBreakGlass = useSearch({ from: '/_shell/docs/$id' }).via === 'break-glass';
  const { detail, content, versions } = useDocumentQueries(id);
  // 05_screens §共通シェル / #446: パンくずの葉は文書タイトルである（モックの crumb
  // `ホーム / 検索結果 / 経費精算規程 v3.2`）。**フックは早期 return より前で呼ぶ。**
  // 取得前・取得失敗時は `undefined`＝葉を描かない（未確定の文字列をパンくずへ出さない。
  // 「読み込み中」を段に出すと、パンくずが現在地ではなく状態の表示になる）。
  useBreadcrumbLeaf(detail.data?.title);

  // 404（不在／秘匿）と 5xx を分ける。**404 は文書の有無を示さない中立表示**であり、
  // `role="alert"` も再試行も付けない（同じ要求は必ず同じ 404 を返す）。
  // SC-18 の「起点未指定 → 404 → 三部品」と同じ描き分けである。
  if (isNotFound(detail.error)) {
    // FR-19, 計画 ADR-0036 D-08 / [[IADR-0612]] (#2046): 緊急アクセスの導線から来て 404 なら、**有効でなくなった**
    // （失効・本人の終了・指名の対象外・資料が無い）ことを告げる。🔴 **どれに当たるかは区別しない**
    // （後段は区別せず 404 を返す。存在秘匿）。`role="alert"` も再試行も付けない（同じ要求は同じ 404 を返す）。
    if (viaBreakGlass) {
      return (
        <EmptyState
          title={t`緊急アクセスでこの資料を開けませんでした。`}
          description={t`緊急アクセスが失効したか、所有者が終了したか、あなたが指名されていない可能性があります。開けるのは、有効な間に指名された管理者だけです。`}
        />
      );
    }
    return (
      <EmptyState
        title={t`文書が見つかりませんでした。`}
        description={t`URL を確かめるか、検索から辿り直してください。`}
      />
    );
  }

  return (
    <QueryState query={detail} errorTitle={t`文書の取得に失敗しました。`}>
      {(doc) => (
        <DocumentDetail
          doc={doc}
          content={content}
          versions={versions}
          viaBreakGlass={viaBreakGlass}
        />
      )}
    </QueryState>
  );
}

function DocumentDetail({
  doc,
  content,
  versions,
  viaBreakGlass,
}: {
  doc: DocumentDto;
  content: UseQueryResult<DocumentContentDto, unknown>;
  versions: UseQueryResult<DocumentVersionDto[], unknown>;
  viaBreakGlass: boolean;
}) {
  const { t } = useLingui();
  // `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである
  // （プロパティ参照・関数呼び出しはカタログの ID を壊す）。
  const status = doc.status;
  const version = doc.version;
  const updatedAt = formatDateTime(doc.updatedAt);

  return (
    <section className="flex flex-col gap-n4 lg:flex-row">
      <div className="min-w-0 lg:flex-[1.7]">
        {/*
          FR-19, 計画 ADR-0036 D-08, planning#841 裁定 1・2・5 / [[IADR-0612]] (#2046): 緊急アクセスで開いたことを
          **本文より前に**告げる（閲覧が 1 件ずつ記録されること・閲覧だけであること・有効な間だけであること）。
          色だけに頼らず、ラベルと文言が意味を担う。
        */}
        {viaBreakGlass && (
          <Alert tone="warning" label={t`緊急アクセス`} className="mb-n3">
            <Trans>
              緊急アクセスで閲覧しています。この閲覧は 1
              件ずつ監査ログに記録されます。閲覧だけができ、編集・削除・共有の変更はできません。所有者が終了するか、発動から
              15 分を過ぎると開けなくなります。
            </Trans>
          </Alert>
        )}
        {/* hi-fi `.ttl` / `.sub`。 */}
        <h1 className="text-[17px] font-medium text-fg">{doc.title}</h1>
        <p className="text-xs text-fg-muted">
          <Trans>正規化文書（Markdown）プレビュー</Trans>
        </p>
        <p className="mb-n3 text-xs text-fg-muted">
          <Trans>
            状態: {status}｜版: v{version}｜更新: {updatedAt}
          </Trans>
        </p>

        <ContentView content={content} hasBody={doc.hasBody !== false} />
        {/* hi-fi の順: 本文 → 導線の行 → AI 提案。 */}
        <SourceLinks doc={doc} />
        {/* 05_screens §SC-03:「本文の下部に表示し、その場で承認／却下できる」。0 件なら欄ごと出ない。 */}
        {/* 緊急アクセスは**閲覧だけ**（planning#841 裁定 1）。承認の欄（書き込みの導線）は出さない。 */}
        {!viaBreakGlass && <AiSuggestionPanel documentId={doc.id} />}
      </div>

      <div className="min-w-0 lg:flex-1">
        {/* 個人資料のときだけ出る欄（組織文書では `null`）。属性・タグより先に置く ——
            「誰の資料か」は属性の読み方を決める文脈だからである。 */}
        <PrivateNoteSummary doc={doc} />

        <Panel heading={<Trans>属性・タグ</Trans>}>
          <AttributeList attributes={doc.attributes} tags={doc.tags} />
        </Panel>

        {/* 版履歴は**補助情報**である（取れなくても本文・属性は読める）。
            🔴 FR-06, SC-03, 05_screens §共通シェルの状態表示の規約 (#2121): **失敗と 0 件を描き分ける**（規約は個別画面の記述より優先する）。
            従前は取れない・0 件のどちらも欄ごと消え、利用者は失敗を「版が無い」と読んでいた。
            判定順（失敗 → 待ち → 空 → 本体）と再試行は共通の `QueryState` が持つ。縮退は版履歴の欄だけに閉じる。 */}
        {/* 欄に名前を付ける（region）。版履歴の失敗の alert を欄の中で探せるようにする（他の欄の alert と取り違えない）。 */}
        <Panel heading={<Trans>バージョン</Trans>} aria-label={t`バージョン`}>
          <QueryState
            query={versions}
            isEmpty={(data) => data.length === 0}
            empty={<EmptyState title={t`版履歴はまだありません。`} />}
            errorTitle={t`版履歴を取得できませんでした。`}
          >
            {(data) => <VersionTable versions={data} />}
          </QueryState>
        </Panel>
      </div>
    </section>
  );
}

/**
 * 正規化 Markdown 本文。
 *
 * **HTML へレンダリングしない**（画面仕様書 §本文の描画）。本文は外部データソース由来であり、
 * HTML 化はサニタイズ方針の決定を伴う（誤ると保存型 XSS になる）。ADR-0031 の採用技術一覧にも
 * Markdown レンダラは無い。原文を等幅・改行保持で安全に表示する。
 */
function ContentView({
  content,
  hasBody,
}: {
  content: UseQueryResult<DocumentContentDto, unknown>;
  hasBody: boolean;
}) {
  // `StatusBadge` の children は文字列を要求する（アイコン＋テキストを内部で組むため）。
  // SC-02 と同じく `useLingui().t` のテンプレート形で渡す。
  const { t } = useLingui();
  return (
    <Panel heading={<Trans>本文</Trans>}>
      {/* SC-03, ADR-0070 決定 3・決定 4 / #1254（[[IADR-0388]] 決定 2）:
          **原本が本文を持たない文書**（テキスト層の無い PDF 等）は、空の本文を出すのではなく
          SC-02 と**同じ文言・同じ導出**で「本文なし（原本を参照）」を示し、原本の導線
          （下の SourceLinks）へ委ねる。空の `pre` を出すと「読み込みに失敗した」と読み違える。
          **`hasBody === false` のときだけ**である（項目を持たない旧応答は従来どおり本文を描く）。
          **本文を取りに行っていないので `QueryState` へは渡さない**——無効な照会は永遠に待ちである。 */}
      {hasBody ? (
        <QueryState
          query={content}
          loadingLabel={t`本文を読み込み中…`}
          errorTitle={t`本文は利用できません。`}
        >
          {(data) => (
            <pre className="overflow-x-auto whitespace-pre-wrap break-words rounded-md bg-surface-muted p-3 text-sm">
              {data.markdown}
            </pre>
          )}
        </QueryState>
      ) : (
        <StatusBadge tone="neutral">{t`本文なし（原本を参照）`}</StatusBadge>
      )}
    </Panel>
  );
}

/**
 * 出典元（原本）リンクと、SC-04（Wiki）・SC-18（ナレッジグラフ）への導線。
 * `http(s)` のときだけリンク化し、`storage://` 等は等幅表記で参照だけ示す（押せないものを押させない）。
 *
 * 「Wiki で閲覧」は台帳（権限内の Wiki ページ一覧）にこの文書があるときだけ出し、**文書別ディープリンク**
 * `/wiki?doc=<id>` へ送る（#1200。従前の「`/wiki` までで、ページ単位では飛べない」を解いた）。
 * 台帳が未取得・取得失敗のときは出さない（到達できない導線を押させない）。
 *
 * 「ナレッジグラフで見る」は **hi-fi モックの同じ行（422 右）**の要素であり、常に出す ——
 * Wiki と違い、辿り着ける先の有無を前もって引く必要が無い（SC-18 は起点があれば必ず描ける）。
 */
/**
 * 原本の所在をリンクにしてよいときだけ、その href を返す（#2121）。
 *
 * 🔴 **URL として解析し、スキームが `http:` か `https:` のときだけ**リンクにする（前方一致の正規表現では判定しない）。
 * `javascript:` / `data:` / `vbscript:` は大小文字の揺れ（`JaVaScRiPt:`）を含めてリンクにしない（`URL` はスキームを小文字へ正規化する）。
 * `HTTPS://` のような大文字の http(s) は http(s) そのものなのでリンクにする。解析できない値（UNC・相対パス）は等幅表記。
 */
function linkableHttpHref(uri: string | null | undefined): string | null {
  if (!uri) return null;
  try {
    const url = new URL(uri);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch {
    return null;
  }
}

function SourceLinks({ doc }: { doc: DocumentDto }) {
  const wiki = useWikiPageIndex();
  const hasWikiPage = wiki.documentIds?.has(doc.id) ?? false;
  // 🔴 FR-06, SC-03, SC-05, IADR-0561 (#2121): **「原本」は原本の所在（台帳の originalUri）だけを指す。**
  // 本文の応答の `sourceUri` の中身は `markdownUri`（正規化済み Markdown の参照）であり、どちらも原本ではない。
  // 従前の `content.sourceUri ?? doc.markdownUri` は、どちらへ落ちても正規化済み Markdown を「原本」と表示していた。
  // 所在が無ければ「—」（推測した所在を出さない）。
  const sourceUri = doc.originalUri ?? null;
  const href = linkableHttpHref(sourceUri);
  return (
    <p className="mb-n3 flex flex-wrap items-center gap-n2 text-sm">
      {hasWikiPage && (
        <>
          {/* UC-07: Wiki 閲覧導線（内部ルート）。閲覧範囲は前段ゲートウェイ（ABAC）が台帳の側で決めている。 */}
          <span aria-hidden>📖</span>
          <Link to="/wiki" search={{ doc: doc.id }} className="text-brand hover:underline">
            <Trans>Wikiで閲覧</Trans>
          </Link>
          <span className="text-fg-muted" aria-hidden>
            ｜
          </span>
        </>
      )}
      <span className="text-fg-muted">
        <Trans>原本</Trans>:
      </span>
      {sourceUri ? (
        href ? (
          <a
            href={href}
            target="_blank"
            rel="noopener noreferrer"
            className="text-brand hover:underline"
          >
            {sourceUri}
          </a>
        ) : (
          <code className="text-xs text-fg-muted">{sourceUri}</code>
        )
      ) : (
        <span aria-hidden>—</span>
      )}
      <span className="text-fg-muted" aria-hidden>
        ｜
      </span>
      {/* SC-18, UC-10, FR-17 (#1240): 近傍探索の**起点として本文書を引き渡す**。
          SC-18 は `root` が無いと「起点を指定してください」の案内を出して照会しないので、
          **起点を渡さない導線は作らない**（押しても何も見えない導線になる）。

          🔴 **`hops` / `by` を書いているのは「既定値の複写」ではなく「明示の要求」である。**
          SC-18 の検索パラメータは 3 つとも必須（`GraphSearch`）であり、
          **feature どうしを import しない**（ADR-0066 決定 1）ため既定を引いて来ることはできない。
          `AiSuggestionPanel` が `/ai-suggestions` へ `{ state: 'pending', kind: 'all' }` を
          渡しているのと同じ形である。**渡した値は SC-18 側の `validateSearch` が値域で丸める**ので、
          こちらが古くなっても壊れ方は「別の深さで開く」に留まる（計画 §ルートパスの例示も
          `/graph?root=…&hops=2` である）。

          色だけで意味を持たせない: 記号 ◉（装飾）＋ テキストの対である。 */}
      <span aria-hidden>◉</span>
      <Link
        to="/graph"
        search={{ root: doc.id, hops: 2, by: 'distance' }}
        className="text-brand hover:underline"
      >
        <Trans>ナレッジグラフで見る</Trans>
      </Link>
    </p>
  );
}

function AttributeList({
  attributes,
  tags,
}: {
  attributes: Record<string, string>;
  tags: string[];
}) {
  const { t } = useLingui();
  const entries = orderedAttributes(attributes);
  return (
    // hi-fi の属性欄は 1 列である（`Kv columns={1}`）。項目名と値の対は `dl` が担う。
    <Kv columns={1}>
      {entries.map(([key, value]): ReactNode => {
        const label = attributeLabel(key);
        return (
          <KvItem key={key} label={label ? i18n._(label) : key}>
            {/* 機密区分だけは表示名へ写す（#2018。写像は SC-01・SC-02 と共有。attributes.ts の冒頭コメント参照）。
                部門の値は変換しない。 */}
            {key === CONFIDENTIALITY_KEY ? confidentialityLabel(value, (d) => i18n._(d)) : value}
          </KvItem>
        );
      })}
      <KvItem label={t`タグ`}>
        <span className="flex flex-wrap gap-1">
          {tags.length === 0 ? (
            <span className="text-fg-muted" aria-hidden>
              —
            </span>
          ) : (
            tags.map((tag) => (
              <Tag key={tag} tone="neutral">
                {tag}
              </Tag>
            ))
          )}
        </span>
      </KvItem>
    </Kv>
  );
}

function VersionTable({ versions }: { versions: DocumentVersionDto[] }) {
  return (
    <Table>
      <TableCaption>
        <Trans>バージョン履歴</Trans>
      </TableCaption>
      <TableHead>
        <TableRow>
          <TableHeaderCell>
            <Trans>版</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>変更メモ</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>作成</Trans>
          </TableHeaderCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {versions.map((v) => (
          <TableRow key={v.version}>
            <TableCell>v{v.version}</TableCell>
            <TableCell>{v.changeNote ?? '—'}</TableCell>
            <TableCell>{formatDateTime(v.createdAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
