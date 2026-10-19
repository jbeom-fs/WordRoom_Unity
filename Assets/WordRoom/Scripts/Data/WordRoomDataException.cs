using System;

namespace WordRoom.Data
{
    public enum DataErrorCode
    {
        MissingFile,
        SizeMismatch,
        HashMismatch,
        PackageIdMismatch,
        UnsupportedSchema,
        InvalidFormat,
        CountMismatch,
        InvalidReference,
        DuplicateEntry,
        InvalidValue,
        UnsupportedPlatform,
        Cancelled,
    }

    /// <summary>Package missing, damaged, mixed from different exports or of an unsupported version.</summary>
    public sealed class WordRoomDataException : Exception
    {
        public DataErrorCode Code { get; }
        public string FileName { get; }

        public WordRoomDataException(DataErrorCode code, string fileName, string message, Exception inner = null)
            : base($"[{code}] {(string.IsNullOrEmpty(fileName) ? "" : fileName + ": ")}{message}", inner)
        {
            Code = code;
            FileName = fileName;
        }
    }

    /// <summary>A query arrived before the dataset finished loading (or after loading failed).</summary>
    public sealed class DataNotReadyException : InvalidOperationException
    {
        public DataNotReadyException(string state)
            : base($"WordRoom 데이터가 아직 준비되지 않았습니다 (상태: {state}). 로딩 완료 후 조회하세요.")
        {
        }
    }
}
