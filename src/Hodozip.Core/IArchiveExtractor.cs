namespace Hodozip.Core;

/// <summary>
/// アーカイブ解凍エンジンの抽象。Phase 1 は SharpCompress 実装のみ。
/// 将来別エンジンへ差し替えられるように UI はこのインターフェースだけを参照する。
/// </summary>
public interface IArchiveExtractor
{
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default);
}
