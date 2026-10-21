using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace ServerMonitor.Services;

/// <summary>
/// Ana ekran / PWA simgesini (koyu zemin uzerinde yesil nabiz cizgisi) calisma aninda PNG olarak cizer.
/// iOS apple-touch-icon icin SVG kabul etmedigi ve harici gorsel dosyasi tasimak istemedigimiz icin.
/// Her boyut bir kez cizilip bellekte tutulur.
/// </summary>
public static class IconRenderer
{
    /// <summary>Istenebilecek boyutlar: 180 iPhone, 192/512 manifest.</summary>
    public static readonly int[] AllowedSizes = { 180, 192, 512 };

    private static readonly ConcurrentDictionary<int, byte[]> Cache = new();

    public static byte[] GetPng(int size) => Cache.GetOrAdd(size, Render);

    private static byte[] Render(int size)
    {
        double s = size;
        var rgb = new byte[size * size * 3];

        // Nabiz cizgisi (0-1 araliginda). Android "maskable" kirpmasina karsi merkezdeki %80'lik alanda kaliyor.
        (double X, double Y)[] shape =
        {
            (0.16, 0.56), (0.35, 0.56), (0.43, 0.36), (0.53, 0.74),
            (0.61, 0.46), (0.67, 0.56), (0.80, 0.56)
        };

        var points = shape.Select(p => (X: p.X * s, Y: p.Y * s)).ToArray();
        var halfWidth = 0.03 * s;

        var dotX = 0.80 * s;
        var dotY = 0.56 * s;
        var dotRadius = 0.058 * s;
        var glowRadius = 0.13 * s;

        // Renkler
        (double R, double G, double B) top = (0x24, 0x2f, 0x3c);
        (double R, double G, double B) bottom = (0x0d, 0x12, 0x17);
        (double R, double G, double B) line = (0x3f, 0xb9, 0x50);
        (double R, double G, double B) dot = (0x6e, 0xe7, 0x7e);

        for (var y = 0; y < size; y++)
        {
            var t = size == 1 ? 0 : y / (s - 1);
            (double R, double G, double B) bg = (Lerp(top.R, bottom.R, t), Lerp(top.G, bottom.G, t), Lerp(top.B, bottom.B, t));

            for (var x = 0; x < size; x++)
            {
                var px = x + 0.5;
                var py = y + 0.5;
                var color = bg;

                // Noktanin cevresindeki yumusak parilti
                var dotDistance = Distance(px, py, dotX, dotY);
                var glow = Math.Clamp(1 - (dotDistance - dotRadius) / (glowRadius - dotRadius), 0, 1);
                color = Blend(color, line, glow * glow * 0.35);

                // Cizgi: en yakin parcaya olan mesafeden kenar yumusatmali kapsama
                var lineDistance = double.MaxValue;
                for (var i = 0; i < points.Length - 1; i++)
                {
                    lineDistance = Math.Min(lineDistance,
                        DistanceToSegment(px, py, points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
                }
                color = Blend(color, line, Math.Clamp(halfWidth + 0.5 - lineDistance, 0, 1));

                // Uc nokta
                color = Blend(color, dot, Math.Clamp(dotRadius + 0.5 - dotDistance, 0, 1));

                var offset = (y * size + x) * 3;
                rgb[offset] = ToByte(color.R);
                rgb[offset + 1] = ToByte(color.G);
                rgb[offset + 2] = ToByte(color.B);
            }
        }

        return EncodePng(size, size, rgb);
    }

    // --- Cizim yardimcilari ---

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static (double R, double G, double B) Blend((double R, double G, double B) under, (double R, double G, double B) over, double alpha) =>
        (Lerp(under.R, over.R, alpha), Lerp(under.G, over.G, alpha), Lerp(under.B, over.B, alpha));

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var vx = bx - ax;
        var vy = by - ay;
        var lengthSquared = vx * vx + vy * vy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / lengthSquared, 0, 1);
        return Distance(px, py, ax + t * vx, ay + t * vy);
    }

    // --- En kucuk PNG kodlayici (RGB, 8 bit, filtresiz satirlar + zlib) ---

    private static byte[] EncodePng(int width, int height, byte[] rgb)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // satir filtresi: yok
                zlib.Write(rgb, y * width * 3, width * 3);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;  // bit derinligi
        header[9] = 2;  // renk tipi: RGB
        header[10] = 0; // sikistirma
        header[11] = 0; // filtre
        header[12] = 0; // interlace yok

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[4];
        var typeBytes = Encoding.ASCII.GetBytes(type);

        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer, 0, 4);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeBytes, data));
        stream.Write(buffer, 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
