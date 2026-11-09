using System.Buffers.Binary;
using System.Text;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Effects;

/// <summary>
/// Append-only editing of an unencrypted UE2 package: the original bytes stay as they are; new and changed objects,
/// then new copies of the name, import and export tables are written after them and the header is pointed at the new
/// tables. Every existing object keeps its offset (L2Dex's own LineageSkillEffect.u was edited the same way).
/// Object references follow UE2 rules: positive = export (index + 1), negative = import (-(index + 1)).
/// </summary>
public sealed class PackageEditor
{
    private readonly byte[] _original;
    private readonly List<string> _names;
    private readonly Dictionary<string, int> _nameLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly List<byte[]> _addedNameEntries = new List<byte[]>();
    private readonly List<ImportEntry> _imports;
    private readonly int _originalImportCount;
    private readonly List<ExportRow> _exports;
    private readonly int _nameTableStart;
    private readonly int _nameTableEnd;
    private readonly int _importTableStart;
    private readonly int _importTableEnd;
    private readonly uint _nameFlags;
    private Dictionary<string, int>? _topLevelClasses;
    private Dictionary<int, List<string>>? _children;

    public PackageEditor(byte[] package)
    {
        _original = package;
        UnrealPackage parsed = UnrealPackage.Parse(package);
        _names = parsed.Names.ToList();
        for (int i = 0; i < _names.Count; i++)
        {
            _nameLookup.TryAdd(_names[i], i);
        }

        _imports = parsed.Imports.ToList();
        _originalImportCount = _imports.Count;
        _exports = parsed.Exports.Select(export => new ExportRow(export)).ToList();

        _nameTableStart = HeaderInt(16);
        int o = _nameTableStart;
        var flagCounts = new Dictionary<uint, int>();
        for (int i = 0; i < _names.Count; i++)
        {
            int length = CompactIndex.Read(package, ref o);
            if (length < 0)
            {
                o += -length * 2;
            }
            else
            {
                o += length;
            }

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(o));
            flagCounts[flags] = flagCounts.GetValueOrDefault(flags) + 1;
            o += 4;
        }

        _nameTableEnd = o;
        _nameFlags = flagCounts.MaxBy(pair => pair.Value).Key;

        _importTableStart = HeaderInt(32);
        o = _importTableStart;
        for (int i = 0; i < _imports.Count; i++)
        {
            CompactIndex.Read(package, ref o);
            CompactIndex.Read(package, ref o);
            o += 4;
            CompactIndex.Read(package, ref o);
        }

        _importTableEnd = o;

