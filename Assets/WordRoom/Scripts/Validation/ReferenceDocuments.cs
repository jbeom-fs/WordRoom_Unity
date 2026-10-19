using System;

// Python baseline written by Tools/DataExport/wr_reference.py (JsonUtility layout).
namespace WordRoom.Data.Validation
{
#pragma warning disable 0649
    [Serializable]
    public sealed class ReferenceDocument
    {
        public int referenceSchema;
        public string packageId;
        public string generatedAt;
        public string sourceKind;
        public string generator;
        public string relationSnapshotId;
        public RefCounts counts;
        public string[] words;
        public int[] wordLengths;
        public int[] wordSenseOffsets;
        public string[] wordSenseIds;
        public RefSenses senses;
        public RefMatrix e5;
        public RefMatrix fasttext;
        public Documents.ThresholdValue[] thresholds;
        public RefScope[] scopes;
        public RefGraph[] neighbors;
        public RefScopedNeighbors[] scopedNeighbors;
        public RefWordStates wordStates;
        public RefEvidence evidence;
        public RefPairs rejected;
        public RefSensePairs sensePairs;
        public RefCategory[] categories;
        public RefFastTextSims fasttextSimilarity;
        public RefE5Sims e5Similarity;
        public RefRestricted restricted;
        public RefSetting[] settings;
        public double generatorSeconds;
    }

    [Serializable]
    public sealed class RefCounts
    {
        public int words;
        public int senses;
        public int e5Dimensions;
        public int fasttextDimensions;
        public int fasttextValid;
        public int compatibleSensePairs;
    }

    [Serializable]
    public sealed class RefSenses
    {
        public string[] senseIds;
        public string[] words;
        public string[] pos;
        public string[] categories;
        public string[] definitions;
        public string[] entryIds;
        public string[] raws;
        public string[] kinds;
    }

    [Serializable]
    public sealed class RefVectorSample
    {
        public string key;
        public string rowSha256;
        public int[] leadingBits;
    }

    [Serializable]
    public sealed class RefMatrix
    {
        public int rows;
        public int cols;
        public string sha256;
        public int[] valid;
        public RefVectorSample[] samples;
    }

    [Serializable]
    public sealed class RefScope
    {
        public string name;
        public int limit;
        public int minLength;
        public int maxLength;
        public bool expectError;
        public int count;
        public string wordsSha256;
        public int[] ranks;
        public string error;
    }

    [Serializable]
    public sealed class RefGraph
    {
        public int thresholdIndex;
        public int[] offsets;
        public int[] neighbors;
        public int approvedWordPairs;
    }

    [Serializable]
    public sealed class RefScopedNeighbors
    {
        public string scope;
        public int thresholdIndex;
        public int word;
        public int[] neighbors;
    }

    [Serializable]
    public sealed class RefWordStates
    {
        public int[] a;
        public int[] b;
        public string[] states;
        public int touchedPairs;
        public int untouchedSamples;
    }

    [Serializable]
    public sealed class RefEvidence
    {
        public int[] word;
        public int[] neighbor;
        public int[] thresholdIndex;
        public string[] senseId;
        public string[] neighborSenseId;
        public string[] scoreBits;
    }

    [Serializable]
    public sealed class RefPairs
    {
        public int[] a;
        public int[] b;
    }

    [Serializable]
    public sealed class RefSensePairs
    {
        public string[] senseA;
        public string[] senseB;
        public string[] status;
        public int[] validYes;
        public string[] scoreBits;
        public string[] forwardAnswer;
        public string[] forwardScoreBits;
        public string[] backwardAnswer;
        public string[] backwardScoreBits;
        public string[] method;
        public string[] createdAt;
        public int[] hasPayload;
    }

    [Serializable]
    public sealed class RefCategory
    {
        public string name;
        public int thresholdIndex;
        public int count;
        public int[] a;
        public int[] b;
        public string[] states;
    }

    [Serializable]
    public sealed class RefFastTextSims
    {
        public int[] a;
        public int[] b;
        public int[] f32Bits;
        public string[] f64Bits;
    }

    [Serializable]
    public sealed class RefE5Sims
    {
        public string[] senseA;
        public string[] senseB;
        public int[] f32Bits;
        public string[] f64Bits;
    }

    [Serializable]
    public sealed class RefRestricted
    {
        public int[] ranks;
        public string[] outsideVocabulary;
    }

    [Serializable]
    public sealed class RefSetting
    {
        public string key;
        public string valueJson;
    }
#pragma warning restore 0649
}
