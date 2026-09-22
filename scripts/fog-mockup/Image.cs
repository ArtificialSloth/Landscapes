using System.IO.Compression;
using System.Text;

namespace VintageHorizons.Tools;

/// <summary>An RGB byte image with a PNG writer and a 5x7 label font; no imaging dependency.</summary>
sealed class Image
{
    public readonly int Width, Height;
    public readonly byte[] Rgb;

    public Image(int width, int height, byte fill = 0)
    {
        Width = width;
        Height = height;
        Rgb = new byte[width * height * 3];
        if (fill != 0) Array.Fill(Rgb, fill);
    }

    public void Blit(Image source, int ox, int oy)
    {
        for (int y = 0; y < source.Height; y++)
            Buffer.BlockCopy(source.Rgb, y * source.Width * 3, Rgb, ((oy + y) * Width + ox) * 3,
                source.Width * 3);
    }

    public void Text(int x, int y, string text, int scale, byte shade)
    {
        foreach (char raw in text.ToUpperInvariant())
        {
            string[] glyph = Glyphs.TryGetValue(raw, out string[]? g) ? g : Glyphs[' '];
            for (int row = 0; row < 7; row++)
            for (int col = 0; col < 5; col++)
            {
                if (glyph[row][col] != '1') continue;
                for (int sy = 0; sy < scale; sy++)
                for (int sx = 0; sx < scale; sx++)
                {
                    int px = x + col * scale + sx, py = y + row * scale + sy;
                    if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
                    int at = (py * Width + px) * 3;
                    Rgb[at] = Rgb[at + 1] = Rgb[at + 2] = shade;
                }
            }
            x += 6 * scale;
        }
    }

    public void WritePng(string path)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BigEndian(header, 0, Width);
        BigEndian(header, 4, Height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < Height; y++)
            {
                z.WriteByte(0);
                z.Write(Rgb, y * Width * 3, Width * 3);
            }
        }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    static void BigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, data.Length);
        s.Write(length);
        byte[] name = Encoding.ASCII.GetBytes(type);
        s.Write(name);
        s.Write(data);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in name) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4];
        BigEndian(crc, 0, (int)(c ^ 0xFFFFFFFFu));
        s.Write(crc);
    }

    static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" },
        ['B'] = new[] { "11110", "10001", "10001", "11110", "10001", "10001", "11110" },
        ['C'] = new[] { "01110", "10001", "10000", "10000", "10000", "10001", "01110" },
        ['D'] = new[] { "11110", "10001", "10001", "10001", "10001", "10001", "11110" },
        ['E'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" },
        ['F'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "10000" },
        ['G'] = new[] { "01110", "10001", "10000", "10111", "10001", "10001", "01111" },
        ['H'] = new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" },
        ['I'] = new[] { "01110", "00100", "00100", "00100", "00100", "00100", "01110" },
        ['J'] = new[] { "00111", "00010", "00010", "00010", "00010", "10010", "01100" },
        ['K'] = new[] { "10001", "10010", "10100", "11000", "10100", "10010", "10001" },
        ['L'] = new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
        ['M'] = new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" },
        ['N'] = new[] { "10001", "11001", "10101", "10011", "10001", "10001", "10001" },
        ['O'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
        ['P'] = new[] { "11110", "10001", "10001", "11110", "10000", "10000", "10000" },
        ['Q'] = new[] { "01110", "10001", "10001", "10001", "10101", "10010", "01101" },
        ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
        ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
        ['T'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" },
        ['U'] = new[] { "10001", "10001", "10001", "10001", "10001", "10001", "01110" },
        ['V'] = new[] { "10001", "10001", "10001", "10001", "10001", "01010", "00100" },
        ['W'] = new[] { "10001", "10001", "10001", "10101", "10101", "10101", "01010" },
        ['X'] = new[] { "10001", "10001", "01010", "00100", "01010", "10001", "10001" },
        ['Y'] = new[] { "10001", "10001", "01010", "00100", "00100", "00100", "00100" },
        ['Z'] = new[] { "11111", "00001", "00010", "00100", "01000", "10000", "11111" },
        ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
        ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
        ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
        ['3'] = new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" },
        ['4'] = new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" },
        ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
        ['6'] = new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" },
        ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
        ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
        ['9'] = new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" },
        ['.'] = new[] { "00000", "00000", "00000", "00000", "00000", "01100", "01100" },
        ['-'] = new[] { "00000", "00000", "00000", "11111", "00000", "00000", "00000" },
        [','] = new[] { "00000", "00000", "00000", "00000", "01100", "00100", "01000" },
        ['%'] = new[] { "11001", "11010", "00010", "00100", "01000", "01011", "10011" },
        [':'] = new[] { "00000", "01100", "01100", "00000", "01100", "01100", "00000" },
        ['('] = new[] { "00010", "00100", "01000", "01000", "01000", "00100", "00010" },
        [')'] = new[] { "01000", "00100", "00010", "00010", "00010", "00100", "01000" },
        [' '] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" },
    };
}
