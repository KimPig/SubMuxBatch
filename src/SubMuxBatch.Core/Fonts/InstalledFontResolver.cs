using System.Buffers.Binary;
using System.Text;

namespace SubMuxBatch.Core.Fonts;

public sealed record FontAttachmentFile(string FilePath, string MimeType)
{
    public string FileName => Path.GetFileName(FilePath);
    public long Size => new FileInfo(FilePath).Length;
}

public interface IInstalledFontResolver
{
    IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName);
}

public sealed class InstalledFontResolver : IInstalledFontResolver
{
    private static readonly string[] SupportedExtensions = [".ttf", ".otf", ".ttc", ".otc"];
    private readonly Lazy<IReadOnlyList<IndexedFontFile>> _fontFiles;

    public InstalledFontResolver(IEnumerable<string>? fontDirectories = null)
    {
        var directories = (fontDirectories ?? GetWindowsFontDirectories())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _fontFiles = new Lazy<IReadOnlyList<IndexedFontFile>>(
            () => BuildIndex(directories),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static IInstalledFontResolver System { get; } = new InstalledFontResolver();

    public IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return [];
        }

        var normalizedFamily = NormalizeName(familyName);
        return _fontFiles.Value
            .Where(font => font.FamilyNames.Contains(normalizedFamily))
            .Select(static font => new FontAttachmentFile(font.FilePath, GetMimeType(font.FilePath)))
            .DistinctBy(static font => font.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static font => font.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetWindowsFontDirectories()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "Fonts");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
        }
    }

    private static IReadOnlyList<IndexedFontFile> BuildIndex(IEnumerable<string> directories)
    {
        var result = new List<IndexedFontFile>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.Where(static path => SupportedExtensions.Contains(
                         Path.GetExtension(path),
                         StringComparer.OrdinalIgnoreCase)))
            {
                try
                {
                    var familyNames = OpenTypeFamilyNameReader.Read(file)
                        .Select(NormalizeName)
                        .Where(static name => name.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (familyNames.Count > 0)
                    {
                        result.Add(new IndexedFontFile(file, familyNames));
                    }
                }
                catch (IOException)
                {
                    // One unreadable font must not prevent other installed fonts from being indexed.
                }
                catch (UnauthorizedAccessException)
                {
                    // One unreadable font must not prevent other installed fonts from being indexed.
                }
                catch (InvalidDataException)
                {
                    // Ignore malformed or unsupported font containers.
                }
            }
        }

        return result;
    }

    private static string NormalizeName(string value)
    {
        var trimmed = value.Trim().TrimStart('@').Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(trimmed.Length);
        var pendingSpace = false;
        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".otc", StringComparison.OrdinalIgnoreCase)
            ? "font/otf"
            : "font/ttf";

    private sealed record IndexedFontFile(string FilePath, HashSet<string> FamilyNames);

    private static class OpenTypeFamilyNameReader
    {
        private const uint TrueTypeCollectionTag = 0x74746366; // ttcf
        private const uint NameTableTag = 0x6E616D65; // name

        public static IReadOnlyList<string> Read(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 12)
            {
                throw new InvalidDataException("Font file is too short.");
            }

            var firstTag = ReadUInt32(stream);
            IReadOnlyList<uint> faceOffsets;
            if (firstTag == TrueTypeCollectionTag)
            {
                _ = ReadUInt32(stream); // TTC version
                var faceCount = ReadUInt32(stream);
                if (faceCount is 0 or > 4096 || stream.Position + (faceCount * 4L) > stream.Length)
                {
                    throw new InvalidDataException("Invalid TrueType collection header.");
                }

                var offsets = new uint[faceCount];
                for (var index = 0; index < offsets.Length; index++)
                {
                    offsets[index] = ReadUInt32(stream);
                }

                faceOffsets = offsets;
            }
            else
            {
                faceOffsets = [0];
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var faceOffset in faceOffsets)
            {
                ReadFaceFamilyNames(stream, faceOffset, names);
            }

            return names.ToArray();
        }

        private static void ReadFaceFamilyNames(Stream stream, uint faceOffset, ISet<string> names)
        {
            if (faceOffset + 12L > stream.Length)
            {
                throw new InvalidDataException("Invalid font face offset.");
            }

            stream.Position = faceOffset + 4;
            var tableCount = ReadUInt16(stream);
            stream.Position += 6;
            if (tableCount > 4096 || stream.Position + (tableCount * 16L) > stream.Length)
            {
                throw new InvalidDataException("Invalid OpenType table directory.");
            }

            uint? nameTableOffset = null;
            uint? nameTableLength = null;
            for (var index = 0; index < tableCount; index++)
            {
                var tag = ReadUInt32(stream);
                _ = ReadUInt32(stream); // checksum
                var offset = ReadUInt32(stream);
                var length = ReadUInt32(stream);
                if (tag == NameTableTag)
                {
                    nameTableOffset = offset;
                    nameTableLength = length;
                }
            }

            if (nameTableOffset is null || nameTableLength is null
                || nameTableOffset.Value + (long)nameTableLength.Value > stream.Length
                || nameTableLength.Value < 6)
            {
                return;
            }

            stream.Position = nameTableOffset.Value;
            _ = ReadUInt16(stream); // format
            var recordCount = ReadUInt16(stream);
            var stringStorageOffset = ReadUInt16(stream);
            if (recordCount > 16384
                || 6L + (recordCount * 12L) > nameTableLength.Value)
            {
                throw new InvalidDataException("Invalid OpenType name table.");
            }

            var records = new List<NameRecord>(recordCount);
            for (var index = 0; index < recordCount; index++)
            {
                var platformId = ReadUInt16(stream);
                _ = ReadUInt16(stream); // encoding ID
                _ = ReadUInt16(stream); // language ID
                var nameId = ReadUInt16(stream);
                var length = ReadUInt16(stream);
                var offset = ReadUInt16(stream);
                if (nameId is 1 or 16)
                {
                    records.Add(new NameRecord(platformId, length, offset));
                }
            }

            var storageStart = nameTableOffset.Value + stringStorageOffset;
            var tableEnd = nameTableOffset.Value + (long)nameTableLength.Value;
            foreach (var record in records)
            {
                var valueOffset = storageStart + record.Offset;
                if (record.Length == 0 || valueOffset + record.Length > tableEnd)
                {
                    continue;
                }

                stream.Position = valueOffset;
                var bytes = new byte[record.Length];
                stream.ReadExactly(bytes);
                var value = record.PlatformId is 0 or 3
                    ? Encoding.BigEndianUnicode.GetString(bytes)
                    : Encoding.Latin1.GetString(bytes);
                value = value.Trim('\0', ' ', '\t', '\r', '\n');
                if (value.Length > 0)
                {
                    names.Add(value);
                }
            }
        }

        private static ushort ReadUInt16(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[2];
            stream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        private static uint ReadUInt32(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[4];
            stream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private sealed record NameRecord(ushort PlatformId, ushort Length, ushort Offset);
    }
}