        // The export table is rewritten from the parsed rows; make sure that reproduces the original bytes exactly.
        byte[] table = SerializeExports();
        if (!package.AsSpan(HeaderInt(24), table.Length).SequenceEqual(table))
        {
            throw new InvalidDataException("Re-serialising the export table does not reproduce the original; format not understood.");
        }
    }

    /// <summary>The index of a name, added to the name table when missing.</summary>
    public int Name(string name)
    {
        if (_nameLookup.TryGetValue(name, out int index))
        {
            return index;
        }

        var writer = new ByteWriter();
        writer.Compact(name.Length + 1);
        writer.Bytes(Encoding.Latin1.GetBytes(name));
        writer.Byte(0);
        writer.UInt32(_nameFlags);
        _addedNameEntries.Add(writer.ToArray());
        _names.Add(name);
        _nameLookup[name] = _names.Count - 1;
        return _names.Count - 1;
    }

    public string NameAt(int index)
    {
        return _names[index];
    }

    public string ExportName(int exportRef)
    {
        return _names[_exports[exportRef - 1].Name];
    }

    /// <summary>The reference of an import, added when missing.</summary>
    public int Import(string classPackage, string className, int outer, string objectName)
    {
        for (int i = 0; i < _imports.Count; i++)
        {
            ImportEntry entry = _imports[i];
            bool match = string.Equals(_names[entry.ClassPackageIndex], classPackage, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_names[entry.ClassNameIndex], className, StringComparison.OrdinalIgnoreCase)
                && entry.OuterIndex == outer
                && string.Equals(_names[entry.ObjectNameIndex], objectName, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return -(i + 1);
            }
        }

        _imports.Add(new ImportEntry(Name(classPackage), Name(className), outer, Name(objectName)));
        return -_imports.Count;
    }

    /// <summary>The reference of the top-level class <paramref name="name"/>, or 0.</summary>
    public int FindClass(string name)
    {
        IndexExports();
        if (_topLevelClasses!.TryGetValue(name, out int exportRef))
        {
            return exportRef;
        }

        return 0;
    }

    public IReadOnlyList<(int Ref, string Name)> TopLevelClasses()
    {
        IndexExports();
        return _topLevelClasses!.Select(pair => (pair.Value, pair.Key)).OrderBy(item => item.Value).ToList();
    }

    public IReadOnlyList<string> ChildNames(int outer)
    {
        IndexExports();
        if (_children!.TryGetValue(outer, out List<string>? list))
        {
            return list;
        }

        return Array.Empty<string>();
    }

    public byte[] GetData(int exportRef)
    {
        ExportRow row = _exports[exportRef - 1];
        if (row.Data is not null)
        {
            return row.Data;
        }

        return _original.AsSpan(row.Offset, row.Size).ToArray();
    }

    public int AddExport(int classRef, int superRef, int outerRef, string name, uint flags, byte[]? data = null)
    {
        var row = new ExportRow(classRef, superRef, outerRef, Name(name), flags)
        {
            Data = data ?? Array.Empty<byte>(),
        };
        _exports.Add(row);
        if (_topLevelClasses is not null)
        {
            Index(_exports.Count);
        }

        return _exports.Count;
    }

    public void SetData(int exportRef, byte[] data)
    {
        _exports[exportRef - 1].Data = data;
    }

    /// <summary>At build time the int at <paramref name="position"/> in the object's data becomes the object's file
    /// offset + <paramref name="relative"/> (texture mips store the absolute offset of the end of their data).</summary>
    public void AddOffsetFixup(int exportRef, int position, int relative)
    {
        _exports[exportRef - 1].Fixups.Add((position, relative));
    }

    public byte[] Build()
    {
        var output = new MemoryStream();
        output.Write(_original);
        foreach (ExportRow row in _exports)
        {
            if (row.Data is null)
            {
                continue;
            }

            row.Offset = (int)output.Length;
            row.Size = row.Data.Length;
            byte[] data = (byte[])row.Data.Clone();
            foreach ((int position, int relative) in row.Fixups)
            {
                SetInt(data, position, row.Offset + relative);
            }

            output.Write(data);
        }

        int nameOffset = (int)output.Length;
        output.Write(_original.AsSpan(_nameTableStart, _nameTableEnd - _nameTableStart));
        foreach (byte[] entry in _addedNameEntries)
        {
            output.Write(entry);
        }

        int importOffset = (int)output.Length;
        output.Write(_original.AsSpan(_importTableStart, _importTableEnd - _importTableStart));
        var imports = new ByteWriter();
        foreach (ImportEntry entry in _imports.Skip(_originalImportCount))
        {
            imports.Compact(entry.ClassPackageIndex);
            imports.Compact(entry.ClassNameIndex);
            imports.Int32(entry.OuterIndex);
            imports.Compact(entry.ObjectNameIndex);
        }

        output.Write(imports.ToArray());
        int exportOffset = (int)output.Length;
        output.Write(SerializeExports());

        byte[] result = output.ToArray();
        SetInt(result, 12, _names.Count);
        SetInt(result, 16, nameOffset);
        SetInt(result, 20, _exports.Count);
        SetInt(result, 24, exportOffset);
        SetInt(result, 28, _imports.Count);
        SetInt(result, 32, importOffset);
        int generations = HeaderInt(52);
        if (generations > 0)
        {
            int last = 56 + ((generations - 1) * 8);
            SetInt(result, last, _exports.Count);
            SetInt(result, last + 4, _names.Count);
        }

        return result;
    }

    private void IndexExports()
    {
        if (_topLevelClasses is not null)
        {
            return;
        }

        _topLevelClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _children = new Dictionary<int, List<string>>();
        for (int i = 0; i < _exports.Count; i++)
        {
            Index(i + 1);
        }
    }

    private void Index(int exportRef)
    {
        ExportRow row = _exports[exportRef - 1];
        if (row.Class == 0 && row.Outer == 0)
        {
            _topLevelClasses!.TryAdd(_names[row.Name], exportRef);
        }

        if (!_children!.TryGetValue(row.Outer, out List<string>? list))
        {
            list = new List<string>();
            _children[row.Outer] = list;
        }

        list.Add(_names[row.Name]);
    }

    private byte[] SerializeExports()
    {
        var writer = new ByteWriter();
        foreach (ExportRow row in _exports)
        {
            writer.Compact(row.Class);
            writer.Compact(row.Super);
            writer.Int32(row.Outer);
            writer.Compact(row.Name);
            writer.UInt32(row.Flags);
            writer.Compact(row.Size);
            if (row.Size > 0)
            {
                writer.Compact(row.Offset);
            }
        }

        return writer.ToArray();
    }

    private int HeaderInt(int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(_original.AsSpan(offset));
    }

    private static void SetInt(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }

    private sealed class ExportRow
    {
        public ExportRow(ExportEntry entry)
        {
            Class = entry.ClassIndex;
            Super = entry.SuperIndex;
            Outer = entry.OuterIndex;
            Name = entry.ObjectNameIndex;
            Flags = entry.ObjectFlags;
            Size = entry.SerialSize;
            Offset = entry.SerialOffset;
        }

        public ExportRow(int classRef, int superRef, int outerRef, int name, uint flags)
        {
            Class = classRef;
            Super = superRef;
            Outer = outerRef;
            Name = name;
            Flags = flags;
        }

        public int Class { get; }

        public int Super { get; }

        public int Outer { get; }

        public int Name { get; }

        public uint Flags { get; }

        public int Size { get; set; }

        public int Offset { get; set; }

        public byte[]? Data { get; set; }

        public List<(int Position, int Relative)> Fixups { get; } = new List<(int Position, int Relative)>();
    }
}
