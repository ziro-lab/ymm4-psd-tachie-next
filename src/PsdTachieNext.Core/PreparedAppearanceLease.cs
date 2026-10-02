namespace PsdTachieNext.Core;

/// <summary>Retains required blocks and optional disk generation pin until rendering/consumer completion.</summary>
public sealed class PreparedAppearanceLease : IDisposable
{
    private readonly Dictionary<int, SharedBlockLease> blocks = [];
    private SharedDocumentLease? document;
    private IDisposable? generationPin;
    private readonly bool ownsDocument;
    private bool disposed;
    public RenderPlan Plan { get; }
    private PreparedAppearanceLease(SharedDocumentLease document, RenderPlan plan, IDisposable? pin, bool ownsDocument)
    {
        if (!ReferenceEquals(document.Manifest, plan.Manifest)) throw new ArgumentException("Plan/document mismatch.");
        this.document = document; Plan = plan; generationPin = pin; this.ownsDocument = ownsDocument;
    }
    /// <summary>Cold loading entry point. Run on a bounded worker, never in the prepared GPU draw path.</summary>
    public static PreparedAppearanceLease Prepare(SharedDocumentLease document, RenderPlan plan,
        IDisposable? generationPin = null, bool ownsDocument = false, CancellationToken cancellationToken = default)
    {
        var result = new PreparedAppearanceLease(document, plan, generationPin, ownsDocument);
        try
        {
            foreach (var id in plan.RequiredBlockIds) { cancellationToken.ThrowIfCancellationRequested(); result.blocks.Add(id, document.AcquireBlock(id)); }
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }
    public ReadOnlyMemory<byte> GetBlock(int id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!blocks.TryGetValue(id, out var block)) throw new InvalidOperationException("描画に必要なblockが未準備です。disk fallbackはありません。");
        return block.Memory;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var b in blocks.Values) b.Dispose(); blocks.Clear();
        try { if (ownsDocument) document?.Dispose(); }
        finally { document = null; generationPin?.Dispose(); generationPin = null; }
    }
}
