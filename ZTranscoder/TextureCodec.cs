
using System;
using System.Collections.Generic;
using System.IO;
using AstcSharp;
using AstcSharp.Core;
using Texture2DDecoder;

internal static class TextureCodec
{

    public const int FmtRGB24 = 3;
    public const int FmtRGBA32 = 4;
    public const int FmtDXT1 = 10;
    public const int FmtDXT5 = 12;
    public const int FmtDXT5Crunched = 29;
    public const int FmtETC2_RGB = 45;
    public const int FmtETC2_RGBA8 = 47;
    public const int FmtASTC_RGBA_4x4 = 48;
    public const int FmtASTC_RGBA_6x6 = 50;
    public const int FmtASTC_RGBA_8x8 = 51;

    private const int FmtAlpha8 = 1;
    private const int FmtARGB4444 = 2;
    private const int FmtARGB32 = 5;
    private const int FmtRGB565 = 7;
    private const int FmtRGBA4444 = 13;
    private const int FmtBGRA32 = 14;
    private const int FmtBC6H = 24;
    private const int FmtBC7 = 25;
    private const int FmtBC4 = 26;
    private const int FmtBC5 = 27;
    private const int FmtDXT1Crunched = 28;
    private const int FmtPVRTC_RGB2 = 30;
    private const int FmtPVRTC_RGBA2 = 31;
    private const int FmtPVRTC_RGB4 = 32;
    private const int FmtPVRTC_RGBA4 = 33;
    private const int FmtETC_RGB4 = 34;
    private const int FmtATC_RGB4 = 35;
    private const int FmtATC_RGBA8 = 36;
    private const int FmtEAC_R = 41;
    private const int FmtEAC_R_SIGNED = 42;
    private const int FmtETC2_RGBA1 = 46;
    private const int FmtRG16 = 62;
    private const int FmtR8 = 63;
    private const int FmtETC_RGB4Crunched = 64;
    private const int FmtETC2_RGBA8Crunched = 65;

    private static readonly Dictionary<int, FootprintType> AstcFootprintsByFormat = new()
    {
        [FmtASTC_RGBA_4x4] = FootprintType.Footprint4x4,
        [FmtASTC_RGBA_6x6] = FootprintType.Footprint6x6,
        [FmtASTC_RGBA_8x8] = FootprintType.Footprint8x8,
    };

    private static readonly HashSet<int> KyaruNativeBgraFormats = new()
    {
        FmtDXT5,
        FmtDXT5Crunched,
        FmtETC2_RGB,
        FmtETC2_RGBA8,
        FmtETC_RGB4Crunched,
        FmtETC2_RGBA8Crunched,
        FmtETC_RGB4,
        FmtETC2_RGBA1,
        FmtEAC_R,
        FmtEAC_R_SIGNED,
        FmtATC_RGB4,
        FmtATC_RGBA8,
        FmtBC4,
        FmtBC5,
        FmtBC6H,
        FmtBC7,
        FmtPVRTC_RGB2,
        FmtPVRTC_RGBA2,
        FmtPVRTC_RGB4,
        FmtPVRTC_RGBA4,
    };

    private static readonly Dictionary<int, ChannelLayout> NativeChannelLayoutByFormat = BuildNativeChannelLayoutTable();

    private static Dictionary<int, ChannelLayout> BuildNativeChannelLayoutTable()
    {
        var table = new Dictionary<int, ChannelLayout>
        {
            [FmtBGRA32] = ChannelLayout.Bgra,
            [FmtARGB32] = ChannelLayout.Argb,
        };

        foreach (int format in KyaruNativeBgraFormats)
            table[format] = ChannelLayout.Bgra;

        return table;
    }

    private delegate bool KyaruDecodeFunc(ReadOnlySpan<byte> data, int width, int height, Span<byte> image);

