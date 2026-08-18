using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;

namespace SubMuxBatch.Core.Fonts;

public sealed record FontAttachmentFile(
    string FilePath,
    string MimeType,
    string? AttachmentName = null)
{
    public string FileName => AttachmentName ?? Path.GetFileName(FilePath);
    public string SourceFileName => Path.GetFileName(FilePath);
    public long Size => new FileInfo(FilePath).Length;
}

public enum InstalledFontMatchKind
{
    DirectFaceName,
    WwsFamily,
    TypographicFamily,
    LegacyFamily,
    RegistryAlias,
    Compatibility
}

public sealed record InstalledFontMatch(
    FontAttachmentFile File,
    InstalledFontMatchKind MatchKind,
    int SelectedWeight,
    bool SelectedItalic,
    string InternalName);

public interface IInstalledFontResolver
{
    IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName);

    InstalledFontMatch? Resolve(AssFontRequirement requirement)
    {
        var files = FindByFamilyName(requirement.FamilyName);
        return files.Count == 0
            ? null
            : new InstalledFontMatch(
                files[0],
                InstalledFontMatchKind.Compatibility,
                requirement.Weight,
                requirement.Italic,
                requirement.FamilyName);
    }
}

public sealed class InstalledFontResolver : IInstalledFontResolver
{
    private static readonly string[] SupportedExtensions = [".ttf", ".otf", ".ttc", ".otc"];
    private readonly Lazy<IReadOnlyList<IndexedFontFile>> _fontFiles;

    public InstalledFontResolver(IEnumerable<string>? fontDirectories = null)
        : this(fontDirectories, registeredFonts: null)
    {
    }

