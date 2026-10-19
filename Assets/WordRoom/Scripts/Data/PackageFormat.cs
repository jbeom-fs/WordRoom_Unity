using System;
using System.Buffers.Binary;
using System.Globalization;

namespace WordRoom.Data
{
    /// <summary>
    /// Constants of the WordRoom data package, schema 1. Mirrors Tools/DataExport/wr_format.py; change both together.
    /// </summary>
    public static class PackageFormat
    {
        public const string FormatName = "WordRoomDataPackage";
        public const int SupportedSchema = 1;
        public const string DefaultFolderName = "WordRoomData";

        public const string Manifest = "manifest.json";
        public const string Words = "words.json";
        public const string Senses = "senses.json";
        public const string E5Vectors = "e5_sense_vectors.bin";
        public const string FastTextVectors = "fasttext_word_vectors.bin";
        public const string Relations = "relations.bin";
        public const string RelationsMeta = "relations.json";
        public const string Restricted = "restricted.json";
        public const string Settings = "settings.json";
        public static readonly string[] RequiredFiles = { Words, Senses, E5Vectors, FastTextVectors, Relations, RelationsMeta, Restricted, Settings };

        public const int HeaderBytes = 64;
        public const string MagicMatrix = "WRDMATF4";
        public const string MagicRelations = "WRDRELV1";
        public const uint ByteOrderMark = 0x01020304;
        public const uint DtypeFloat32 = 1;
        public const uint DtypeRelationV1 = 2;
        public const uint LayoutRowMajor = 1;
        public const uint RowKeySense = 1;
        public const uint RowKeyWord = 2;
        public const uint RowKeyRelation = 3;
        public const int RelationRecordBytes = 64;

        public const byte FlagSkippedPresent = 1;
        public const byte FlagSkippedValue = 2;
        public const byte FlagCached = 4;

        /// <summary>IEEE-754 bits written by the exporter as 16 hex digits (big-endian text).</summary>
        public static double ParseDoubleBits(string hex)
        {
            if (hex == null || hex.Length != 16 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
                throw new FormatException($"float64 비트 표기가 아닙니다: '{hex}'");
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        public static string DoubleBits(double value) =>
            unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("X16", CultureInfo.InvariantCulture);

        public static float FloatFromBits(int bits) => BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>64-byte little-endian header in front of every binary file.</summary>
    public readonly struct BinaryHeader
    {
        public readonly string Magic;
        public readonly uint Schema;
        public readonly uint HeaderBytes;
        public readonly ulong Rows;
        public readonly uint Cols;
        public readonly uint Dtype;
        public readonly uint ByteOrder;
        public readonly uint Layout;
        public readonly uint RowKey;
        public readonly uint ElementBytes;
        public readonly byte[] PackagePrefix;

        BinaryHeader(ReadOnlySpan<byte> b)
        {
            var magic = new char[8];
            for (var i = 0; i < 8; i++) magic[i] = (char)b[i];
            Magic = new string(magic);
            Schema = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8));
            HeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12));
            Rows = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(16));
            Cols = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(24));
            Dtype = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(28));
            ByteOrder = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(32));
            Layout = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(36));
            RowKey = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(40));
            ElementBytes = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(44));
            PackagePrefix = b.Slice(48, 16).ToArray();
        }

        public static BinaryHeader Parse(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < PackageFormat.HeaderBytes)
                throw new ArgumentException("header is shorter than 64 bytes");
            return new BinaryHeader(bytes);
        }

        public bool PrefixMatches(string packageId)
        {
            if (packageId == null || packageId.Length < 32) return false;
            for (var i = 0; i < 16; i++)
            {
                if (!byte.TryParse(packageId.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) || v != PackagePrefix[i])
                    return false;
            }
            return true;
        }
    }
}
