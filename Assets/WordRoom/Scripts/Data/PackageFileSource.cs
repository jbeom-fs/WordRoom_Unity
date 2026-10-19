using System;
using System.IO;

namespace WordRoom.Data
{
    /// <summary>
    /// Read access to the files of one package. Platform-specific access (e.g. Android, where StreamingAssets
    /// lives inside the APK and must be copied or streamed first) goes behind this interface.
    /// </summary>
    public interface IPackageFileSource
    {
        string Description { get; }
        bool Exists(string name);
        long GetLength(string name);
        Stream OpenRead(string name);
    }

    /// <summary>Package in a plain folder (Editor, Windows/macOS/Linux players: StreamingAssets is a real folder).</summary>
    public sealed class DirectoryPackageSource : IPackageFileSource
    {
        readonly string directory;

        public DirectoryPackageSource(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("package directory is empty", nameof(directory));
            this.directory = directory;
        }

        public string Description => directory;

        string PathOf(string name)
        {
            if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains(".."))
                throw new ArgumentException($"package file names are plain names: {name}");
            return Path.Combine(directory, name);
        }

        public bool Exists(string name) => File.Exists(PathOf(name));

        public long GetLength(string name) => new FileInfo(PathOf(name)).Length;

        public Stream OpenRead(string name) =>
            new FileStream(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
    }
}
