// Issue #126 / IADR-0009: 存在秘匿。404（不在または権限による秘匿）は同一の画面で応答し、
// 資源の存在有無を推測させない。
export function NotFound() {
  return (
    <main style={{ padding: '2rem', textAlign: 'center' }}>
      <h1>見つかりませんでした</h1>
      <p>お探しのページは存在しないか、アクセスできません。</p>
    </main>
  );
}
