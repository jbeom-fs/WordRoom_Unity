using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WordRoom.Data;

namespace WordRoom.UnityLayer
{
    public enum DataLoadState
    {
        NotStarted,
        Loading,
        Ready,
        Failed,
    }

    /// <summary>
    /// Owns the one loaded dataset of a scene: loads it once on a worker thread and exposes its state. Queries
    /// before Ready throw DataNotReadyException instead of returning partial data.
    /// </summary>
    public sealed class WordRoomDataService : MonoBehaviour
    {
        [SerializeField] string packageFolder = PackageFormat.DefaultFolderName;
        [SerializeField] bool loadOnStart = true;
        [SerializeField] bool verifyHashes = true;

        WordRoomDataset dataset;
        Task<WordRoomDataset> loading;
        CancellationTokenSource cancel;
        volatile string stage = "";
        volatile float progress;

        public DataLoadState State { get; private set; } = DataLoadState.NotStarted;
        public string Stage => stage;
        public float Progress => progress;
        public Exception Error { get; private set; }
        public string PackagePath { get; private set; }
        public event Action<WordRoomDataService> StateChanged;

        /// <summary>The dataset; throws DataNotReadyException while loading, before loading or after a failure.</summary>
        public WordRoomDataset Dataset
        {
            get
            {
                if (State != DataLoadState.Ready) throw new DataNotReadyException(State.ToString());
                return dataset;
            }
        }

        public bool TryGetDataset(out WordRoomDataset result)
        {
            result = State == DataLoadState.Ready ? dataset : null;
            return result != null;
        }

        void Start()
        {
            if (loadOnStart && !WordRoomVerificationRunner.Active) LoadAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // failure is kept in State/Error
        }

        void OnDestroy() => cancel?.Cancel();

        /// <summary>Package location for this platform. Desktop players and the Editor read StreamingAssets as a folder.</summary>
        public static IPackageFileSource CreateDefaultSource(string folder, out string path)
        {
            path = Path.Combine(Application.streamingAssetsPath, folder);
            if (Application.platform == RuntimePlatform.Android)
                throw new WordRoomDataException(DataErrorCode.UnsupportedPlatform, null,
                    "Android에서는 StreamingAssets가 APK 내부라 폴더로 읽을 수 없습니다. persistentDataPath로 복사하는 IPackageFileSource가 필요합니다(미구현).");
            return new DirectoryPackageSource(path);
        }

        /// <summary>Starts loading once; later calls return the same task.</summary>
        public Task<WordRoomDataset> LoadAsync()
        {
            if (loading != null) return loading;
            loading = LoadInternal();
            return loading;
        }

        async Task<WordRoomDataset> LoadInternal()
        {
            SetState(DataLoadState.Loading);
            cancel = new CancellationTokenSource();
            var options = new LoadOptions { VerifyHashes = verifyHashes };
            var token = cancel.Token;
            try
            {
                var source = CreateDefaultSource(packageFolder, out var path); // main thread: Application.* access
                PackagePath = path;
                var report = new ProgressSink(this);
                dataset = await Task.Run(() => WordRoomDataLoader.Load(source, options, report, token), token);
                SetState(DataLoadState.Ready);
                return dataset;
            }
            catch (Exception e)
            {
                Error = e;
                Debug.LogError($"WordRoom 데이터 로딩 실패: {e.Message}");
                SetState(DataLoadState.Failed);
                throw;
            }
        }

        void SetState(DataLoadState state)
        {
            State = state;
            StateChanged?.Invoke(this);
        }

        sealed class ProgressSink : IProgress<LoadProgress>
        {
            readonly WordRoomDataService owner;
            public ProgressSink(WordRoomDataService owner) => this.owner = owner;

            public void Report(LoadProgress value)
            {
                owner.stage = value.Stage;
                owner.progress = value.Fraction;
            }
        }
    }
}
