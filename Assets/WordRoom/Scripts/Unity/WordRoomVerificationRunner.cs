using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using WordRoom.Data;
using WordRoom.Data.Validation;
using Debug = UnityEngine.Debug;

namespace WordRoom.UnityLayer
{
    /// <summary>
    /// Player self-check, active only with the command-line flag -wordroomVerify:
    ///   WordRoom.exe -batchmode -wordroomVerify [-wordroomReference python_reference.json] [-wordroomReport report.txt]
    /// Loads the package from StreamingAssets, measures time and memory, compares with the Python baseline when given,
    /// times queries, writes the report and quits with exit code 0 (pass) or 1 (fail).
    /// </summary>
    public sealed class WordRoomVerificationRunner : MonoBehaviour
    {
        public static bool Active { get; private set; }
        string referencePath, reportPath;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-wordroomVerify") < 0) return;
            Active = true;
            var go = new GameObject("WordRoomVerificationRunner");
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<WordRoomVerificationRunner>();
            runner.referencePath = Arg(args, "-wordroomReference");
            runner.reportPath = Arg(args, "-wordroomReport") ?? Path.Combine(Application.persistentDataPath, "wordroom_verify_report.txt");
        }

        static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        IEnumerator Start()
        {
            var sb = new StringBuilder();
            var passed = true;
            sb.AppendLine($"WordRoom 데이터 빌드 검증 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"platform={Application.platform} editor={Application.isEditor} unity={Application.unityVersion} " +
                          $"scriptingBackend={(Type.GetType("Mono.Runtime") != null ? "Mono" : "IL2CPP/.NET")} dataPath={Application.dataPath}");
            sb.AppendLine("Python/AI 모델/외부 API 사용: 없음 (이 실행은 StreamingAssets 패키지 파일만 읽음)");
            IPackageFileSource source = null;
            try
            {
                source = WordRoomDataService.CreateDefaultSource(PackageFormat.DefaultFolderName, out var path);
                sb.AppendLine($"package={path}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"FAIL 패키지 위치: {e.Message}");
                passed = false;
            }
            if (source != null)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                var before = GC.GetTotalMemory(true);
                var memBefore = ProcessMemory.Read();
                var task = Task.Run(() => WordRoomDataLoader.Load(source, new LoadOptions { VerifyHashes = true }));
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                {
                    sb.AppendLine($"FAIL 로딩: {task.Exception?.GetBaseException().Message}");
                    passed = false;
                }
                else
                {
                    var d = task.Result;
                    var after = GC.GetTotalMemory(true);
                    var memAfter = ProcessMemory.Read();
                    sb.AppendLine($"load.hashVerified=true seconds={d.LoadReport.TotalSeconds:F3} {d.LoadReport}");
                    sb.AppendLine($"memory.managedHeapDelta={(after - before) / 1048576.0:F1}MB managedHeapTotal={after / 1048576.0:F1}MB " +
                                  $"monoUsed={Profiler.GetMonoUsedSizeLong() / 1048576.0:F1}MB totalAllocated={Profiler.GetTotalAllocatedMemoryLong() / 1048576.0:F1}MB " +
                                  $"process(before→after) {memBefore} → {memAfter}");
                    sb.AppendLine($"counts words={d.WordCount} senses={d.SenseCount} relations={d.Relations.RecordCount} e5={d.E5Dimensions} fasttext={d.FastTextDimensions} package={d.PackageId}");
                    if (!string.IsNullOrEmpty(referencePath))
                    {
                        try
                        {
                            var report = ReferenceComparer.Compare(d, ReferenceComparer.LoadReference(referencePath));
                            sb.Append(report);
                            passed &= report.Passed;
                        }
                        catch (Exception e)
                        {
                            sb.AppendLine($"FAIL 기준값 비교: {e}");
                            passed = false;
                        }
                    }
                    else
                    {
                        sb.AppendLine("기준값 비교 생략 (-wordroomReference 없음)");
                    }
                    sb.AppendLine(DatasetBenchmark.Run(d).ToString());
                    d = null;
                    task = null;
                    GC.Collect();
                    var fast = Stopwatch.StartNew();
                    var task2 = Task.Run(() => WordRoomDataLoader.Load(source, new LoadOptions { VerifyHashes = false }));
                    while (!task2.IsCompleted) yield return null;
                    sb.AppendLine(task2.IsFaulted
                        ? $"FAIL 해시 생략 로딩: {task2.Exception?.GetBaseException().Message}"
                        : $"load.hashVerified=false seconds={task2.Result.LoadReport.TotalSeconds:F3} (wall {fast.Elapsed.TotalSeconds:F3})");
                    passed &= !task2.IsFaulted;
                }
            }
            sb.AppendLine(passed ? "RESULT PASS" : "RESULT FAIL");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
                File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"보고서 저장 실패: {e.Message}");
            }
            Debug.Log(sb.ToString());
            Application.Quit(passed ? 0 : 1);
        }
    }

    /// <summary>Working set and private bytes of this process (Windows API; Process.WorkingSet64 reads 0 in the Mono player).</summary>
    static class ProcessMemory
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        struct Counters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool K32GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);

        public static string Read()
        {
            if (!K32GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<Counters>())) return "(측정 실패)";
            return $"workingSet={(ulong)c.WorkingSetSize / 1048576.0:F1}MB private={(ulong)c.PagefileUsage / 1048576.0:F1}MB peakWorkingSet={(ulong)c.PeakWorkingSetSize / 1048576.0:F1}MB";
        }
#else
        public static string Read() => "(이 플랫폼에서 프로세스 메모리 측정 안 함)";
#endif
    }
}
