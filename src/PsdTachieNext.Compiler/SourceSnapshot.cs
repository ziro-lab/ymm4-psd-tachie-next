using PsdTachieNext.Core;

namespace PsdTachieNext.Compiler;

/// <summary>Owned immutable file snapshot; parser objects never escape CompileSnapshot.</summary>
public sealed class SourceSnapshot : IDisposable
{
    private FileStream? pin;
    public string SourcePath { get; }
    public string Path { get; }
    public SourceFingerprint Fingerprint { get; }
    internal SourceSnapshot(string sourcePath, string path, SourceFingerprint fingerprint)
    {
        SourcePath = sourcePath; Path = path; Fingerprint = fingerprint;
        pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(pin is null, this);
    public void VerifyOriginal(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var original = PsdCompiler.OpenOriginal(SourcePath);
        if (original.Length != Fingerprint.Length || PsdCompiler.HashStream(original, cancellationToken) != Fingerprint.Sha256)
            throw new SourceChangedDuringPreparationException("素材が準備中に変更されました。元PSDを再読込してください。");
    }
    public void Dispose()
    {
        var current = Interlocked.Exchange(ref pin, null);
        if (current is null) return;
        current.Dispose();
        Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true);
    }
}
