using System.Text;

namespace L2Mod.Core.Packages;

public sealed record ImportEntry(int ClassPackageIndex, int ClassNameIndex, int OuterIndex, int ObjectNameIndex);

public sealed record ExportEntry(
    int ClassIndex,
    int SuperIndex,
    int OuterIndex,
    int ObjectNameIndex,
    uint ObjectFlags,
    int SerialSize,
    int SerialOffset);

/// <summary>
/// Minimal reader of an Unreal Engine 2 package (header, name, import and export tables).
/// Object references follow UE2 rules: 0 = none, negative = import (-index - 1), positive = export (index - 1).
/// </summary>
public sealed class UnrealPackage
{
    public const uint Signature = 0x9E2A83C1;

    private const int MinimumSupportedVersion = 64;

    private UnrealPackage(
        byte[] data,
        ushort fileVersion,
        ushort licenseeVersion,
        IReadOnlyList<string> names,
        IReadOnlyList<ImportEntry> imports,
        IReadOnlyList<ExportEntry> exports)
    {
        Data = data;
        FileVersion = fileVersion;
        LicenseeVersion = licenseeVersion;
        Names = names;
        Imports = imports;
        Exports = exports;
    }

    public byte[] Data { get; }

    public ushort FileVersion { get; }

    public ushort LicenseeVersion { get; }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<ImportEntry> Imports { get; }

    public IReadOnlyList<ExportEntry> Exports { get; }

    public static UnrealPackage Parse(byte[] data)
    {
        var reader = new ByteReader(data, 0);
        uint signature = reader.ReadUInt32();
        if (signature != Signature)
        {
            throw new InvalidDataException("Not an Unreal package (bad signature).");
        }

        ushort fileVersion = reader.ReadUInt16();
        ushort licenseeVersion = reader.ReadUInt16();
        reader.ReadUInt32();
        int nameCount = reader.ReadInt32();
        int nameOffset = reader.ReadInt32();
        int exportCount = reader.ReadInt32();
        int exportOffset = reader.ReadInt32();
        int importCount = reader.ReadInt32();
        int importOffset = reader.ReadInt32();

        if (fileVersion < MinimumSupportedVersion)
        {
            throw new NotSupportedException($"Package version {fileVersion} is too old (need {MinimumSupportedVersion}+).");
        }

        List<string> names = ReadNames(data, nameOffset, nameCount);
        List<ImportEntry> imports = ReadImports(data, importOffset, importCount);
        List<ExportEntry> exports = ReadExports(data, exportOffset, exportCount);
        return new UnrealPackage(data, fileVersion, licenseeVersion, names, imports, exports);
    }

    public string GetName(int index)
    {
        if (index < 0 || index >= Names.Count)
        {
            throw new InvalidDataException($"Name index {index} is out of range.");
        }

        return Names[index];
    }

    public string GetObjectName(int objectReference)
    {
        if (objectReference == 0)
        {
            return "None";
        }

        if (objectReference < 0)
        {
            return GetName(Imports[-objectReference - 1].ObjectNameIndex);
        }

        return GetName(Exports[objectReference - 1].ObjectNameIndex);
    }

    public string GetClassName(ExportEntry export)
    {
        if (export.ClassIndex == 0)
        {
            return "Class";
        }

        return GetObjectName(export.ClassIndex);
    }

    /// <summary>Object path inside the package, e.g. "skill_i.skill1235".</summary>
    public string GetPath(ExportEntry export)
    {
        var parts = new List<string>();
        parts.Add(GetName(export.ObjectNameIndex));
        int outer = export.OuterIndex;
        int guard = 0;
        while (outer > 0 && guard < 64)
        {
            ExportEntry outerExport = Exports[outer - 1];
            parts.Add(GetName(outerExport.ObjectNameIndex));
            outer = outerExport.OuterIndex;
            guard++;
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static List<string> ReadNames(byte[] data, int offset, int count)
    {
        var reader = new ByteReader(data, offset);
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadCompactIndex();
            string name;
            if (length < 0)
            {
                ReadOnlySpan<byte> raw = reader.ReadSpan(-length * 2);
                name = Encoding.Unicode.GetString(raw).TrimEnd('\0');
            }
            else
            {
                ReadOnlySpan<byte> raw = reader.ReadSpan(length);
                name = Encoding.Latin1.GetString(raw).TrimEnd('\0');
            }

            reader.ReadUInt32();
            names.Add(name);
        }

        return names;
    }

    private static List<ImportEntry> ReadImports(byte[] data, int offset, int count)
    {
        var reader = new ByteReader(data, offset);
        var imports = new List<ImportEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int classPackage = reader.ReadCompactIndex();
            int className = reader.ReadCompactIndex();
            int outer = reader.ReadInt32();
            int objectName = reader.ReadCompactIndex();
            imports.Add(new ImportEntry(classPackage, className, outer, objectName));
        }

        return imports;
    }

    private static List<ExportEntry> ReadExports(byte[] data, int offset, int count)
    {
        var reader = new ByteReader(data, offset);
        var exports = new List<ExportEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int classIndex = reader.ReadCompactIndex();
            int superIndex = reader.ReadCompactIndex();
            int outer = reader.ReadInt32();
            int objectName = reader.ReadCompactIndex();
            uint flags = reader.ReadUInt32();
            int serialSize = reader.ReadCompactIndex();
            int serialOffset = 0;
            if (serialSize > 0)
            {
                serialOffset = reader.ReadCompactIndex();
            }

            exports.Add(new ExportEntry(classIndex, superIndex, outer, objectName, flags, serialSize, serialOffset));
        }

        return exports;
    }
}
