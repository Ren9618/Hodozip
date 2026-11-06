using System.Text;

namespace Hodozip.Core;

internal static class EncodingSetup
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            // Shift_JIS (CP932) を SharpCompress の ZIP ファイル名デコードに使うために必要。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