    private static readonly Dictionary<int, KyaruDecodeFunc> KyaruDecodersByFormat = new()
    {
        [FmtETC_RGB4] = TextureDecoder.DecodeETC1,
        [FmtETC2_RGBA1] = TextureDecoder.DecodeETC2A1,
        [FmtEAC_R] = TextureDecoder.DecodeEACR,
        [FmtEAC_R_SIGNED] = TextureDecoder.DecodeEACRSigned,
        [FmtATC_RGB4] = TextureDecoder.DecodeATCRGB4,
        [FmtATC_RGBA8] = TextureDecoder.DecodeATCRGBA8,
        [FmtBC4] = TextureDecoder.DecodeBC4,
        [FmtBC5] = TextureDecoder.DecodeBC5,
        [FmtBC6H] = TextureDecoder.DecodeBC6,
        [FmtBC7] = TextureDecoder.DecodeBC7,
        [FmtPVRTC_RGB2] = (data, w, h, image) => TextureDecoder.DecodePVRTC(data, w, h, image, is2bpp: true),
        [FmtPVRTC_RGBA2] = (data, w, h, image) => TextureDecoder.DecodePVRTC(data, w, h, image, is2bpp: true),
        [FmtPVRTC_RGB4] = (data, w, h, image) => TextureDecoder.DecodePVRTC(data, w, h, image, is2bpp: false),
        [FmtPVRTC_RGBA4] = (data, w, h, image) => TextureDecoder.DecodePVRTC(data, w, h, image, is2bpp: false),
    };

    public static int ResolveOutputFormat(int sourceFormat, int requestedFormat) =>
        sourceFormat == FmtRGB24 ? FmtETC2_RGB : requestedFormat;

    public static string FormatName(int format) => format switch
    {
        FmtRGB24 => "RGB24",
        FmtRGBA32 => "RGBA32",
        FmtDXT1 => "DXT1",
        FmtDXT5 => "DXT5",
        FmtDXT5Crunched => "DXT5Crunched",
        FmtETC2_RGB => "ETC2_RGB",
        FmtETC2_RGBA8 => "ETC2_RGBA8",
        FmtASTC_RGBA_4x4 => "ASTC_RGBA_4x4",
        FmtASTC_RGBA_6x6 => "ASTC_RGBA_6x6",
        FmtASTC_RGBA_8x8 => "ASTC_RGBA_8x8",
        _ => $"Format{format}"
    };

