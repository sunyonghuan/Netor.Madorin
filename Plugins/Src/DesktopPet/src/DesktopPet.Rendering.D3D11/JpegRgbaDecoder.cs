using System.Buffers.Binary;

namespace DesktopPet.Rendering.D3D11;

// Baseline (SOF0) JPEG → RGBA8 decoder.
// Supports YCbCr 4:4:4 / 4:2:0, grayscale, 8-bit precision, restart markers.
public static class JpegRgbaDecoder
{
    // Zigzag → natural-order index table
    private static readonly int[] ZigzagMap =
    [
         0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    // CosTable[u, x] = cos((2x+1)*u*π/16)
    private static readonly float[,] CosTable = BuildCosTable();

    private static float[,] BuildCosTable()
    {
        var t = new float[8, 8];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < 8; x++)
                t[u, x] = (float)Math.Cos((2.0 * x + 1.0) * u * Math.PI / 16.0);
        return t;
    }

    public static PngImage Decode(ReadOnlySpan<byte> jpegBytes)
        => new JpegDecoder(jpegBytes.ToArray()).Decode();

    // ── helper types ──────────────────────────────────────────────────────

    private sealed class HuffTable(int[] startCode, byte[][] symbolsByLength)
    {
        public readonly int[]    StartCode        = startCode;
        public readonly byte[][] SymbolsByLength  = symbolsByLength;
    }

    private sealed record ComponentInfo(int Id, int H, int V, int QtId);
    private sealed record ScanComp(int Ci, int DcId, int AcId);

    // ── decoder ───────────────────────────────────────────────────────────

