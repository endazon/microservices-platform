namespace RetrievalService;

// FR-02, FR-03 / #1247: 統合テストが WebApplicationFactory<T> の型引数に使うマーカー。
//
// Program.cs は `public partial class Program { }` を公開しているが、それは
// **グローバル名前空間**の型である。統合テストが複数のサービスを同時に参照すると
// `Program` が衝突する（CS0433）ため、他サービスと同じくサービス固有のマーカーを置く。
// WebApplicationFactory<T> は typeof(T).Assembly からエントリポイントを探すので、
// アセンブリ内の任意の public 型でよい。
//
// **本サービスの `Tests/` は `WebApplicationFactory<Program>` のままでよい** ——
// あちらは RetrievalService 1 つしか参照しないので衝突しない。ここを足しても
// 既存の 98 件の宣言は 1 バイトも動かない。
public sealed class RetrievalServiceTestMarker { }
