using Sungaila.NewDark.Core;
using System.Text;

namespace Sungaila.NewDark.Tests;

[TestClass]
public sealed class GameTextTests
{
    [TestMethod]
    [DataRow(GameTextEncoding.Oem850, "Gr\u00fc\u00dfe\u00b3")]
    [DataRow(GameTextEncoding.Windows1250, "\u0141\u00f3d\u017a \u015al\u0105sk")]
    [DataRow(GameTextEncoding.Windows1251, "\u0420\u0443\u0441\u0441\u043a\u0438\u0439")]
    [DataRow(GameTextEncoding.Utf8, "\u0420\u0443\u0441\u0441\u043a\u0438\u0439 \U0001f5dd")]
    public void Decode_UsesTheSelectedCodepage(GameTextEncoding encoding, string text)
    {
        var clientEncoding = encoding == GameTextEncoding.Utf8
            ? Encoding.UTF8
            : CodePagesEncodingProvider.Instance.GetEncoding((int)encoding)!;
        var bytes = clientEncoding.GetBytes(text);
        var byteString = Encoding.Latin1.GetString(bytes);

        Assert.AreEqual(text, GameText.Decode(byteString, encoding));
        Assert.AreSequenceEqual(bytes, Encoding.Latin1.GetBytes(byteString));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Thief 2 Server")]
    [DataRow("\u0420\u0443\u0441\u0441\u043a\u0438\u0439 \U0001f5dd")]
    public void Automatic_DecodesValidUtf8(string text)
    {
        var byteString = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

        Assert.AreEqual(text, GameText.Decode(byteString));
    }

    [TestMethod]
    [DataRow("\u0084\u0094\u0081", "\u00e4\u00f6\u00fc")]
    [DataRow("\u00c0\u00af", "\u2514\u00bb")]
    [DataRow("\u00c3", "\u251c")]
    public void Automatic_FallsBackForInvalidOrIncompleteUtf8(string byteString, string expected)
    {
        Assert.AreEqual(expected, GameText.Decode(byteString));
    }

    [TestMethod]
    public void ExplicitCodepage_OverridesAmbiguousUtf8Bytes()
    {
        const string byteString = "\u00c3\u00a4";

        Assert.AreEqual("\u00e4", GameText.Decode(byteString));
        Assert.AreEqual("\u251c\u00f1", GameText.Decode(byteString, GameTextEncoding.Oem850));
        Assert.AreEqual("\u0413\u00a4", GameText.Decode(byteString, GameTextEncoding.Windows1251));
    }

    [TestMethod]
    public void Utf8_ReplacesIncompleteSequencesOnlyForDisplay()
    {
        const string byteString = "\u00c3";

        Assert.AreEqual("\ufffd", GameText.Decode(byteString, GameTextEncoding.Utf8));
        Assert.AreSequenceEqual(new byte[] { 0xC3 }, Encoding.Latin1.GetBytes(byteString));
    }
}