    public static byte[] DecodeToRgba32(byte[] encodedData, int width, int height, int format, string texName)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"invalid dimensions for '{texName}': {width}x{height}");

        byte[] pixels = DecodeNative(encodedData, width, height, format, texName);
        ChannelLayout layout = NativeChannelLayoutByFormat.TryGetValue(format, out ChannelLayout mapped)
            ? mapped
            : ChannelLayout.Rgba;

        return ChannelSwizzle.ConvertToRgba(pixels, layout);
    }

    private static byte[] DecodeNative(byte[] encodedData, int width, int height, int format, string texName)
    {
        switch (format)
        {
            case FmtDXT1:
                return DecodeBC1(encodedData, width, height);

            case FmtDXT5:
                return DecodeKyaruDXT5(encodedData, width, height);

            case FmtDXT1Crunched:
                return DecodeBC1Crunched(encodedData, width, height);

            case FmtDXT5Crunched:
                return DecodeKyaruDXT5Crunched(encodedData, width, height);

            case FmtETC2_RGB:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: false);

            case FmtETC2_RGBA8:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: true);

            case FmtETC_RGB4Crunched:
                return DecodeKyaruCrunchedGeneric(encodedData, width, height, TextureDecoder.DecodeETC1, "ETC_RGB4Crunched");

            case FmtETC2_RGBA8Crunched:
                return DecodeKyaruCrunchedGeneric(encodedData, width, height, TextureDecoder.DecodeETC2A8, "ETC2_RGBA8Crunched");

            case FmtRGBA32:
                return DecodeRawRgba32(encodedData, width, height, texName);

            case FmtRGB24:
                return DecodeRGB24(encodedData, width, height);

            case FmtBGRA32:
                return DecodeBgra32(encodedData, width, height, texName);

            case FmtAlpha8:
                return DecodeAlpha8(encodedData, width, height);

            case FmtR8:
                return DecodeR8(encodedData, width, height);

            case FmtRG16:
                return DecodeRG16(encodedData, width, height);

            case FmtRGB565:
                return DecodeRgb565(encodedData, width, height);

            case FmtARGB32:
                return DecodeArgb32(encodedData, width, height);

            case FmtARGB4444:
                return DecodeArgb4444(encodedData, width, height);

            case FmtRGBA4444:
                return DecodeRgba4444(encodedData, width, height);
        }

        if (AstcFootprintsByFormat.TryGetValue(format, out FootprintType footprint))
            return DecodeAstc(encodedData, width, height, footprint, texName);

        if (KyaruDecodersByFormat.TryGetValue(format, out KyaruDecodeFunc? decodeFunc))
            return DecodeKyaruGeneric(encodedData, width, height, decodeFunc, FormatName(format));

        throw new NotSupportedException(
            $"TextureCodec has no decoder for format {format} ('{FormatName(format)}', texture '{texName}').");
    }

    private static byte[] DecodeRawRgba32(byte[] encodedData, int width, int height, string texName)
    {
        int expected = checked(width * height * 4);
        if (encodedData.Length < expected)
            throw new InvalidDataException(
                $"RGBA32 data too small for '{texName}': got {encodedData.Length}, expected at least {expected}");
        var rgba = new byte[expected];
        Buffer.BlockCopy(encodedData, 0, rgba, 0, expected);
        return rgba;
    }

    private static byte[] DecodeBgra32(byte[] encodedData, int width, int height, string texName)
    {
        int expected = checked(width * height * 4);
        if (encodedData.Length < expected)
            throw new InvalidDataException(
                $"BGRA32 data too small for '{texName}': got {encodedData.Length}, expected at least {expected}");
        var bgra = new byte[expected];
        Buffer.BlockCopy(encodedData, 0, bgra, 0, expected);
        return bgra;
    }

    private static byte[] DecodeAlpha8(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        if (data.Length < pixelCount)
            throw new InvalidDataException($"Alpha8 data too small: got {data.Length}, expected at least {pixelCount}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, dst = 0; i < pixelCount; i++, dst += 4)
            rgba[dst + 3] = data[i];
        return rgba;
    }

    private static byte[] DecodeR8(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        if (data.Length < pixelCount)
            throw new InvalidDataException($"R8 data too small: got {data.Length}, expected at least {pixelCount}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, dst = 0; i < pixelCount; i++, dst += 4)
        {
            rgba[dst + 0] = data[i];
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeRG16(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 2);
        if (data.Length < expected)
            throw new InvalidDataException($"RG16 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 2, dst += 4)
        {
            rgba[dst + 0] = data[src + 0];
            rgba[dst + 1] = data[src + 1];
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeRgb565(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 2);
        if (data.Length < expected)
            throw new InvalidDataException($"RGB565 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 2, dst += 4)
        {
            ushort c = (ushort)(data[src] | (data[src + 1] << 8));
            int r5 = (c >> 11) & 0x1F;
            int g6 = (c >> 5) & 0x3F;
            int b5 = c & 0x1F;
            rgba[dst + 0] = (byte)((r5 << 3) | (r5 >> 2));
            rgba[dst + 1] = (byte)((g6 << 2) | (g6 >> 4));
            rgba[dst + 2] = (byte)((b5 << 3) | (b5 >> 2));
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeArgb32(byte[] data, int width, int height)
    {
        int expected = checked(width * height * 4);
        if (data.Length < expected)
            throw new InvalidDataException($"ARGB32 data too small: got {data.Length}, expected at least {expected}");

        var argb = new byte[expected];
        Buffer.BlockCopy(data, 0, argb, 0, expected);
        return argb;
    }

    private static byte[] DecodeArgb4444(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 2);
        if (data.Length < expected)
            throw new InvalidDataException($"ARGB4444 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 2, dst += 4)
        {
            ushort c = (ushort)(data[src] | (data[src + 1] << 8));
            int a4 = (c >> 12) & 0xF;
            int r4 = (c >> 8) & 0xF;
            int g4 = (c >> 4) & 0xF;
            int b4 = c & 0xF;
            rgba[dst + 0] = (byte)((r4 << 4) | r4);
            rgba[dst + 1] = (byte)((g4 << 4) | g4);
            rgba[dst + 2] = (byte)((b4 << 4) | b4);
            rgba[dst + 3] = (byte)((a4 << 4) | a4);
        }
        return rgba;
    }

    private static byte[] DecodeRgba4444(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 2);
        if (data.Length < expected)
            throw new InvalidDataException($"RGBA4444 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 2, dst += 4)
        {
            ushort c = (ushort)(data[src] | (data[src + 1] << 8));
            int r4 = (c >> 12) & 0xF;
            int g4 = (c >> 8) & 0xF;
            int b4 = (c >> 4) & 0xF;
            int a4 = c & 0xF;
            rgba[dst + 0] = (byte)((r4 << 4) | r4);
            rgba[dst + 1] = (byte)((g4 << 4) | g4);
            rgba[dst + 2] = (byte)((b4 << 4) | b4);
            rgba[dst + 3] = (byte)((a4 << 4) | a4);
        }
        return rgba;
    }

    private static byte[] DecodeKyaruGeneric(byte[] encodedData, int width, int height, KyaruDecodeFunc decodeFunc, string formatLabel)
    {
        int outputSize = checked(width * height * 4);
        var rgba = new byte[outputSize];

        if (!decodeFunc(encodedData, width, height, rgba))
            throw new InvalidDataException($"Kyaru Texture2DDecoder failed to decode {formatLabel}");

        return rgba;
    }

    private static byte[] DecodeKyaruCrunchedGeneric(byte[] encodedData, int width, int height, KyaruDecodeFunc decodeFunc, string formatLabel)
    {
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException($"Kyaru Texture2DDecoder failed to unpack UnityCrunch data for {formatLabel}");

        return DecodeKyaruGeneric(unpacked, width, height, decodeFunc, formatLabel);
    }

    public static byte[] EncodeFromRgba32(byte[] rgba32, int width, int height, int outputFormat, string texName)
    {
        switch (outputFormat)
        {
            case FmtRGBA32:
                return rgba32;

            case FmtASTC_RGBA_4x4:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint4x4, texName);

            case FmtASTC_RGBA_6x6:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint6x6, texName);

            case FmtASTC_RGBA_8x8:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint8x8, texName);

            case FmtETC2_RGB:
            case FmtETC2_RGBA8:
                return Etc2Encoder.Encode(rgba32, width, height, outputFormat, texName);

            default:
                throw new NotSupportedException(
                    $"TextureCodec has no encoder for output format {outputFormat} ('{texName}').");
        }
    }

    private static byte[] DecodeRGB24(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 3);
        if (data.Length < expected)
            throw new InvalidDataException(
                $"RGB24 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 3, dst += 4)
        {
            rgba[dst + 0] = data[src + 0];
            rgba[dst + 1] = data[src + 1];
            rgba[dst + 2] = data[src + 2];
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeBC1(byte[] encodedData, int width, int height)
    {
        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        int expected = checked(blocksWide * blocksHigh * 8);
        if (encodedData.Length < expected)
            throw new InvalidDataException(
                $"DXT1 data too small: got {encodedData.Length}, expected at least {expected}");

        var rgba = new byte[checked(width * height * 4)];
        Span<byte> colorsR = stackalloc byte[4];
        Span<byte> colorsG = stackalloc byte[4];
        Span<byte> colorsB = stackalloc byte[4];
        Span<byte> colorsA = stackalloc byte[4];

        int blockIndex = 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int off = blockIndex * 8;
                blockIndex++;

                int c0 = encodedData[off] | (encodedData[off + 1] << 8);
                int c1 = encodedData[off + 2] | (encodedData[off + 3] << 8);
                uint indices = (uint)(encodedData[off + 4]
                    | (encodedData[off + 5] << 8)
                    | (encodedData[off + 6] << 16)
                    | (encodedData[off + 7] << 24));

                Unpack565(c0, out byte r0, out byte g0, out byte b0);
                Unpack565(c1, out byte r1, out byte g1, out byte b1);

                colorsR[0] = r0; colorsG[0] = g0; colorsB[0] = b0; colorsA[0] = 255;
                colorsR[1] = r1; colorsG[1] = g1; colorsB[1] = b1; colorsA[1] = 255;

                if (c0 > c1)
                {
                    colorsR[2] = (byte)((2 * r0 + r1) / 3);
                    colorsG[2] = (byte)((2 * g0 + g1) / 3);
                    colorsB[2] = (byte)((2 * b0 + b1) / 3);
                    colorsA[2] = 255;

                    colorsR[3] = (byte)((r0 + 2 * r1) / 3);
                    colorsG[3] = (byte)((g0 + 2 * g1) / 3);
                    colorsB[3] = (byte)((b0 + 2 * b1) / 3);
                    colorsA[3] = 255;
                }
                else
                {
                    colorsR[2] = (byte)((r0 + r1) / 2);
                    colorsG[2] = (byte)((g0 + g1) / 2);
                    colorsB[2] = (byte)((b0 + b1) / 2);
                    colorsA[2] = 255;

                    colorsR[3] = 0; colorsG[3] = 0; colorsB[3] = 0; colorsA[3] = 0;
                }

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) continue;

                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) continue;

                        int shift = (py * 4 + px) * 2;
                        int sel = (int)((indices >> shift) & 0x3);

                        int dst = (y * width + x) * 4;
                        rgba[dst + 0] = colorsR[sel];
                        rgba[dst + 1] = colorsG[sel];
                        rgba[dst + 2] = colorsB[sel];
                        rgba[dst + 3] = colorsA[sel];
                    }
                }
            }
        }

        return rgba;
    }

    private static void Unpack565(int value, out byte r, out byte g, out byte b)
    {
        int r5 = (value >> 11) & 0x1F;
        int g6 = (value >> 5) & 0x3F;
        int b5 = value & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    private static byte[] DecodeBC1Crunched(byte[] encodedData, int width, int height)
    {
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to unpack UnityCrunch DXT1 data");

        return DecodeBC1(unpacked, width, height);
    }

    private static byte[] DecodeKyaruDXT5(byte[] encodedData, int width, int height)
    {
        int outputSize = checked(width * height * 4);
        var rgba = new byte[outputSize];

        if (!TextureDecoder.DecodeDXT5(encodedData, width, height, rgba))
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to decode DXT5");

        return rgba;
    }

    private static byte[] DecodeKyaruDXT5Crunched(byte[] encodedData, int width, int height)
    {
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to unpack UnityCrunch DXT5 data");

        return DecodeKyaruDXT5(unpacked, width, height);
    }


    private static byte[] DecodeKyaruETC2(byte[] encodedData, int width, int height, bool hasAlpha)
    {
        int outputSize = checked(width * height * 4);
        var rgba = new byte[outputSize];

        bool ok = hasAlpha
            ? TextureDecoder.DecodeETC2A8(encodedData, width, height, rgba)
            : TextureDecoder.DecodeETC2(encodedData, width, height, rgba);

        if (!ok)
            throw new InvalidDataException(
                $"Kyaru Texture2DDecoder failed to decode {(hasAlpha ? "ETC2_RGBA8" : "ETC2_RGB")}");

        return rgba;
    }

    private static byte[] DecodeAstc(byte[] encodedData, int width, int height, FootprintType footprintType, string texName)
    {
        using var source = new MemoryStream(encodedData, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(footprintType);
        AstcDecoder.DecompressImage(source, destination, width, height, footprint);
        byte[] rgba32 = destination.ToArray();

        int expected = checked(width * height * 4);
        if (rgba32.Length != expected)
        {
            throw new InvalidDataException(
                $"ASTC decode size mismatch for '{texName}': got {rgba32.Length:N0}, expected {expected:N0}");
        }

        return rgba32;
    }

    private static byte[] EncodeAstc(byte[] rgba32, int width, int height, FootprintType footprintType, string texName)
    {
        int blockWidth = footprintType switch
        {
            FootprintType.Footprint4x4 => 4,
            FootprintType.Footprint6x6 => 6,
            FootprintType.Footprint8x8 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(footprintType), $"Unsupported ASTC footprint {footprintType}")
        };

        return NativeAstcEncoder.Encode(rgba32, width, height, blockWidth, blockWidth, texName);
    }

    public static byte[] DownsampleToGrid(byte[] rgba32, int width, int height, int gridSize)
    {
        var grid = new byte[gridSize * gridSize * 4];

        for (int gy = 0; gy < gridSize; gy++)
        {
            int y0 = (int)((long)gy * height / gridSize);
            int y1 = (int)((long)(gy + 1) * height / gridSize);
            if (y1 <= y0) y1 = y0 + 1;
            y1 = Math.Min(y1, height);

            for (int gx = 0; gx < gridSize; gx++)
            {
                int x0 = (int)((long)gx * width / gridSize);
                int x1 = (int)((long)(gx + 1) * width / gridSize);
                if (x1 <= x0) x1 = x0 + 1;
                x1 = Math.Min(x1, width);

                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * width * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowBase + x * 4;
                        sumR += rgba32[i + 0];
                        sumG += rgba32[i + 1];
                        sumB += rgba32[i + 2];
                        sumA += rgba32[i + 3];
                        count++;
                    }
                }

                int gi = (gy * gridSize + gx) * 4;
                grid[gi + 0] = (byte)(sumR / count);
                grid[gi + 1] = (byte)(sumG / count);
                grid[gi + 2] = (byte)(sumB / count);
                grid[gi + 3] = (byte)(sumA / count);
            }
        }

        return grid;
    }

    private const double kAlphaWeight = 0.25;

    public static double Percentile95CellDifference(byte[] gridA, byte[] gridB, int gridSize)
    {
        if (gridA.Length != gridB.Length)
            throw new ArgumentException("grids must be the same size to compare");

        int cellCount = gridSize * gridSize;
        int expected = checked(cellCount * 4);
        if (gridA.Length != expected)
            throw new ArgumentException(
                $"grid length {gridA.Length} doesn't match gridSize {gridSize} (expected {expected})");

        var cellScores = new double[cellCount];
        for (int c = 0; c < cellCount; c++)
        {
            int i = c * 4;
            double dr = Math.Abs(gridA[i + 0] - gridB[i + 0]);
            double dg = Math.Abs(gridA[i + 1] - gridB[i + 1]);
            double db = Math.Abs(gridA[i + 2] - gridB[i + 2]);
            double da = Math.Abs(gridA[i + 3] - gridB[i + 3]);
            cellScores[c] = (dr + dg + db + da * kAlphaWeight) / (3.0 + kAlphaWeight);
        }

        Array.Sort(cellScores);

        int rank = (int)Math.Ceiling(0.95 * cellCount) - 1;
        rank = Math.Clamp(rank, 0, cellCount - 1);
        return cellScores[rank];
    }

    public static byte[] ResampleBilinear(byte[] srcRgba32, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
            return srcRgba32;

        var dst = new byte[dstWidth * dstHeight * 4];

        for (int dy = 0; dy < dstHeight; dy++)
        {
            double sy = (dy + 0.5) * srcHeight / dstHeight - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;
            int y0c = Math.Clamp(y0, 0, srcHeight - 1);
            int y1c = Math.Clamp(y0 + 1, 0, srcHeight - 1);

            for (int dx = 0; dx < dstWidth; dx++)
            {
                double sx = (dx + 0.5) * srcWidth / dstWidth - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;
                int x0c = Math.Clamp(x0, 0, srcWidth - 1);
                int x1c = Math.Clamp(x0 + 1, 0, srcWidth - 1);

                int i00 = (y0c * srcWidth + x0c) * 4;
                int i10 = (y0c * srcWidth + x1c) * 4;
                int i01 = (y1c * srcWidth + x0c) * 4;
                int i11 = (y1c * srcWidth + x1c) * 4;

                int di = (dy * dstWidth + dx) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = srcRgba32[i00 + c] * (1 - fx) + srcRgba32[i10 + c] * fx;
                    double bot = srcRgba32[i01 + c] * (1 - fx) + srcRgba32[i11 + c] * fx;
                    dst[di + c] = (byte)Math.Round(top * (1 - fy) + bot * fy);
                }
            }
        }

        return dst;
    }
}

