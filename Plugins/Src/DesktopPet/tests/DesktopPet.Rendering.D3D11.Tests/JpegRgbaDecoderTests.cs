namespace DesktopPet.Rendering.D3D11.Tests;

[TestClass]
public sealed class JpegRgbaDecoderTests
{
    [TestMethod]
    public void Decode_NonJpeg_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => JpegRgbaDecoder.Decode([0x00, 0x01, 0x02]));
    }

    [TestMethod]
    public void Decode_Minimal1x1Gray128_ReturnsSinglePixelLuma128()
    {
        // Construct a 1×1 grayscale baseline JPEG where the single pixel = 128.
        // Level-shifted DC delta = 0 → DC Huffman symbol 0 → no value bits.
        // This is the simplest possible valid JPEG scan.
        var jpeg = BuildGray1x1JpegNeutral();
        var img  = JpegRgbaDecoder.Decode(jpeg);

        Assert.AreEqual(1, img.Width);
        Assert.AreEqual(1, img.Height);
        Assert.HasCount(4, img.RgbaPixels);
        Assert.AreEqual(128, img.RgbaPixels[0], "R");
        Assert.AreEqual(128, img.RgbaPixels[1], "G");
        Assert.AreEqual(128, img.RgbaPixels[2], "B");
        Assert.AreEqual(255, img.RgbaPixels[3], "A");
    }

    // ── JPEG builder helpers ──────────────────────────────────────────────

    // Builds a 1×1 grayscale JPEG whose single pixel decodes to luma = 128.
    // DCT of a 128-constant block (level-shifted to all-zeros) → F(0,0) = 0.
    // DC delta = 0 → Huffman symbol 0 → single code bit "0".
    // AC EOB → code bit "0".  Bitstream = "00" padded → byte 0x3F.
    private static byte[] BuildGray1x1JpegNeutral()
    {
        using var ms = new MemoryStream();

        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // DQT: 8-bit, table 0, all Q = 1
        var dqt = new byte[1 + 64];
        dqt[0] = 0x00;
        for (var i = 1; i <= 64; i++) dqt[i] = 1;
        WriteSegment(ms, 0xDB, dqt);

        // SOF0: 1×1, 8-bit, 1 component Y (id=1, H=1, V=1, QT=0)
        WriteSegment(ms, 0xC0, [0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00]);

        // DHT DC: class=0, id=0, 1 symbol of length 1: symbol=0x00 (DC delta = 0 bits)
        // payload = class/id (1) + counts×16 (16) + symbols (1)
        WriteSegment(ms, 0xC4, [0x00, 0x01, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, 0x00]);

        // DHT AC: class=1, id=0, 1 symbol of length 1: symbol=0x00 (EOB)
        WriteSegment(ms, 0xC4, [0x10, 0x01, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, 0x00]);

        // SOS header: Ns=1, comp-id=1, DC/AC=0/0, Ss=0, Se=63, Ah/Al=0
        WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);

        // Compressed bitstream: DC=0 (bit 0) + AC-EOB (bit 0) = "00" → 0x3F
        ms.WriteByte(0x3F);

        // EOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);

        return ms.ToArray();
    }

    private static void WriteSegment(MemoryStream ms, byte marker, byte[] payload)
    {
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        var len = (ushort)(payload.Length + 2);
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)len);
        ms.Write(payload);
    }
}
