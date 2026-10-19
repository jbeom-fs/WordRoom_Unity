using System;

// JSON documents of the data package (read with UnityEngine.JsonUtility: public fields, arrays, no dictionaries).
// Field names match the JSON written by Tools/DataExport/wr_export.py.
namespace WordRoom.Data.Documents
{
#pragma warning disable 0649
    [Serializable]
    public sealed class ManifestDocument
    {
        public string format;
        public int schemaVersion;
        public string packageId;
        public string createdAt;
        public ExporterInfo exporter;
        public PackageIdentity identity;
        public PackageCounts counts;
        public ManifestFile[] files;
        public string[] notes;
    }

    [Serializable]
    public sealed class ExporterInfo
    {
        public string name;
        public string version;
        public string python;
        public string numpy;
    }

    [Serializable]
    public sealed class PackageIdentity
    {
        public string sourceKind;
        public string appVersion;
        public string vocabularyName;
        public string vocabularyCreatedAt;
        public string senseOrderSha256;
        public string filterVersion;
        public int vocabularyLimit;
        public int minLength;
        public int maxLength;
        public bool excludeJeok;
        public string dictionarySourceName;
        public long dictionarySourceBytes;
        public string dictionarySourceMtimeNs;
        public string dictionaryCreatedAt;
        public int dictionarySchema;
        public string embeddingModel;
        public string embeddingInput;
        public string fasttextVersion;
        public string fasttextModel;
        public string fasttextModelSha256;
        public string judgeModel;
        public string judgePromptSha256;
        public string judgePromptVersion;
        public string relationVersion;
        public string relationRule;
        public string relationSnapshotId;
        public string relationContentSha256;
        public string restrictedListVersion;
        public string settingsSha256;
        public string effectiveSettingsSha256;
        public SourceFile[] sourceFiles;
    }

    [Serializable]
    public sealed class SourceFile
    {
        public string name;
        public long bytes;
        public string sha256;
    }

    [Serializable]
    public sealed class PackageCounts
    {
        public int words;
        public int senses;
        public int e5Dimensions;
        public int fasttextDimensions;
        public int fasttextValidWords;
        public int relationRecords;
        public int restrictedEntries;
        public int restrictedInVocabulary;
        public int preparedSourceRows;
    }

    [Serializable]
    public sealed class ManifestFile
    {
        public string name;
        public string role;
        public long bytes;
        public string sha256;
        public long records;
        public string contentType;
        public long rows;
        public int cols;
        public string dtype;
        public string byteOrder;
        public string layout;
        public string rowKey;
        public int headerBytes;
        public int elementBytes;
        public string payloadSha256;
    }

    [Serializable]
    public sealed class WordsDocument
    {
        public string packageId;
        public int count;
        public string[] words;
        public int[] lengths;
        public int[] senseOffsets;
        public int[] senseRows;
        public int[] fasttextValid;
    }

    [Serializable]
    public sealed class SensesDocument
    {
        public string packageId;
        public int count;
        public string[] senseIds;
        public int[] wordRanks;
        public string[] entryIds;
        public string[] raws;
        public string[] pos;
        public string[] kinds;
        public string[] categories;
        public string[] definitions;
    }

    [Serializable]
    public sealed class ThresholdValue
    {
        public string text;
        public string bits;
    }

    [Serializable]
    public sealed class NameCount
    {
        public string name;
        public int count;
    }

    [Serializable]
    public sealed class JudgeInfo
    {
        public string repo;
        public string revision;
        public string license;
        public string promptSha256;
        public string promptVersion;
    }

    [Serializable]
    public sealed class RelationCounts
    {
        public int storedRowsAllVersions;
        public int currentVersionRows;
        public int compatible;
        public int excludedOutsideVocabulary;
        public int excludedSignatureMismatch;
        public int otherVersionRows;
        public int legacyJudgmentsTableRows;
        public int validYes;
        public int decideMismatches;
        public int preparedRows;
        public int preparedExcluded;
        public NameCount[] byStatus;
    }

    [Serializable]
    public sealed class SnapshotSourceFile
    {
        public string name;
        public long bytes;
        public string mtime_ns;
    }

    [Serializable]
    public sealed class SnapshotInfo
    {
        public string method;
        public string snapshotId;
        public SnapshotSourceFile[] sourceFiles;
    }

    [Serializable]
    public sealed class PreparedSourcesDocument
    {
        public string[] scopes;
        public int[] senseRows;
        public int[] scopeIndex;
        public int[] topK;
    }

    [Serializable]
    public sealed class RelationsDocument
    {
        public string packageId;
        public string relationVersion;
        public string rule;
        public string storedRule;
        public string bRule;
        public string approvedStatusName;
        public JudgeInfo judge;
        public string[] statusNames;
        public string[] answerNames;
        public string[] methodNames;
        public string[] createdAtValues;
        public int recordCount;
        public RelationCounts counts;
        public SnapshotInfo snapshot;
        public string[] unknownPayloadKeys;
        public PreparedSourcesDocument preparedSources;
        public ThresholdValue[] thresholdLadder;
    }

    [Serializable]
    public sealed class RestrictedEntryDocument
    {
        public string word;
        public int rank;
        public string selection;
        public string[] rules;
        public string reason;
        public double functionNeighborShare;
    }

    [Serializable]
    public sealed class RestrictedDocument
    {
        public string packageId;
        public string listVersion;
        public string sourcePath;
        public string sourceSha256;
        public string purpose;
        public string effect;
        public string[] application;
        public RestrictedEntryDocument[] entries;
        public int inVocabulary;
    }

    [Serializable]
    public sealed class SettingEntryDocument
    {
        public string key;
        public string type;
        public string valueJson;
        public string rawJson;
        public bool rawPresent;
        public string change;
    }

    [Serializable]
    public sealed class DroppedSettingDocument
    {
        public string key;
        public string rawJson;
        public string reason;
    }

    [Serializable]
    public sealed class SettingsDocument
    {
        public string packageId;
        public string source;
        public string sourceSha256;
        public string loader;
        public string appVersion;
        public SettingEntryDocument[] entries;
        public DroppedSettingDocument[] droppedRawKeys;
        public string[] notices;
        public string[] migrationSteps;
        public string[] guiRoundTripNotes;
        public ThresholdValue[] thresholdLadder;
    }
#pragma warning restore 0649
}
