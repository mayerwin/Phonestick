using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Phonestick
{
    /// <summary>
    /// A small QR code encoder (byte mode, error correction level M, versions 1 to 10), enough
    /// for Android's "Pair device with QR code". Follows ISO/IEC 18004; structure after
    /// Project Nayuki's reference implementation.
    /// </summary>
    internal sealed class QrCode
    {
        // Level M, indexed by version (0 unused).
        private static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] NumBlocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

        public readonly int Size;
        private readonly bool[,] _m;    // [y, x], true = dark
        private readonly bool[,] _fn;   // function modules (not data)

        public bool this[int x, int y] => _m[y, x];

        public static QrCode Encode(string text, int forceMask = -1)
        {
            var data = Encoding.UTF8.GetBytes(text);
            for (int ver = 1; ver <= 10; ver++)
            {
                int capacityBits = DataCodewords(ver) * 8;
                int countBits = ver <= 9 ? 8 : 16;
                if (4 + countBits + data.Length * 8 <= capacityBits) return new QrCode(ver, data, forceMask);
            }
            throw new ArgumentException("Text too long for a QR code here");
        }

        private QrCode(int ver, byte[] data, int forceMask)
        {
            Size = ver * 4 + 17;
            _m = new bool[Size, Size];
            _fn = new bool[Size, Size];
            DrawFunctionPatterns(ver);
            var codewords = AddEccAndInterleave(ver, EncodeData(ver, data));
            DrawCodewords(codewords);

            int best = 0, bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                int p = Penalty();
                if (p < bestPenalty) { best = mask; bestPenalty = p; }
                ApplyMask(mask); // undo (XOR)
            }
            if (forceMask >= 0) best = forceMask;
            ApplyMask(best);
            DrawFormatBits(best);
        }

        // ------------------------------------------------------------ data

        private static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];

        private static byte[] EncodeData(int ver, byte[] data)
        {
            var bits = new List<bool>();
            void Add(int value, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
            Add(0x4, 4); // byte mode
            Add(data.Length, ver <= 9 ? 8 : 16);
            foreach (var b in data) Add(b, 8);
            int capacity = DataCodewords(ver) * 8;
            Add(0, Math.Min(4, capacity - bits.Count));
            Add(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Add(pad, 8);
            var result = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return result;
        }

        private static byte[] AddEccAndInterleave(int ver, byte[] data)
        {
            int numBlocks = NumBlocks[ver], blockEcc = EccPerBlock[ver];
            int rawCodewords = RawDataModules(ver) / 8;
            int numShort = numBlocks - rawCodewords % numBlocks;
            int shortLen = rawCodewords / numBlocks;
            var divisor = RsDivisor(blockEcc);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortLen - blockEcc + (i < numShort ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                var ecc = RsRemainder(dat, divisor);
                var block = new byte[shortLen + 1];
                // Short blocks get a dummy byte before their ECC, skipped when interleaving.
                Array.Copy(dat, 0, block, 0, datLen);
                Array.Copy(ecc, 0, block, block.Length - blockEcc, blockEcc);
                blocks.Add(block);
            }
            var result = new List<byte>();
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - blockEcc || j >= numShort) result.Add(blocks[j][i]);
            return result.ToArray();
        }

        private static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)GfMul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMul(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= (byte)GfMul(divisor[i], factor);
            }
            return result;
        }

        private static int GfMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        // ------------------------------------------------------------ drawing

        private void Set(int x, int y, bool dark) { _m[y, x] = dark; _fn[y, x] = true; }

        private void DrawFunctionPatterns(int ver)
        {
            for (int i = 0; i < Size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                int step = (ver * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
                var pos = new int[numAlign];
                pos[0] = 6;
                for (int i = numAlign - 1, p = Size - 7; i >= 1; i--, p -= step) pos[i] = p;
                for (int i = 0; i < numAlign; i++)
                    for (int j = 0; j < numAlign; j++)
                    {
                        if ((i == 0 && j == 0) || (i == 0 && j == numAlign - 1) || (i == numAlign - 1 && j == 0)) continue;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                Set(pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
            }
            DrawFormatBits(0); // reserves the area; redrawn with the real mask
            if (ver >= 7)
            {
                int rem = ver;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = ver << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, b = i / 3;
                    Set(a, b, bit);
                    Set(b, a, bit);
                }
            }
        }

        private void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) Set(xx, yy, dist != 2 && dist != 4);
                }
        }

        private void DrawFormatBits(int mask)
        {
            int data = 0 << 3 | mask; // level M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
            for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
            Set(8, Size - 8, true);
        }

        private void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!_fn[y, x] && i < data.Length * 8)
                        {
                            _m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_fn[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) _m[y, x] = !_m[y, x];
                }
        }

        /// <summary>Simplified penalty (runs, 2x2 blocks, dark balance): picks a readable mask.</summary>
        private int Penalty()
        {
            int result = 0, dark = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < Size; a++)
                {
                    int run = 0; bool color = false;
                    for (int b = 0; b < Size; b++)
                    {
                        bool c = pass == 0 ? _m[a, b] : _m[b, a];
                        if (b > 0 && c == color) run++;
                        else { if (run >= 5) result += run - 2; color = c; run = 1; }
                    }
                    if (run >= 5) result += run - 2;
                }
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_m[y, x]) dark++;
                    if (x < Size - 1 && y < Size - 1)
                    {
                        bool c = _m[y, x];
                        if (c == _m[y, x + 1] && c == _m[y + 1, x] && c == _m[y + 1, x + 1]) result += 3;
                    }
                }
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return result + Math.Max(0, k) * 10;
        }

        /// <summary>Black on white, with the 4-module quiet zone the standard asks for.</summary>
        public Bitmap ToBitmap(int scale)
        {
            int border = 4, n = (Size + border * 2) * scale;
            var bmp = new Bitmap(n, n);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (_m[y, x]) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
            }
            return bmp;
        }
    }
}
