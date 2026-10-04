using System.Buffers.Binary;
using System.Text;

namespace PsdTachieNext.Tests;

// Independent synthetic PSD/PSB writer. No user asset or production parser code is copied.
internal static class PsdFixture
{
    public const int Width = 8, Height = 4;
    public static byte[] Pixels(byte seed = 11)
    {
        var b = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            b[i * 4] = (byte)(seed + i); b[i * 4 + 1] = (byte)(200 - i);
            b[i * 4 + 2] = (byte)(50 + i); b[i * 4 + 3] = (byte)(i * 8);
        }
        return b;
    }
    public static byte[] MaskPixels() => Enumerable.Range(0, Width * Height).Select(i => (byte)(i * 7)).ToArray();
    private sealed record Layer(string Name, bool Hidden = false, int Divider = 0, bool Mask = false,
        byte Opacity = 255, string Blend = "norm", bool Clipping = false, byte Seed = 11, string? Unicode = null);

    public static void Write(string path, bool psb = false, bool rle = false, bool groups = false, byte seed = 11,
        string visibleName = "visible", string hiddenName = "hidden", int[]? layerIds = null,
        string[]? simpleLayerNames = null, bool[]? simpleLayerVisible = null)
    {
        // Top-to-bottom logical order; PSD file stores reversed record order.
        var ordered = simpleLayerNames is not null
            ? simpleLayerNames.Select((name,i)=>new Layer(name, Hidden: !simpleLayerVisible![i], Seed: (byte)(11+i*17))).ToArray()
            : groups
            ? new[] { new Layer("Group", Divider: 1, Opacity: 191, Blend: "pass"),
                new Layer("eye", Mask: true, Opacity: 173, Blend: "mul ", Clipping: true, Seed: seed, Unicode: "目/％"),
                new Layer("eye", Hidden: true, Seed: 23), new Layer("End", Divider: 3), new Layer("body", Seed: 33) }
            : new[] { new Layer(visibleName, Seed: seed), new Layer(hiddenName, Hidden: true, Seed: 23) };
        using var records = new MemoryStream(); using var data = new MemoryStream();
        foreach (var layer in ordered.Reverse())
        {
            var folder = layer.Divider != 0;
            var channels = folder ? Array.Empty<short>() : layer.Mask ? new short[] { 0, 1, 2, -1, -2 } : [0, 1, 2, -1];
            var payloads = new List<byte[]>();
            foreach (var c in channels)
            {
                var pixels = Pixels(layer.Seed); var channel = new byte[Width * Height];
                for (var i = 0; i < channel.Length; i++) channel[i] = c switch
                { 0 => pixels[i * 4 + 2], 1 => pixels[i * 4 + 1], 2 => pixels[i * 4], -1 => pixels[i * 4 + 3], _ => MaskPixels()[i] };
                using var encoded = new MemoryStream(); U16(encoded, rle ? 1 : 0);
                if (rle)
                {
                    for (var y = 0; y < Height; y++) { if (psb) U32(encoded, Width + 1); else U16(encoded, Width + 1); }
                    for (var y = 0; y < Height; y++) { encoded.WriteByte(Width - 1); encoded.Write(channel, y * Width, Width); }
                }
                else encoded.Write(channel);
                payloads.Add(encoded.ToArray());
            }
            I32(records, folder ? 0 : -2); I32(records, folder ? 0 : 3);
            I32(records, folder ? 0 : Height - 2); I32(records, folder ? 0 : Width + 3);
            U16(records, channels.Length);
            for (var i = 0; i < channels.Length; i++) { U16(records, unchecked((ushort)channels[i])); Length(records, payloads[i].Length, psb); }
            Ascii(records, "8BIM"); Ascii(records, layer.Blend); records.WriteByte(layer.Opacity);
            records.WriteByte(layer.Clipping ? (byte)1 : (byte)0); records.WriteByte(layer.Hidden ? (byte)2 : (byte)0); records.WriteByte(0);
            using var extra = new MemoryStream();
            if (layer.Mask)
            {
                U32(extra, 20); I32(extra, -2); I32(extra, 3); I32(extra, Height - 2); I32(extra, Width + 3);
                extra.WriteByte(255); extra.WriteByte(0); U16(extra, 0);
            }
            else U32(extra, 0);
            U32(extra, 0); Pascal(extra, layer.Name);
            if (folder)
            {
                using var tag = new MemoryStream(); U32(tag, layer.Divider); Ascii(tag, "8BIM"); Ascii(tag, layer.Blend);
                Tag(extra, "lsct", tag.ToArray());
            }
            if (layer.Unicode is not null)
            {
                using var tag = new MemoryStream(); U32(tag, layer.Unicode.Length); tag.Write(Encoding.BigEndianUnicode.GetBytes(layer.Unicode));
                Tag(extra, "luni", tag.ToArray());
            }
            if(layerIds is not null)
            {
                using var tag=new MemoryStream();U32(tag,layerIds[Array.IndexOf(ordered,layer)]);Tag(extra,"lyid",tag.ToArray());
            }
            U32(records, checked((int)extra.Length)); extra.Position = 0; extra.CopyTo(records);
            foreach (var payload in payloads) data.Write(payload);
        }
        using var layerInfo = new MemoryStream(); U16(layerInfo, ordered.Length); records.Position = 0; records.CopyTo(layerInfo);
        data.Position = 0; data.CopyTo(layerInfo); if ((layerInfo.Length & 1) != 0) layerInfo.WriteByte(0);
        using var maskSection = new MemoryStream(); Length(maskSection, layerInfo.Length, psb);
        layerInfo.Position = 0; layerInfo.CopyTo(maskSection); U32(maskSection, 0);
        using var output = File.Create(path); Ascii(output, "8BPS"); U16(output, psb ? 2 : 1); output.Write(new byte[6]);
        U16(output, 4); U32(output, Height); U32(output, Width); U16(output, 8); U16(output, 3);
        U32(output, 0); U32(output, 0); Length(output, maskSection.Length, psb);
        maskSection.Position = 0; maskSection.CopyTo(output); U16(output, 0); output.Write(new byte[Width * Height * 4]);
    }
    private static void Tag(Stream s, string key, byte[] payload)
    { Ascii(s, "8BIM"); Ascii(s, key); U32(s, payload.Length); s.Write(payload); if ((payload.Length & 1) != 0) s.WriteByte(0); }
    private static void Pascal(Stream s, string text)
    { var b = Encoding.ASCII.GetBytes(text); s.WriteByte((byte)b.Length); s.Write(b); for (var i = b.Length + 1; i % 4 != 0; i++) s.WriteByte(0); }
    private static void Ascii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
    private static void U16(Stream s, int value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value); s.Write(b); }
    private static void U32(Stream s, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value); s.Write(b); }
    private static void I32(Stream s, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); s.Write(b); }
    private static void Length(Stream s, long value, bool psb)
    { if (!psb) U32(s, checked((int)value)); else { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); s.Write(b); } }
}
