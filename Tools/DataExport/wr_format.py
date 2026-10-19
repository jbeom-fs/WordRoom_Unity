"""WordRoom Unity data package, schema 1: names, binary layouts and hashing shared by the exporter,
the package verifier and the fixture builder. The C# reader (Assets/WordRoom/Scripts/Data/PackageFormat.cs)
mirrors these constants; change both together and raise SCHEMA when the layout changes."""
import hashlib
import json
import struct
from pathlib import Path
import numpy as np

FORMAT = 'WordRoomDataPackage'
SCHEMA = 1
EXPORTER_VERSION = '1.0.0'
PACKAGE_DIR = 'WordRoomData'

MANIFEST = 'manifest.json'
WORDS = 'words.json'
SENSES = 'senses.json'
E5 = 'e5_sense_vectors.bin'
FASTTEXT = 'fasttext_word_vectors.bin'
RELATIONS = 'relations.bin'
RELATIONS_META = 'relations.json'
RESTRICTED = 'restricted.json'
SETTINGS = 'settings.json'
FILES = [WORDS, SENSES, E5, FASTTEXT, RELATIONS, RELATIONS_META, RESTRICTED, SETTINGS]

# 64-byte little-endian header in front of every binary file.
#   magic[8] schema:u32 header_bytes:u32 rows:u64 cols:u32 dtype:u32 byte_order:u32 layout:u32 row_key:u32
#   element_bytes:u32 package_prefix[16] (first 16 bytes of the binary package id)
HEADER = struct.Struct('<8sIIQIIIIII16s')
assert HEADER.size == 64
MAGIC_MATRIX = b'WRDMATF4'
MAGIC_RELATIONS = b'WRDRELV1'
BYTE_ORDER_MARK = 0x01020304      # written little-endian: 04 03 02 01
DTYPE_FLOAT32 = 1
DTYPE_RELATION_V1 = 2
LAYOUT_ROW_MAJOR = 1
ROW_KEY_SENSE = 1                 # row = index into senses.json arrays (ascending integer sense_id)
ROW_KEY_WORD = 2                  # row = index into words.json words (frequency rank)
ROW_KEY_RELATION = 3              # row = relation record

# One relation record = one stored sense-pair judgment of the current judge version (64 bytes).
#   a/b: sense rows of the DB columns a/b (a < b as sense-ID strings; forward = a then b)
#   status: index into relations.json statusNames; valid: stored valid_yes (B rule result)
#   fa/ba/cfa/cba: answer code (0 = absent, k = answerNames[k-1]) of forward/backward/cached forward/cached backward
#   method: index into methodNames; flags: bit0 backward_skipped present, bit1 its value, bit2 cached_scores present
#   created: index into createdAtValues; score: stored score (float64, unrounded); fs/bs/cfs/cbs: payload scores (NaN = absent)
RELATION_DTYPE = np.dtype([('a', '<i4'), ('b', '<i4'), ('status', 'u1'), ('valid', 'u1'), ('fa', 'u1'), ('ba', 'u1'),
                           ('method', 'u1'), ('flags', 'u1'), ('cfa', 'u1'), ('cba', 'u1'), ('created', '<u2'),
                           ('reserved16', '<u2'), ('reserved32', '<u4'), ('score', '<f8'), ('fs', '<f8'), ('bs', '<f8'),
                           ('cfs', '<f8'), ('cbs', '<f8')])
assert RELATION_DTYPE.itemsize == 64
FLAG_SKIPPED_PRESENT, FLAG_SKIPPED_VALUE, FLAG_CACHED = 1, 2, 4


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path, chunk=1 << 22):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        while block := f.read(chunk):
            h.update(block)
    return h.hexdigest()


def json_bytes(obj, compact=True):
    """UTF-8 without BOM, non-ASCII kept as is (JsonUtility reads it)."""
    text = json.dumps(obj, ensure_ascii=False, separators=(',', ':') if compact else None, indent=None if compact else 2,
                      allow_nan=False)
    return text.encode('utf-8')


def header(magic, rows, cols, dtype, row_key, element_bytes, package_id):
    return HEADER.pack(magic, SCHEMA, HEADER.size, rows, cols, dtype, BYTE_ORDER_MARK, LAYOUT_ROW_MAJOR, row_key,
                       element_bytes, bytes.fromhex(package_id)[:16])


def read_header(data):
    names = ('magic', 'schema', 'header_bytes', 'rows', 'cols', 'dtype', 'byte_order', 'layout', 'row_key',
             'element_bytes', 'package_prefix')
    return dict(zip(names, HEADER.unpack_from(data, 0)))


def double_bits(x):
    """IEEE-754 bits of a float64 as 16 hex digits (exact transport of thresholds and scores in JSON)."""
    return struct.pack('>d', float(x)).hex().upper()


def float_bits(x):
    """IEEE-754 bits of a float32 as a signed 32-bit int (JsonUtility int)."""
    return struct.unpack('<i', struct.pack('<f', float(x)))[0]


def write_bytes(path, data):
    Path(path).write_bytes(data)
    return dict(bytes=len(data), sha256=sha256_bytes(data))
