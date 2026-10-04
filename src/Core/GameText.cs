using System;
using System.Text;

namespace Sungaila.NewDark.Core
{
    public enum GameTextEncoding
    {
        Automatic = 0,
        Oem850 = 850,
        Windows1250 = 1250,
        Windows1251 = 1251,
        Utf8 = 65001
    }

    /// <summary>
    /// Decodes byte-preserving game names for display without changing the protocol data.
    /// </summary>
    public static class GameText
    {
        private static readonly UTF8Encoding _strictUtf8 = new(false, true);

        /// <param name="byteString">Raw protocol bytes mapped one-to-one to Latin-1 characters.</param>
        /// <param name="encoding">Automatic tries valid UTF-8, then DOS 850. Legacy codepages can be selected explicitly.</param>
        public static string Decode(string byteString, GameTextEncoding encoding = GameTextEncoding.Automatic)
        {
            var bytes = Encoding.Latin1.GetBytes(byteString);

            if (encoding == GameTextEncoding.Automatic)
            {
                try
                {
                    return _strictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    // NewDark does not transmit a codepage. This fallback is a display preference,
                    // not reliable detection; other legacy encodings require an explicit selection.
                    encoding = GameTextEncoding.Oem850;
                }
            }

            var decoder = encoding switch
            {
                GameTextEncoding.Utf8 => Encoding.UTF8,
                GameTextEncoding.Oem850 or GameTextEncoding.Windows1250 or
                GameTextEncoding.Windows1251 => CodePagesEncodingProvider.Instance.GetEncoding((int)encoding)!,
                _ => throw new ArgumentOutOfRangeException(nameof(encoding))
            };

            return decoder.GetString(bytes);
        }
    }
}