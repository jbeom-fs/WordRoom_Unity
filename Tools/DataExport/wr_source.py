"""Where the exporter reads from, and the original Python modules it reuses (read-only).

The exporter never constructs kw_semantic.SemanticLexicon: its constructor opens relations.sqlite for writing
(journal mode, schema, rule re-derivation, legacy/scope migrations). Only pure functions and the read-only
RelationDB class are used, always on a private snapshot of the database."""
import importlib
import json
import sqlite3
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path


class ExportError(Exception):
    pass


@dataclass
class PythonModules:
    root: Path
    semantic: object
    relations: object
    engine: object
    judge: object
    app_constants: dict


def load_python(root):
    """Import the original WordRoom modules from `root` without writing bytecode into that project."""
    root = Path(root).resolve()
    if not (root / 'kw_relations.py').exists():
        raise ExportError(f'WordRoom Python 프로젝트가 아닙니다: {root}')
    sys.dont_write_bytecode = True
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    semantic = importlib.import_module('kw_semantic')
    relations = importlib.import_module('kw_relations')
    engine = importlib.import_module('kw_semantic_engine')
    judge = importlib.import_module('semantic_judge')
    if Path(semantic.__file__).resolve().parent != root:
        raise ExportError(f'다른 위치의 kw_semantic이 로드되었습니다: {semantic.__file__}')
    # kw_app imports tkinter; only its settings constants are needed, so they are read from the source text.
    app_constants = {'PERCENT': {'goal_weight', 'neighbor_weight', 'diversity', 'exploitation'},
                     'VOCAB_MARK': '_vocabulary_default'}
    text = (root / 'kw_app.py').read_text(encoding='utf-8')
    if "VOCAB_MARK = '_vocabulary_default'" not in text or "PERCENT = {'goal_weight','neighbor_weight','diversity','exploitation'}" not in text:
        raise ExportError('kw_app.py의 설정 상수가 예상과 다릅니다. 내보내기 도구의 설정 보정 절차를 다시 확인하세요.')
    return PythonModules(root, semantic, relations, engine, judge, app_constants)


@dataclass
class Source:
    """Input files of one export. `kind` is 'real' (the active prepared vocabulary) or 'synthetic' (test fixture)."""
    kind: str
    root: Path                 # Python project root (relative config paths resolve here)
    vocab_dir: Path
    relations_db: Path
    relation_version: str
    judge: dict
    settings_path: Path
    app_version: str
    models_manifest: Path
    judge_manifest: Path
    notes: list = field(default_factory=list)

    def ready(self):
        return json.loads((self.vocab_dir / 'ready.json').read_text(encoding='utf-8'))

    def records(self):
        return json.loads((self.vocab_dir / 'selected_senses.json').read_text(encoding='utf-8'))


def real_source(py):
    """The vocabulary Python play uses now: cache/semantic/vocabulary.json 'active' (kw_semantic.vocab_dir)."""
    root = py.root
    vocab = Path(py.semantic.vocab_dir())
    for name in ('ready.json', 'verified.json', 'selected_senses.json', 'vectors.npy', 'fasttext.npy', 'fasttext.json'):
        if not (vocab / name).exists():
            raise ExportError(f'활성 어휘 {vocab.name}에 {name}이 없습니다. Python에서 run.py prepare로 준비·검증하세요.')
    ready = json.loads((vocab / 'ready.json').read_text(encoding='utf-8'))
    judge = json.loads((root / 'models/judge_manifest.json').read_text(encoding='utf-8'))
    judge.update(prompt_sha256=py.judge.PROMPT_HASH, prompt_version=py.judge.PROMPT_VERSION)
    return Source(kind='real', root=root, vocab_dir=vocab, relations_db=root / 'cache/semantic/relations.sqlite',
                  relation_version=py.semantic.relation_version(ready['dictionary']), judge=judge,
                  settings_path=root / 'settings.json', app_version=py.engine.VERSION,
                  models_manifest=root / 'models/manifest.json', judge_manifest=root / 'models/judge_manifest.json')


def snapshot_database(path, dest):
    """Consistent copy of the relation DB including committed WAL frames: SQLite online backup from a read-only
    connection (the source file, its -wal and -shm are not modified). The copy is switched to rollback journal
    so later read-only opens need no shared-memory file."""
    path, dest = Path(path), Path(dest)
    if not path.exists():
        raise ExportError(f'관계 DB가 없습니다: {path}')
    dest.unlink(missing_ok=True)
    src = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=30)
    try:
        dst = sqlite3.connect(dest)
        try:
            src.backup(dst)
            dst.execute('PRAGMA journal_mode=DELETE')
            check = dst.execute('PRAGMA integrity_check').fetchone()[0]
            if check != 'ok':
                raise ExportError(f'관계 DB 스냅샷 무결성 검사 실패: {check}')
        finally:
            dst.close()
    finally:
        src.close()
    files = [dict(name=p.name, bytes=p.stat().st_size, mtime_ns=str(p.stat().st_mtime_ns))
             for p in (path, path.with_name(path.name + '-wal')) if p.exists()]
    return dict(method='sqlite3 online backup API from a read-only (mode=ro) connection', source_files=files)


def effective_settings(py, raw):
    """Settings as Python 1.1.0 applies them when it loads settings.json (kw_app.App.load_saved_config followed by
    kw_semantic_engine.load_config), emulated without writing anything. Returns (Config, notices, steps)."""
    notices, steps = [], []
    values = dict(raw)
    mark = py.app_constants['VOCAB_MARK']
    if 'relation_threshold' not in values:
        values.update(goal_weight=0., conflict_mode='strict', repeat_scope='nearby', repeat_radius=3,
                      candidate_count=64, selection_top=4, runs=10)
        values = asdict(py.engine.load_config(values, notices.append))
        steps.append('relation_threshold가 없는 이전 설정: GUI가 의미 기반 기본 규칙을 적용함(원본 GUI는 이때 settings_before_semantic.json 백업 후 저장)')
    if values.pop(mark, None) is None and values.get('vocabulary_limit') == 10000:
        values['vocabulary_limit'] = 15000
        steps.append('1.1.0 이전 기본 어휘 수 10,000 → 15,000 (원본 GUI는 이때 settings_before_vocabulary15000.json 백업 후 저장)')
    elif mark in raw:
        steps.append(f'{mark} 표시가 있어 저장된 어휘 수를 그대로 사용')
    cfg = py.engine.load_config(values, notices.append)
    return cfg, notices, steps


def gui_round_trip_notes(py, cfg):
    """kw_app shows percent fields as value*100 in Tk StringVars and divides by 100 on read; report values whose
    float representation changes in that round trip (Tcl and Python both print the shortest round-trip form)."""
    notes = []
    for key in sorted(py.app_constants['PERCENT']):
        value = getattr(cfg, key)
        back = float(repr(value * 100)) / 100
        if back != value:
            notes.append(f'{key}: {value!r} → GUI 입력칸 왕복 후 {back!r} (화면 표시 {value*100!r}%). 내보낸 값은 load_config 결과 {value!r}')
    return notes
