using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SharpCompress.Compressors.Xz;

internal static class Carra2Mode
{
    private const int MaxNameBytes = 4096;
    private const int MaxHeaderScanInts = 48;
    private const int MaxTextureDimension = 16384;

    private static ReadOnlySpan<byte> XzMagic => new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };

    private sealed record Carra2Entry(string EntryName, long PathId, byte[] Payload);

    private sealed class Options
    {
        public string Carra2Path = "";
        public string OriginalPath = "";
        public string OutputPath = "";
        public string? TpkPath;
        public bool DryRun;
        public int? NewTextureFormat;
    }

    public static int Run(string[] modeArgs)
    {
        Options? opt = ParseArgs(modeArgs);
        if (opt == null)
        {
            Console.Error.WriteLine(
                "usage: ZTranscoder carra2 <carra2.zip> <original.bundle> <output.bundle> " +
                "[--new-texture-format FMT] [--dry-run] [classdata.tpk]");
            return 2;
        }

        Console.WriteLine(
            $"[config] dry-run={opt.DryRun} " +
            $"new-texture-format={(opt.NewTextureFormat is int fmt ? TextureCodec.FormatName(fmt) : "<match original>")}");

        List<Carra2Entry> entries;
        try
        {
            entries = ReadCarra2Entries(opt.Carra2Path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[carra2] failed to open '{opt.Carra2Path}': {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"[carra2] Loaded {entries.Count} usable entry(ies) from '{opt.Carra2Path}'.");

        var entriesByPathId = new Dictionary<long, List<Carra2Entry>>();
        foreach (Carra2Entry e in entries)
        {
            if (!entriesByPathId.TryGetValue(e.PathId, out List<Carra2Entry>? bucket))
            {
                bucket = new List<Carra2Entry>();
                entriesByPathId[e.PathId] = bucket;
            }
            bucket.Add(e);
        }

        var matchedPathIds = new HashSet<long>();

        var manager = new AssetsManager();
        if (opt.TpkPath != null)
            manager.LoadClassPackage(opt.TpkPath);

        string? tempUnpacked = null;
        int spritesOverridden = 0, spritesSkippedErrors = 0;
        int texturesReencoded = 0, texturesSkippedErrors = 0;
        int touchedFiles = 0;

        try
        {
            BundleFileInstance bunInst = LoadFullyUnpacked(manager, opt.OriginalPath, "ZTranscoder-carra2", out tempUnpacked);

            for (int dirIndex = 0; dirIndex < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
            {
                var dirInfo = bunInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex];
                if (!LooksLikeSerializedFile(dirInfo.Name))
                    continue;

                AssetsFileInstance? afileInst;
                try
                {
                    afileInst = manager.LoadAssetsFileFromBundle(bunInst, dirIndex, loadDeps: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{dirInfo.Name}] skipped: could not load as a SerializedFile ({ex.GetType().Name}: {ex.Message}).");
                    continue;
                }

                if (afileInst?.file == null)
                {
                    Console.WriteLine($"[{dirInfo.Name}] skipped: not a readable SerializedFile.");
                    continue;
                }

                AssetsFile af = afileInst.file;
                bool fileTouched = false;

                var spriteByPathId = new Dictionary<long, AssetFileInfo>();
                foreach (AssetFileInfo info in af.GetAssetsOfType(AssetClassID.Sprite))
                    spriteByPathId[info.PathId] = info;

                var textureByPathId = new Dictionary<long, AssetFileInfo>();
                foreach (AssetFileInfo info in af.GetAssetsOfType(AssetClassID.Texture2D))
                    textureByPathId[info.PathId] = info;

                foreach (KeyValuePair<long, List<Carra2Entry>> kv in entriesByPathId)
                {
                    if (!spriteByPathId.TryGetValue(kv.Key, out AssetFileInfo? spriteInfo))
                        continue;

                    if (!matchedPathIds.Add(kv.Key))
                    {
                        Console.WriteLine(
                            $"[{dirInfo.Name}] Sprite PathId {kv.Key}: also matched in an earlier SerializedFile - " +
                            "leaving this occurrence untouched to avoid an ambiguous double-apply.");
                        continue;
                    }

                    try
                    {
                        Carra2Entry? source = null;
                        string spriteName = "";
                        float[] f = Array.Empty<float>();
                        foreach (Carra2Entry candidate in kv.Value)
                        {
                            if (TryParseSpritePayload(candidate.Payload, out spriteName, out f))
                            {
                                source = candidate;
                                break;
                            }
                        }

                        if (source == null)
                        {
                            Console.WriteLine(
                                $"[{dirInfo.Name}] Sprite PathId {kv.Key} ('{kv.Value[0].EntryName}'): no payload " +
                                "matches the expected name+13-float layout - skipped.");
                            spritesSkippedErrors++;
                            continue;
                        }

                        AssetTypeValueField spriteBase = manager.GetBaseField(afileInst, spriteInfo);

                        Console.WriteLine(
                            $"[{dirInfo.Name}] Sprite '{spriteName}' PathId {kv.Key}: overriding rect/offset/border/pivot.");

                        if (!opt.DryRun)
                        {
                            AssetTypeValueField rect = spriteBase["m_Rect"];
                            rect["x"].AsFloat = f[0];
                            rect["y"].AsFloat = f[1];
                            rect["width"].AsFloat = f[2];
                            rect["height"].AsFloat = f[3];

                            AssetTypeValueField spriteOffset = spriteBase["m_Offset"];
                            spriteOffset["x"].AsFloat = f[4];
                            spriteOffset["y"].AsFloat = f[5];

                            AssetTypeValueField border = spriteBase["m_Border"];
                            border["x"].AsFloat = f[6];
                            border["y"].AsFloat = f[7];
                            border["z"].AsFloat = f[8];
                            border["w"].AsFloat = f[9];

                            spriteBase["m_PixelsToUnits"].AsFloat = f[10];

                            AssetTypeValueField pivot = spriteBase["m_Pivot"];
                            pivot["x"].AsFloat = f[11];
                            pivot["y"].AsFloat = f[12];

                            spriteInfo.SetNewData(spriteBase);
                        }

                        spritesOverridden++;
                        fileTouched = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[{dirInfo.Name}] Sprite PathId {kv.Key}: skipped due to error - {ex.GetType().Name}: {ex.Message}");
                        spritesSkippedErrors++;
                    }
                }

                foreach (KeyValuePair<long, List<Carra2Entry>> kv in entriesByPathId)
                {
                    if (!textureByPathId.TryGetValue(kv.Key, out AssetFileInfo? texInfo))
                        continue;

                    if (!matchedPathIds.Add(kv.Key))
                    {
                        Console.WriteLine(
                            $"[{dirInfo.Name}] Texture2D PathId {kv.Key}: also matched in an earlier SerializedFile - " +
                            "leaving this occurrence untouched to avoid an ambiguous double-apply.");
                        continue;
                    }

                    try
                    {
                        Carra2Entry? source = null;
                        string texName = "";
                        int width = 0, height = 0, dataSize = 0, format = 0;
                        byte[] pixelData = Array.Empty<byte>();
                        foreach (Carra2Entry candidate in kv.Value)
                        {
                            if (TryParseTexturePayload(
                                    candidate.Payload, out texName, out width, out height,
                                    out dataSize, out format, out pixelData))
                            {
                                source = candidate;
                                break;
                            }
                        }

                        if (source == null)
                        {
                            Console.WriteLine(
                                $"[{dirInfo.Name}] Texture2D PathId {kv.Key} ('{kv.Value[0].EntryName}'): no payload " +
                                "contains a recognizable Texture2D header with a supported format " +
                                "(likely a placeholder record) - skipped.");
                            texturesSkippedErrors++;
                            continue;
                        }

                        AssetTypeValueField texBase = manager.GetBaseField(afileInst, texInfo);
                        int targetFormat = opt.NewTextureFormat ?? texBase["m_TextureFormat"].AsInt;
                        targetFormat = TextureCodec.ResolveOutputFormat(format, targetFormat);

                        int origWidth = texBase["m_Width"].AsInt;
                        int origHeight = texBase["m_Height"].AsInt;
                        int finalWidth = origWidth > 0 ? origWidth : width;
                        int finalHeight = origHeight > 0 ? origHeight : height;

                        byte[] rgba32 = TextureCodec.DecodeToRgba32(pixelData, width, height, format, texName);

                        if (finalWidth != width || finalHeight != height)
                        {
                            Console.WriteLine(
                                $"[{dirInfo.Name}] Texture2D '{texName}' PathId {kv.Key}: mod payload is " +
                                $"{width}x{height}, but the original bundle texture is {finalWidth}x{finalHeight} - " +
                                "resampling to match the original resolution.");
                            rgba32 = TextureCodec.ResampleBilinear(rgba32, width, height, finalWidth, finalHeight);
                        }

                        byte[] encoded = TextureCodec.EncodeFromRgba32(rgba32, finalWidth, finalHeight, targetFormat, texName);

                        Console.WriteLine(
                            $"[{dirInfo.Name}] Texture2D '{texName}' PathId {kv.Key}: {width}x{height} " +
                            $"{TextureCodec.FormatName(format)} (dataSize={dataSize:N0}) -> {finalWidth}x{finalHeight} " +
                            $"{TextureCodec.FormatName(targetFormat)}.");

                        if (!opt.DryRun)
                        {
                            texBase["m_TextureFormat"].AsInt = targetFormat;
                            texBase["m_Width"].AsInt = finalWidth;
                            texBase["m_Height"].AsInt = finalHeight;
                            texBase["m_MipCount"].AsInt = 1;
                            texBase["m_CompleteImageSize"].AsInt = encoded.Length;

                            AssetTypeValueField streamData = texBase["m_StreamData"];
                            streamData["offset"].AsULong = 0;
                            streamData["size"].AsInt = 0;
                            streamData["path"].AsString = string.Empty;
                            texBase["image data"].AsByteArray = encoded;

                            texInfo.SetNewData(texBase);
                        }

                        texturesReencoded++;
                        fileTouched = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[{dirInfo.Name}] Texture2D PathId {kv.Key}: skipped due to error - {ex.GetType().Name}: {ex.Message}");
                        texturesSkippedErrors++;
                    }
                }

                if (fileTouched && !opt.DryRun)
                {
                    dirInfo.SetNewData(af);
                    touchedFiles++;
                }
                else if (fileTouched)
                {
                    touchedFiles++;
                }
            }

            foreach (KeyValuePair<long, List<Carra2Entry>> kv in entriesByPathId)
            {
                if (!matchedPathIds.Contains(kv.Key))
                    Console.WriteLine(
                        $"[carra2] PathId {kv.Key} ('{kv.Value[0].EntryName}'): no matching Sprite or Texture2D found " +
                        "anywhere in the original bundle - skipped.");
            }

            Console.WriteLine(
                $"[summary] Sprites: {spritesOverridden} overridden, {spritesSkippedErrors} skipped due to errors. " +
                $"Textures: {texturesReencoded} re-encoded, {texturesSkippedErrors} skipped due to errors. " +
                $"{entriesByPathId.Count - matchedPathIds.Count} PathId(s) unmatched. " +
                $"{touchedFiles} SerializedFile(s) touched.");

            if (opt.DryRun)
            {
                Console.WriteLine("[dry-run] no output written.");
                return 0;
            }

            WritePackedOutput(bunInst, opt.OutputPath);
            Console.WriteLine($"Wrote {opt.OutputPath}.");
            return 0;
        }
        finally
        {
            manager.UnloadAll();
            if (tempUnpacked != null) TryDelete(tempUnpacked);
        }
    }

    private static List<Carra2Entry> ReadCarra2Entries(string carra2Path)
    {
        var result = new List<Carra2Entry>();
        using ZipArchive archive = ZipFile.OpenRead(carra2Path);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            if (!TryParsePathId(entry.Name, out long pathId))
            {
                Console.WriteLine($"[carra2] entry '{entry.FullName}': no PathID at the start of '{entry.Name}' - skipped.");
                continue;
            }

            byte[] payload;
            try
            {
                payload = ReadEntryPayload(entry);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[carra2] entry '{entry.FullName}': could not read payload ({ex.GetType().Name}: {ex.Message}) - skipped.");
                continue;
            }

            result.Add(new Carra2Entry(entry.FullName, pathId, payload));
        }

        return result;
    }

    private static bool TryParsePathId(string fileName, out long pathId)
    {
        int dot = fileName.IndexOf('.');
        string stem = dot < 0 ? fileName : fileName[..dot];
        return long.TryParse(stem, out pathId);
    }

    private static byte[] ReadEntryPayload(ZipArchiveEntry entry)
    {
        byte[] raw;
        using (Stream entryStream = entry.Open())
        using (var rawMem = new MemoryStream())
        {
            entryStream.CopyTo(rawMem);
            raw = rawMem.ToArray();
        }

        if (!raw.AsSpan().StartsWith(XzMagic))
            return raw;

        using var xz = new XZStream(new MemoryStream(raw));
        using var mem = new MemoryStream();
        xz.CopyTo(mem);
        return mem.ToArray();
    }

    private static int Pad4(int n) => (n + 3) & ~3;

    private static int ReadInt(byte[] payload, int start, int index) =>
        BitConverter.ToInt32(payload, start + index * 4);

    private static bool TryReadName(byte[] payload, out string name, out int afterName)
    {
        name = "";
        afterName = 0;

        if (payload.Length < 4)
            return false;

        int nameLen = BitConverter.ToInt32(payload, 0);
        if (nameLen < 0 || nameLen > MaxNameBytes || 4 + Pad4(nameLen) > payload.Length)
            return false;

        ReadOnlySpan<byte> raw = payload.AsSpan(4, nameLen);
        foreach (byte b in raw)
        {
            if (b < 0x20)
                return false;
        }

        name = Encoding.UTF8.GetString(raw);
        afterName = 4 + Pad4(nameLen);
        return true;
    }

    private static bool TryParseSpritePayload(byte[] payload, out string name, out float[] fields)
    {
        name = "";
        fields = Array.Empty<float>();

        if (payload.Length < 4)
            return false;

        int nameLen = BitConverter.ToInt32(payload, 0);
        if (nameLen < 0 || 4 + nameLen > payload.Length)
            return false;

        string parsedName = Encoding.UTF8.GetString(payload, 4, nameLen);
        int off = 4 + Pad4(nameLen);
        if (off + 13 * 4 > payload.Length)
            return false;

        var parsed = new float[13];
        for (int i = 0; i < 13; i++)
        {
            parsed[i] = BitConverter.ToSingle(payload, off + i * 4);
            if (!float.IsFinite(parsed[i]))
                return false;
        }

        name = parsedName;
        fields = parsed;
        return true;
    }

    private static bool TryParseTexturePayload(
        byte[] payload, out string name, out int width, out int height,
        out int dataSize, out int format, out byte[] pixelData)
    {
        name = "";
        width = height = dataSize = format = 0;
        pixelData = Array.Empty<byte>();

        int scanStart = 0;
        if (TryReadName(payload, out string parsedName, out int afterName))
        {
            name = parsedName;
            scanStart = afterName;
        }

        bool located = TryLocateTextureHeader(payload, scanStart, out width, out height, out dataSize, out format);
        if (!located && scanStart != 0)
            located = TryLocateTextureHeader(payload, 0, out width, out height, out dataSize, out format);

        if (!located)
            return false;

        pixelData = new byte[dataSize];
        Buffer.BlockCopy(payload, payload.Length - dataSize, pixelData, 0, dataSize);
        return true;
    }

    private static bool TryLocateTextureHeader(
        byte[] payload, int start, out int width, out int height, out int dataSize, out int format)
    {
        width = height = dataSize = format = 0;

        int intCount = Math.Min(MaxHeaderScanInts, (payload.Length - start) / 4);

        for (int k = 0; k + 4 <= intCount; k++)
        {
            int w = ReadInt(payload, start, k);
            int h = ReadInt(payload, start, k + 1);
            int size = ReadInt(payload, start, k + 2);

            if (w <= 0 || h <= 0 || w > MaxTextureDimension || h > MaxTextureDimension || size <= 0)
                continue;

            for (int formatIndex = k + 4; formatIndex >= k + 3; formatIndex--)
            {
                if (formatIndex >= intCount)
                    continue;

                int candidateFormat = ReadInt(payload, start, formatIndex);
                if (!TextureCodec.TryGetLevel0Size(candidateFormat, w, h, out long level0))
                    continue;

                long headerEnd = start + (formatIndex + 1L) * 4;
                if (size > payload.Length - headerEnd)
                    continue;

                if (size < level0 || (level0 > 0 && size > level0 * 2 + 1024))
                    continue;

                width = w;
                height = h;
                dataSize = size;
                format = candidateFormat;
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

    private static BundleFileInstance LoadFullyUnpacked(
        AssetsManager manager, string path, string tempPrefix, out string? tempPath)
    {
        tempPath = null;
        BundleFileInstance loaded = manager.LoadBundleFile(path, unpackIfPacked: false);

        AssetBundleCompressionType compression = loaded.file.GetCompressionType();
        if (compression == AssetBundleCompressionType.None)
        {
            Console.WriteLine($"[bundle] '{path}' is already uncompressed.");
            return loaded;
        }

        string unpackedPath = Path.Combine(Path.GetTempPath(), $"{tempPrefix}-{Guid.NewGuid():N}.unity3d");
        using (var unpackedStream = File.Create(unpackedPath))
        using (var unpackedWriter = new AssetsFileWriter(unpackedStream))
        {
            loaded.file.Unpack(unpackedWriter);
        }

        manager.UnloadBundleFile(loaded);
        tempPath = unpackedPath;

        BundleFileInstance reloaded = manager.LoadBundleFile(unpackedPath, unpackIfPacked: false);
        if (reloaded.file.GetCompressionType() != AssetBundleCompressionType.None || reloaded.file.DataIsCompressed)
            throw new InvalidDataException($"failed to fully decompress '{path}'");

        Console.WriteLine($"[bundle] '{path}' decompressed ({compression} -> None).");
        return reloaded;
    }

    private static void WritePackedOutput(BundleFileInstance bunInst, string outputPath)
    {
        string tempUnpackedPath = Path.Combine(Path.GetTempPath(), $"ZTranscoder-carra2-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(Path.GetTempPath(), $"ZTranscoder-carra2-packed-{Guid.NewGuid():N}.unity3d");

        try
        {
            using (var tempStream = File.Create(tempUnpackedPath))
            using (var tempWriter = new AssetsFileWriter(tempStream))
            {
                bunInst.file.Write(tempWriter, 0);
            }

            var packManager = new AssetsManager();
            try
            {
                BundleFileInstance materializedInst = packManager.LoadBundleFile(tempUnpackedPath, unpackIfPacked: false);
                using var packedStream = File.Create(tempPackedPath);
                using var packedWriter = new AssetsFileWriter(packedStream);
                materializedInst.file.Pack(packedWriter, AssetBundleCompressionType.LZ4);
            }
            finally
            {
                packManager.UnloadAll();
            }

            byte[] packedBytes = File.ReadAllBytes(tempPackedPath);
            byte[] forcedLz4Bytes = UnityFsLz4Transcoder.ForceStandardLz4(packedBytes);
            File.WriteAllBytes(outputPath, forcedLz4Bytes);
        }
        finally
        {
            TryDelete(tempUnpackedPath);
            TryDelete(tempPackedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static Options? ParseArgs(string[] args)
    {
        var positional = new List<string>(args);
        var opt = new Options();

        for (int i = 0; i < positional.Count; i++)
        {
            if (string.Equals(positional[i], "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                opt.DryRun = true;
                positional.RemoveAt(i);
                i--;
            }
            else if (string.Equals(positional[i], "--new-texture-format", StringComparison.OrdinalIgnoreCase) && i + 1 < positional.Count)
            {
                opt.NewTextureFormat = ParseFormatName(positional[i + 1]);
                positional.RemoveRange(i, 2);
                i--;
            }
        }

        if (positional.Count < 3)
            return null;

        opt.Carra2Path = positional[0];
        opt.OriginalPath = positional[1];
        opt.OutputPath = positional[2];
        if (positional.Count >= 4)
            opt.TpkPath = positional[3];

        return opt;
    }

    private static int ParseFormatName(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RGBA32" => TextureCodec.FmtRGBA32,
        "ASTC_RGBA_4X4" => TextureCodec.FmtASTC_RGBA_4x4,
        "ASTC_RGBA_6X6" => TextureCodec.FmtASTC_RGBA_6x6,
        "ASTC_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC_RGBA_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ETC2" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGBA8" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGB" => TextureCodec.FmtETC2_RGB,
        _ => throw new ArgumentException(
            $"Unknown --new-texture-format '{value}'. Use RGBA32, ETC2, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6, or ASTC_RGBA_8x8.")
    };
}
