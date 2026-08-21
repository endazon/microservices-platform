namespace ConversionService.Worker;

// ［2026-08-21 / #455 Phase 0 U0b］統合テストが WebApplicationFactory<T> の型引数に使うマーカー。
// 理由は IngestionService.Worker/TestMarker.cs と同じ（グローバル名前空間の Program の衝突回避）。
public sealed class ConversionServiceTestMarker { }
