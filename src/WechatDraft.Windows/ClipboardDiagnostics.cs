using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WechatDraft.Windows;

public static class ClipboardDiagnostics
{
    public static IReadOnlyList<string> Formats()
    {
        if (!Native.OpenClipboard(0)) throw new InvalidOperationException("剪贴板正忙。");
        try
        {
            var result = new List<string>(); uint format = 0;
            while ((format = Native.EnumClipboardFormats(format)) != 0)
            {
                var name = new StringBuilder(128); Native.GetClipboardFormatName(format, name, name.Capacity);
                result.Add(format + ":" + name);
                if (result.Count >= 128) break;
            }
            if (Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("无法枚举剪贴板格式。");
            return result;
        }
        finally { Native.CloseClipboard(); }
    }
}
