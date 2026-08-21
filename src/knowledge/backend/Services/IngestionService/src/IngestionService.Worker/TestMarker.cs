namespace IngestionService.Worker;

// ［2026-08-21 / #455 Phase 0 U0b］統合テストが WebApplicationFactory<T> の型引数に使うマーカー。
//
// Program.cs は `public partial class Program { }` を公開しているが、それは
// **グローバル名前空間**の型である。統合テストが複数のサービスを同時に参照すると
// `Program` が衝突するため、Api 系 5 サービスと同じくサービス固有のマーカーを置く。
// WebApplicationFactory<T> は typeof(T).Assembly からエントリポイントを探すので、
// アセンブリ内の任意の public 型でよい。
public sealed class IngestionServiceTestMarker { }