    internal InstalledFontResolver(
        IEnumerable<string>? fontDirectories,
        IEnumerable<RegisteredFontEntry>? registeredFonts)
    {
        var directories = (fontDirectories ?? GetWindowsFontDirectories())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _fontFiles = new Lazy<IReadOnlyList<IndexedFontFile>>(
            () => BuildIndex(directories, registeredFonts?.ToArray()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static IInstalledFontResolver System { get; } = new InstalledFontResolver();

    // Compatibility API. New processing code uses Resolve() to select one face.
    public IReadOnlyList<FontAttachmentFile> FindByFamilyName(string familyName)
    {
        var normalized = NormalizeName(familyName);
        if (normalized.Length == 0)
        {
            return [];
        }

        return FindCandidates(normalized, out _)
            .Select(static candidate => ToAttachment(candidate.Font.FilePath))
            .DistinctBy(static attachment => attachment.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static attachment => attachment.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public InstalledFontMatch? Resolve(AssFontRequirement requirement)
    {
        var normalized = NormalizeName(requirement.FamilyName);
        if (normalized.Length == 0)
        {
            return null;
        }

        var candidates = FindCandidates(normalized, out var matchKind);
        if (candidates.Length == 0)
        {
            return null;
        }

        var ranked = candidates
            .Select(candidate => new RankedCandidate(
                candidate,
                candidate.Face.Italic == requirement.Italic ? 0 : 1,
                Math.Abs(candidate.Face.Weight - Math.Clamp(requirement.Weight, 1, 1000)),
                Math.Abs(candidate.Face.WidthClass - 5)))
            .OrderBy(static candidate => candidate.ItalicMismatch)
            .ThenBy(static candidate => candidate.WeightDistance)
            .ThenBy(static candidate => candidate.WidthDistance)
            .ThenBy(static candidate => candidate.Candidate.Font.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Candidate.Face.FaceIndex)
            .ToArray();
        var selected = ranked[0];

        return new InstalledFontMatch(
            ToAttachment(selected.Candidate.Font.FilePath),
            matchKind,
            selected.Candidate.Face.Weight,
            selected.Candidate.Face.Italic,
            selected.Candidate.Face.PreferredInternalName);
    }

    private FontCandidate[] FindCandidates(string normalizedName, out InstalledFontMatchKind matchKind)
    {
        var files = _fontFiles.Value;
        var direct = files
            .SelectMany(static font => font.Faces.Select(face => new FontCandidate(font, face)))
            .Where(candidate => candidate.Face.IsSpecificFaceName(normalizedName))
            .ToArray();
        if (direct.Length > 0)
        {
            matchKind = InstalledFontMatchKind.DirectFaceName;
            return direct;
        }

        foreach (var familyKind in new[]
                 {
                     InstalledFontMatchKind.WwsFamily,
                     InstalledFontMatchKind.LegacyFamily,
                     InstalledFontMatchKind.TypographicFamily
                 })
        {
            var familyMatches = files
                .SelectMany(static font => font.Faces.Select(face => new FontCandidate(font, face)))
                .Where(candidate => NamesFor(candidate.Face, familyKind).Contains(normalizedName))
                .ToArray();
            if (familyMatches.Length > 0)
            {
                matchKind = familyKind;
                return familyMatches;
            }
        }

        var registry = files
            .Where(font => font.RegistryAliases.Contains(normalizedName))
            .SelectMany(static font => font.Faces.Select(face => new FontCandidate(font, face)))
            .ToArray();
        matchKind = InstalledFontMatchKind.RegistryAlias;
        return registry;
    }

    private static IReadOnlySet<string> NamesFor(IndexedFontFace face, InstalledFontMatchKind kind) => kind switch
    {
        InstalledFontMatchKind.WwsFamily => face.WwsFamilyNames,
        InstalledFontMatchKind.TypographicFamily => face.TypographicFamilyNames,
        _ => face.LegacyFamilyNames
    };

    private static FontAttachmentFile ToAttachment(string path) => new(path, GetMimeType(path));

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

    private static IReadOnlyList<IndexedFontFile> BuildIndex(
        IReadOnlyList<string> directories,
        IReadOnlyList<RegisteredFontEntry>? registeredFonts)
    {
        var result = new List<IndexedFontFile>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string[] files;
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
                if (TryIndexFont(file) is { } indexed)
                {
                    result.Add(indexed);
                }
            }
        }

        AddWindowsRegistryAliases(
            result,
            directories,
            registeredFonts ?? ReadWindowsRegisteredFonts());
        return result;
    }

    private static IndexedFontFile? TryIndexFont(string path)
    {
        try
        {
            var faces = OpenTypeMetadataReader.Read(path)
                .Select((metadata, index) => new IndexedFontFace(
                    index,
                    NormalizeNames(metadata.LegacyFamilyNames),
                    NormalizeNames(metadata.TypographicFamilyNames),
                    NormalizeNames(metadata.WwsFamilyNames),
                    NormalizeNames(metadata.FullNames),
                    NormalizeNames(metadata.PostScriptNames),
                    metadata.Weight,
                    metadata.Italic,
                    metadata.WidthClass))
                .Where(static face => face.HasAnyName)
                .ToArray();
            return faces.Length == 0
                ? null
                : new IndexedFontFile(Path.GetFullPath(path), faces, []);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static HashSet<string> NormalizeNames(IEnumerable<string> names) =>
        names.Select(NormalizeName)
            .Where(static name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AddWindowsRegistryAliases(
        IList<IndexedFontFile> fonts,
        IReadOnlyList<string> fontDirectories,
        IReadOnlyList<RegisteredFontEntry> registeredFonts)
    {
        var fontsByPath = fonts.ToDictionary(
            static font => Path.GetFullPath(font.FilePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registeredFonts)
        {
            var path = ResolveRegisteredFontPath(entry.RegisteredPath, fontDirectories);
            if (path is null)
            {
                continue;
            }

            if (!fontsByPath.TryGetValue(path, out var font))
            {
                font = TryIndexFont(path);
                if (font is null)
                {
                    continue;
                }

                fonts.Add(font);
                fontsByPath.Add(path, font);
            }

            foreach (var alias in ReadRegistryAliases(entry.DisplayName))
            {
                font.RegistryAliases.Add(alias);
            }
        }
    }

    private static IReadOnlyList<RegisteredFontEntry> ReadWindowsRegisteredFonts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = new List<RegisteredFontEntry>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                        is string registeredPath)
                    {
                        result.Add(new RegisteredFontEntry(valueName, registeredPath));
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                               or global::System.Security.SecurityException
                                               or IOException)
            {
                // Internal metadata remains usable when registry access fails.
            }
        }

        return result;
    }

    private static string? ResolveRegisteredFontPath(
        string registeredPath,
        IReadOnlyList<string> fontDirectories)
    {
        var expanded = Environment.ExpandEnvironmentVariables(registeredPath.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }

        foreach (var directory in fontDirectories)
        {
            var candidate = Path.Combine(directory, expanded);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> ReadRegistryAliases(string valueName)
    {
        var displayName = valueName.Trim();
        var technologyStart = displayName.LastIndexOf(" (", StringComparison.Ordinal);
        if (technologyStart > 0 && displayName.EndsWith(')'))
        {
            var technology = displayName[(technologyStart + 2)..^1];
            if (technology.Contains("TrueType", StringComparison.OrdinalIgnoreCase)
                || technology.Contains("OpenType", StringComparison.OrdinalIgnoreCase)
                || technology.Contains("Type 1", StringComparison.OrdinalIgnoreCase)
                || technology.Contains("All res", StringComparison.OrdinalIgnoreCase))
            {
                displayName = displayName[..technologyStart].Trim();
            }
        }

        foreach (var value in new[] { displayName }
                     .Concat(displayName.Split(
                         '&',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            var normalized = NormalizeName(value);
            if (normalized.Length > 0)
            {
                yield return normalized;
            }
        }
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

    private sealed record IndexedFontFile(
        string FilePath,
        IReadOnlyList<IndexedFontFace> Faces,
        HashSet<string> RegistryAliases);

    private sealed record IndexedFontFace(
        int FaceIndex,
        HashSet<string> LegacyFamilyNames,
        HashSet<string> TypographicFamilyNames,
        HashSet<string> WwsFamilyNames,
        HashSet<string> FullNames,
        HashSet<string> PostScriptNames,
        int Weight,
        bool Italic,
        int WidthClass)
    {
        public bool HasAnyName => LegacyFamilyNames.Count > 0
                                  || TypographicFamilyNames.Count > 0
                                  || WwsFamilyNames.Count > 0
                                  || FullNames.Count > 0
                                  || PostScriptNames.Count > 0;

        public bool IsSpecificFaceName(string name) =>
            PostScriptNames.Contains(name)
            || FullNames.Contains(name)
            && !WwsFamilyNames.Contains(name)
            && !TypographicFamilyNames.Contains(name)
            && !LegacyFamilyNames.Contains(name);

        public string PreferredInternalName => WwsFamilyNames
            .Concat(TypographicFamilyNames)
            .Concat(LegacyFamilyNames)
            .Concat(FullNames)
            .Concat(PostScriptNames)
            .FirstOrDefault() ?? string.Empty;
    }

    private sealed record FontCandidate(IndexedFontFile Font, IndexedFontFace Face);
    private sealed record RankedCandidate(
        FontCandidate Candidate,
        int ItalicMismatch,
        int WeightDistance,
        int WidthDistance);

    internal sealed record RegisteredFontEntry(string DisplayName, string RegisteredPath);

    private static class OpenTypeMetadataReader
    {
        private const uint TrueTypeCollectionTag = 0x74746366; // ttcf
        private const uint NameTableTag = 0x6E616D65; // name
        private const uint HeadTableTag = 0x68656164; // head
        private const uint Os2TableTag = 0x4F532F32; // OS/2

        internal static IReadOnlyList<FontFaceMetadata> Read(string path)
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
                _ = ReadUInt32(stream);
                var faceCount = ReadUInt32(stream);
                if (faceCount is 0 or > 4096 || stream.Position + faceCount * 4L > stream.Length)
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

            return faceOffsets.Select(offset => ReadFace(stream, offset)).ToArray();
        }

        private static FontFaceMetadata ReadFace(Stream stream, uint faceOffset)
        {
            var tables = ReadTables(stream, faceOffset);
            var names = tables.TryGetValue(NameTableTag, out var nameTable)
                ? ReadNames(stream, nameTable)
                : NameMetadata.Empty;
            var style = ReadStyle(stream, tables, names);
            return new FontFaceMetadata(
                names.LegacyFamilies,
                names.TypographicFamilies,
                names.WwsFamilies,
                names.FullNames,
                names.PostScriptNames,
                style.Weight,
                style.Italic,
                style.WidthClass);
        }

        private static IReadOnlyDictionary<uint, TableRecord> ReadTables(Stream stream, uint faceOffset)
        {
            if (faceOffset + 12L > stream.Length)
            {
                throw new InvalidDataException("Invalid font face offset.");
            }

            stream.Position = faceOffset + 4;
            var tableCount = ReadUInt16(stream);
            stream.Position += 6;
            if (tableCount > 4096 || stream.Position + tableCount * 16L > stream.Length)
            {
                throw new InvalidDataException("Invalid OpenType table directory.");
            }

            var tables = new Dictionary<uint, TableRecord>();
            for (var index = 0; index < tableCount; index++)
            {
                var tag = ReadUInt32(stream);
                _ = ReadUInt32(stream);
                var offset = ReadUInt32(stream);
                var length = ReadUInt32(stream);
                if (offset + (long)length <= stream.Length)
                {
                    tables[tag] = new TableRecord(offset, length);
                }
            }

            return tables;
        }

        private static NameMetadata ReadNames(Stream stream, TableRecord table)
        {
            if (table.Length < 6)
            {
                return NameMetadata.Empty;
            }

            stream.Position = table.Offset;
            _ = ReadUInt16(stream);
            var recordCount = ReadUInt16(stream);
            var stringStorageOffset = ReadUInt16(stream);
            if (recordCount > 16384 || 6L + recordCount * 12L > table.Length)
            {
                throw new InvalidDataException("Invalid OpenType name table.");
            }

            var records = new List<NameRecord>();
            for (var index = 0; index < recordCount; index++)
            {
                var platformId = ReadUInt16(stream);
                var encodingId = ReadUInt16(stream);
                _ = ReadUInt16(stream);
                var nameId = ReadUInt16(stream);
                var length = ReadUInt16(stream);
                var offset = ReadUInt16(stream);
                if (nameId is 1 or 2 or 4 or 6 or 16 or 17 or 21 or 22
                    && platformId is 0 or 3)
                {
                    records.Add(new NameRecord(platformId, encodingId, nameId, length, offset));
                }
            }

            var result = new NameMetadata([], [], [], [], [], [], [], []);
            var storageStart = table.Offset + stringStorageOffset;
            var tableEnd = table.Offset + (long)table.Length;
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
                var value = Encoding.BigEndianUnicode.GetString(bytes).Trim('\0', ' ', '\t', '\r', '\n');
                if (value.Length == 0)
                {
                    continue;
                }

                NamesFor(result, record.NameId).Add(value);
            }

            return result;
        }

        private static HashSet<string> NamesFor(NameMetadata names, ushort nameId) => nameId switch
        {
            1 => names.LegacyFamilies,
            2 => names.LegacySubfamilies,
            4 => names.FullNames,
            6 => names.PostScriptNames,
            16 => names.TypographicFamilies,
            17 => names.TypographicSubfamilies,
            21 => names.WwsFamilies,
            _ => names.WwsSubfamilies
        };

        private static FaceStyle ReadStyle(
            Stream stream,
            IReadOnlyDictionary<uint, TableRecord> tables,
            NameMetadata names)
        {
            int? weight = null;
            int? width = null;
            bool? italic = null;
            var macBold = false;

            if (tables.TryGetValue(Os2TableTag, out var os2) && os2.Length >= 8)
            {
                stream.Position = os2.Offset;
                var version = ReadUInt16(stream);
                stream.Position = os2.Offset + 4;
                var os2Weight = ReadUInt16(stream);
                var os2Width = ReadUInt16(stream);
                weight = os2Weight is >= 1 and <= 1000 ? os2Weight : null;
                width = os2Width is >= 1 and <= 9 ? os2Width : null;
                if (os2.Length >= 64)
                {
                    stream.Position = os2.Offset + 62;
                    var selection = ReadUInt16(stream);
                    italic = (selection & 0x0001) != 0 || version >= 4 && (selection & 0x0200) != 0;
                    macBold = (selection & 0x0020) != 0;
                }
            }

            bool? headItalic = null;
            if (tables.TryGetValue(HeadTableTag, out var head) && head.Length >= 46)
            {
                stream.Position = head.Offset + 44;
                var macStyle = ReadUInt16(stream);
                macBold |= (macStyle & 0x0001) != 0;
                headItalic = (macStyle & 0x0002) != 0;
            }

            var subfamilies = names.WwsSubfamilies
                .Concat(names.TypographicSubfamilies)
                .Concat(names.LegacySubfamilies)
                .ToArray();
            return new FaceStyle(
                weight ?? InferWeight(subfamilies) ?? (macBold ? 700 : 400),
                italic ?? headItalic ?? InferItalic(subfamilies),
                width ?? 5);
        }

        private static int? InferWeight(IEnumerable<string> names)
        {
            foreach (var value in names)
            {
                var name = new string(value.Normalize(NormalizationForm.FormKC)
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray());
                if (name.Contains("thin", StringComparison.Ordinal)) return 100;
                if (name.Contains("extralight", StringComparison.Ordinal) || name.Contains("ultralight", StringComparison.Ordinal)) return 200;
                if (name.Contains("light", StringComparison.Ordinal)) return 300;
                if (name.Contains("semibold", StringComparison.Ordinal) || name.Contains("demibold", StringComparison.Ordinal)) return 600;
                if (name.Contains("extrabold", StringComparison.Ordinal) || name.Contains("ultrabold", StringComparison.Ordinal)) return 800;
                if (name.Contains("black", StringComparison.Ordinal) || name.Contains("heavy", StringComparison.Ordinal)) return 900;
                if (name.Contains("bold", StringComparison.Ordinal)) return 700;
                if (name.Contains("medium", StringComparison.Ordinal)) return 500;
            }

            return null;
        }

        private static bool InferItalic(IEnumerable<string> names) => names.Any(value =>
            value.Contains("italic", StringComparison.OrdinalIgnoreCase)
            || value.Contains("oblique", StringComparison.OrdinalIgnoreCase)
            || value.Contains("기울", StringComparison.Ordinal));

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

        private sealed record TableRecord(uint Offset, uint Length);
        private sealed record NameRecord(ushort PlatformId, ushort EncodingId, ushort NameId, ushort Length, ushort Offset);
        private sealed record FaceStyle(int Weight, bool Italic, int WidthClass);
        private sealed record NameMetadata(
            HashSet<string> LegacyFamilies,
            HashSet<string> LegacySubfamilies,
            HashSet<string> TypographicFamilies,
            HashSet<string> TypographicSubfamilies,
            HashSet<string> WwsFamilies,
            HashSet<string> WwsSubfamilies,
            HashSet<string> FullNames,
            HashSet<string> PostScriptNames)
        {
            public static NameMetadata Empty { get; } = new([], [], [], [], [], [], [], []);
        }

        internal sealed record FontFaceMetadata(
            HashSet<string> LegacyFamilyNames,
            HashSet<string> TypographicFamilyNames,
            HashSet<string> WwsFamilyNames,
            HashSet<string> FullNames,
            HashSet<string> PostScriptNames,
            int Weight,
            bool Italic,
            int WidthClass);
    }
}
