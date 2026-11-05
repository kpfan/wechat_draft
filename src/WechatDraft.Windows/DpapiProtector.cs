using System;
using System.Runtime.InteropServices;
using System.Text;
using WechatDraft.Core;

namespace WechatDraft.Windows;

public sealed class DpapiProtector : ISecretProtector
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    public string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, "WechatDraft", 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new InvalidOperationException($"Windows 无法保护或解密当前用户的数据（错误 {Marshal.GetLastWin32Error()}）。");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result; }
            finally { Native.LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); Array.Clear(bytes); }
    }
}
