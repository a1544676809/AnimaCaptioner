using System.Runtime.InteropServices;
using System.Text;

namespace AnimaCaptioner.Core;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）加密/解密。
/// 选 DPAPI 而不是自造密钥：主密钥由 Windows 托管、绑定本机本用户，
/// 把设置文件拷到别的机器或别的账户下解不开，磁盘上也不会留明文 API Key。
/// </summary>
internal static class DpapiSecret
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plainText, string entropy)
        => Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plainText), entropy));

    /// <summary>解不开时返回 null（换机器/换账户），调用方据此提示重新填写。</summary>
    public static string? TryUnprotect(string base64, string entropy)
    {
        try
        {
            var clear = UnprotectBytes(Convert.FromBase64String(base64), entropy);
            return clear is null ? null : Encoding.UTF8.GetString(clear);
        }
        catch { return null; }
    }

    private static byte[] ProtectBytes(byte[] data, string entropy)
    {
        var inBlob = ToBlob(data);
        var entBlob = ToBlob(Encoding.UTF8.GetBytes(entropy));
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                throw new InvalidOperationException("CryptProtectData failed, win32=" + Marshal.GetLastWin32Error());
            try { return FromBlob(outBlob); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Free(inBlob); Free(entBlob); }
    }

    private static byte[]? UnprotectBytes(byte[] data, string entropy)
    {
        var inBlob = ToBlob(data);
        var entBlob = ToBlob(Encoding.UTF8.GetBytes(entropy));
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                return null;
            try { return FromBlob(outBlob); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Free(inBlob); Free(entBlob); }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var blob = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
        return blob;
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }

    private static void Free(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
    }
}