    private sealed class JpegDecoder(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _pos = 2; // skip SOI

        private readonly int[][]      _quantTables = new int[4][];
        private readonly HuffTable?[,] _huffTables  = new HuffTable?[2, 4];

        private int _width, _height, _nComponents;
        private ComponentInfo[] _components = [];
        private int _hmax, _vmax, _restartInterval;

        // bit reader state
        private int _scanPos, _bitBuf, _bitsLeft;

        internal PngImage Decode()
        {
            if (_data.Length < 2 || _data[0] != 0xFF || _data[1] != 0xD8)
                throw new InvalidDataException("Not a JPEG file.");

            while (_pos < _data.Length - 1)
            {
                if (_data[_pos++] != 0xFF) throw new InvalidDataException("Expected JPEG marker.");
                while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
                if (_pos >= _data.Length) break;

                var marker = _data[_pos++];
                switch (marker)
                {
                    case 0xD8: break;
                    case 0xD9: goto done;
                    case 0xC0: ParseSOF0(ReadPayload()); break;
                    case 0xC4: ParseDHT(ReadPayload()); break;
                    case 0xDB: ParseDQT(ReadPayload()); break;
                    case 0xDD: ParseDRI(ReadPayload()); break;
                    case 0xDA: return DecodeScan(ReadPayload());
                    // standalone (RST0-7): no length field
                    case >= 0xD0 and <= 0xD7: break;
                    default: SkipPayload(); break;
                }
            }
        done:
            throw new InvalidDataException("JPEG missing SOS marker.");
        }

        private byte[] ReadPayload()
        {
            if (_pos + 2 > _data.Length) throw new InvalidDataException("JPEG truncated.");
            var len = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_pos, 2)) - 2;
            _pos += 2;
            if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("JPEG marker length invalid.");
            var payload = _data[_pos..(_pos + len)];
            _pos += len;
            return payload;
        }

        private void SkipPayload()
        {
            if (_pos + 2 <= _data.Length)
                _pos += BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_pos, 2));
        }

        private void ParseDQT(byte[] d)
        {
            var i = 0;
            while (i < d.Length)
            {
                var prec = d[i] >> 4;
                var id   = d[i++] & 0xF;
                if (id > 3) throw new InvalidDataException("Invalid DQT id.");
                var tbl = new int[64];
                _quantTables[id] = tbl;
                for (var k = 0; k < 64; k++)
                {
                    tbl[ZigzagMap[k]] = prec == 0 ? d[i++]
                        : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i, 2));
                    if (prec != 0) i += 2;
                }
            }
        }

        private void ParseDHT(byte[] d)
        {
            var i = 0;
            while (i < d.Length)
            {
                var cls = d[i] >> 4;
                var id  = d[i++] & 0xF;
                if (cls > 1 || id > 3) throw new InvalidDataException("Invalid DHT.");
                var counts = new int[17];
                var total  = 0;
                for (var k = 1; k <= 16; k++) { counts[k] = d[i++]; total += counts[k]; }
                _huffTables[cls, id] = BuildHuffTable(counts, d[i..(i + total)]);
                i += total;
            }
        }

        private static HuffTable BuildHuffTable(int[] counts, byte[] syms)
        {
            var start  = new int[17];
            var byLen  = new byte[17][];
            for (var k = 0; k <= 16; k++) byLen[k] = [];
            var code = 0;
            var si   = 0;
            for (var len = 1; len <= 16; len++)
            {
                start[len]  = code;
                byLen[len]  = syms[si..(si + counts[len])];
                si  += counts[len];
                code = (code + counts[len]) << 1;
            }
            return new HuffTable(start, byLen);
        }

        private void ParseSOF0(byte[] d)
        {
            if (d[0] != 8) throw new NotSupportedException("Only 8-bit JPEG.");
            _height      = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(1, 2));
            _width       = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(3, 2));
            _nComponents = d[5];
            if (_nComponents is not (1 or 3))
                throw new NotSupportedException($"JPEG {_nComponents}-component not supported.");
            _components = new ComponentInfo[_nComponents];
            _hmax = _vmax = 0;
            for (var i = 0; i < _nComponents; i++)
            {
                var id = d[6 + i * 3];
                var s  = d[7 + i * 3];
                var q  = d[8 + i * 3];
                var h = s >> 4; var v = s & 0xF;
                _components[i] = new ComponentInfo(id, h, v, q);
                if (h > _hmax) _hmax = h;
                if (v > _vmax) _vmax = v;
            }
        }

        private void ParseDRI(byte[] d)
            => _restartInterval = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(0, 2));

        private PngImage DecodeScan(byte[] scanHeader)
        {
            var nsc       = scanHeader[0];
            var scanComps = new ScanComp[nsc];
            var p = 1;
            for (var i = 0; i < nsc; i++)
            {
                var cid  = scanHeader[p++];
                var tids = scanHeader[p++];
                scanComps[i] = new ScanComp(FindComp(cid), tids >> 4, tids & 0xF);
            }

            var mcusX = (_width  + _hmax * 8 - 1) / (_hmax * 8);
            var mcusY = (_height + _vmax * 8 - 1) / (_vmax * 8);

            var bufW = new int[_nComponents];
            var bufH = new int[_nComponents];
            var sam  = new int[_nComponents][][];
            for (var c = 0; c < _nComponents; c++)
            {
                bufW[c] = mcusX * _components[c].H * 8;
                bufH[c] = mcusY * _components[c].V * 8;
                sam[c]  = new int[bufH[c]][];
                for (var r = 0; r < bufH[c]; r++) sam[c][r] = new int[bufW[c]];
            }

            var dcPrev   = new int[_nComponents];
            _scanPos     = _pos;
            _bitBuf      = _bitsLeft = 0;
            var mcuCount = 0;

            for (var my = 0; my < mcusY; my++)
            for (var mx = 0; mx < mcusX; mx++)
            {
                if (_restartInterval > 0 && mcuCount > 0 && mcuCount % _restartInterval == 0)
                    ResetAtRestart(dcPrev);

                foreach (var sc in scanComps)
                {
                    var ci   = sc.Ci;
                    var comp = _components[ci];
                    var dcT  = _huffTables[0, sc.DcId] ?? throw new InvalidDataException("Missing DC Huffman table.");
                    var acT  = _huffTables[1, sc.AcId] ?? throw new InvalidDataException("Missing AC Huffman table.");
                    var qT   = _quantTables[comp.QtId]  ?? throw new InvalidDataException("Missing quantization table.");

                    for (var by = 0; by < comp.V; by++)
                    for (var bx = 0; bx < comp.H; bx++)
                        DecodeBlock(ci, mx, my, bx, by, comp, dcT, acT, qT, dcPrev, bufH, bufW, sam);
                }
                mcuCount++;
            }

            return BuildRgba(sam, bufW, bufH);
        }

        private void DecodeBlock(
            int ci, int mx, int my, int bx, int by,
            ComponentInfo comp, HuffTable dcT, HuffTable acT, int[] qT,
            int[] dcPrev, int[] bufH, int[] bufW, int[][][] sam)
        {
            var coeff = new int[64];

            // DC coefficient
            var dcLen = HuffDecode(dcT);
            if (dcLen > 0) dcPrev[ci] += Extend(ReadBits(dcLen), dcLen);
            coeff[0] = dcPrev[ci] * qT[0];

            // AC coefficients
            for (var k = 1; k < 64;)
            {
                var sym = HuffDecode(acT);
                if (sym == 0) break;            // EOB
                var run = sym >> 4;
                var acn = sym & 0xF;
                if (acn == 0) { k += 16; continue; } // ZRL
                k += run;
                if (k >= 64) break;
                coeff[ZigzagMap[k]] = Extend(ReadBits(acn), acn) * qT[ZigzagMap[k]];
                k++;
            }

            var pixels = new int[64];
            IDCT8x8(coeff, pixels);

            var sx  = (mx * comp.H + bx) * 8;
            var sy  = (my * comp.V + by) * 8;
            var buf = sam[ci];
            for (var py = 0; py < 8 && sy + py < bufH[ci]; py++)
            for (var px = 0; px < 8 && sx + px < bufW[ci]; px++)
                buf[sy + py][sx + px] = pixels[py * 8 + px];
        }

        private void ResetAtRestart(int[] dcPrev)
        {
            _bitsLeft = _bitBuf = 0;
            while (_scanPos < _data.Length - 1)
            {
                if (_data[_scanPos] == 0xFF && (_data[_scanPos + 1] & 0xF8) == 0xD0)
                { _scanPos += 2; break; }
                _scanPos++;
            }
            Array.Clear(dcPrev);
        }

        // ── bit reader ────────────────────────────────────────────────────

        private int HuffDecode(HuffTable tbl)
        {
            var code = 0;
            for (var len = 1; len <= 16; len++)
            {
                code = (code << 1) | ReadOneBit();
                var offset = code - tbl.StartCode[len];
                if (offset >= 0 && offset < tbl.SymbolsByLength[len].Length)
                    return tbl.SymbolsByLength[len][offset];
            }
            throw new InvalidDataException("Invalid Huffman code in JPEG.");
        }

        private int ReadBits(int n)
        {
            var val = 0;
            for (var i = 0; i < n; i++) val = (val << 1) | ReadOneBit();
            return val;
        }

        private int ReadOneBit()
        {
            if (_bitsLeft == 0)
            {
                if (_scanPos >= _data.Length) throw new InvalidDataException("JPEG bitstream truncated.");
                var b = _data[_scanPos++];
                if (b == 0xFF && _scanPos < _data.Length)
                {
                    var next = _data[_scanPos];
                    if (next == 0x00) _scanPos++;                  // byte stuffing
                    else if ((next & 0xF8) == 0xD0) _scanPos++;    // restart marker
                }
                _bitBuf   = b;
                _bitsLeft = 8;
            }
            return (_bitBuf >> --_bitsLeft) & 1;
        }

        // ── RGBA assembly ─────────────────────────────────────────────────

        private PngImage BuildRgba(int[][][] sam, int[] bufW, int[] bufH)
        {
            var rgba = new byte[_width * _height * 4];

            if (_nComponents == 1)
            {
                for (var py = 0; py < _height; py++)
                for (var px = 0; px < _width; px++)
                {
                    var luma = Clamp8(sam[0][py][px] + 128);
                    var off  = (py * _width + px) * 4;
                    rgba[off] = rgba[off + 1] = rgba[off + 2] = luma;
                    rgba[off + 3] = 255;
                }
            }
            else
            {
                // YCbCr: components are in SOF0 order (typically Y=0, Cb=1, Cr=2)
                var yH  = _components[0].H; var yV  = _components[0].V;
                var cbH = _components[1].H; var cbV = _components[1].V;
                var crH = _components[2].H; var crV = _components[2].V;

                for (var py = 0; py < _height; py++)
                for (var px = 0; px < _width; px++)
                {
                    var yv  = sam[0][py][px];
                    var cbx = Math.Min(px * cbH / yH, bufW[1] - 1);
                    var cby = Math.Min(py * cbV / yV, bufH[1] - 1);
                    var crx = Math.Min(px * crH / yH, bufW[2] - 1);
                    var cry = Math.Min(py * crV / yV, bufH[2] - 1);
                    var cb  = sam[1][cby][cbx];
                    var cr  = sam[2][cry][crx];

                    // JFIF YCbCr→RGB, level-shifted samples (samples ∈ [-128, 127])
                    var off = (py * _width + px) * 4;
                    rgba[off]     = Clamp8((int)(yv + 128 + 1.402f   * cr));
                    rgba[off + 1] = Clamp8((int)(yv + 128 - 0.34414f * cb - 0.71414f * cr));
                    rgba[off + 2] = Clamp8((int)(yv + 128 + 1.772f   * cb));
                    rgba[off + 3] = 255;
                }
            }

            return new PngImage(_width, _height, rgba);
        }

        private int FindComp(int id)
        {
            for (var i = 0; i < _nComponents; i++)
                if (_components[i].Id == id) return i;
            throw new InvalidDataException($"JPEG component id {id} not found.");
        }

        private static int  Extend(int val, int n)
            => n > 0 && val < (1 << (n - 1)) ? val - (1 << n) + 1 : val;

        private static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    // ── 8×8 separable float IDCT ──────────────────────────────────────────
    // Input:  dequantized DCT coefficients in natural order
    // Output: spatial samples in [-128, 127] (level shift applied by caller)
    private static void IDCT8x8(int[] coeff, int[] output)
    {
        const float invSqrt2 = 0.70710678f;
        var tmp = new float[64];

        // Row pass: G(x, v) = Σ_u [ C(u) * coeff[v*8+u] * cos((2x+1)*u*π/16) ]
        for (var v = 0; v < 8; v++)
        {
            var off = v * 8;
            var c0  = coeff[off];
            if (coeff[off+1] == 0 && coeff[off+2] == 0 && coeff[off+3] == 0 &&
                coeff[off+4] == 0 && coeff[off+5] == 0 && coeff[off+6] == 0 && coeff[off+7] == 0)
            {
                // All AC = 0: constant row
                var dc = c0 * invSqrt2;
                for (var x = 0; x < 8; x++) tmp[off + x] = dc;
                continue;
            }
            for (var x = 0; x < 8; x++)
            {
                var sum = c0 * invSqrt2;
                for (var u = 1; u < 8; u++) sum += coeff[off + u] * CosTable[u, x];
                tmp[off + x] = sum;
            }
        }

        // Column pass: f(x, y) = 0.25 * Σ_v [ C(v) * G(x,v) * cos((2y+1)*v*π/16) ]
        for (var x = 0; x < 8; x++)
        for (var y = 0; y < 8; y++)
        {
            var sum = tmp[x] * invSqrt2;
            for (var v = 1; v < 8; v++) sum += tmp[v * 8 + x] * CosTable[v, y];
            output[y * 8 + x] = (int)(sum * 0.25f);
        }
    }
}
