using System;
using System.Threading;

namespace WechatDraft.Windows;

internal interface IClipboardWrite : IDisposable { void Commit(); }
internal interface IClipboardSnapshot : IDisposable { IClipboardWrite PrepareRestore(); }
internal interface IClipboardBackend : IDisposable
{
    bool Open();
    void Close();
    uint Sequence { get; }
    uint OwnerProcess { get; }
    IClipboardSnapshot Capture();
    IClipboardWrite PrepareText(string text);
    void Empty();
    string ReadPlainText();
}

// The clipboard is global. Keep each read/check/write inside the same native lock,
// and allocate all replacement handles before removing any current contents.
internal sealed class ClipboardSession : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IClipboardBackend _backend;
    private IClipboardSnapshot? _snapshot;
    private uint _sequence;
    private bool _modified, _disposed;

    public ClipboardSession(nint owner) : this(new NativeClipboardBackend(owner)) { }
    internal ClipboardSession(IClipboardBackend backend)
    {
        _backend = backend;
        if (!Gate.Wait(0)) { backend.Dispose(); throw new InvalidOperationException("其他任务正在使用剪贴板，请稍后重试。"); }
        try
        {
            Open();
            try { _snapshot = backend.Capture(); _sequence = backend.Sequence; }
            finally { backend.Close(); }
        }
        catch
        {
            try { _snapshot?.Dispose(); }
            finally { try { backend.Dispose(); } finally { Gate.Release(); } }
            throw;
        }
    }

    public void PutText(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var replacement = _backend.PrepareText(text);
        Open();
        try
        {
            if (_backend.Sequence != _sequence) throw new InvalidOperationException("剪贴板被其他程序修改，已停止操作。");
            _backend.Empty(); _modified = true;
            try { replacement.Commit(); }
            finally { _sequence = _backend.Sequence; }
        }
        finally { _backend.Close(); }
    }

    public bool TryReadCopied(uint sourceProcess, out string? text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        text = null;
        if (_backend.Sequence == _sequence || !_backend.Open()) return false;
        try
        {
            if (_backend.Sequence == _sequence) return false;
            if (_backend.OwnerProcess != sourceProcess) throw new InvalidOperationException("复制来源不属于目标微信，已停止操作。");
            try { text = _backend.ReadPlainText(); }
            finally { _sequence = _backend.Sequence; } // Delayed rendering may advance the sequence.
            return true;
        }
        finally { _backend.Close(); }
    }

    private void Open()
    {
        for (var i = 0; i < 5; i++) { if (_backend.Open()) return; Thread.Sleep(20); }
        throw new InvalidOperationException("剪贴板正忙，未继续操作微信。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_modified || _backend.Sequence != _sequence) return;
            using var replacement = _snapshot!.PrepareRestore();
            Open();
            try
            {
                if (_backend.Sequence != _sequence) return;
                _backend.Empty();
                replacement.Commit();
            }
            finally { _backend.Close(); }
        }
        catch (Exception e) when (e is InvalidOperationException or OutOfMemoryException)
        { throw new InvalidOperationException("原剪贴板未能完整恢复，请检查剪贴板；微信输入未重复操作。", e); }
        finally
        {
            try { _snapshot?.Dispose(); }
            finally { try { _backend.Dispose(); } finally { Gate.Release(); } }
        }
    }
}
