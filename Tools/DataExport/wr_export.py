"""Build and verify a WordRoom Unity data package (schema 1) from prepared Python data (activation: wr_swap.py).

Read-only on every source file: the prepared vocabulary folder, a private snapshot of relations.sqlite,
settings.json, the fastText restriction list and the model manifests. Nothing is recomputed: vectors are copied
bit for bit (float32), relation scores keep their float64 value, and no model is loaded."""
import json
import platform
import sqlite3
import time
from collections import Counter
from dataclasses import asdict, fields
from pathlib import Path
import numpy as np
import wr_format as F
from wr_source import ExportError, effective_settings, gui_round_trip_notes

STATUS_ORDER = ['judged', 'partial', 'invalid']
ANSWER_ORDER = ['YES', 'NO']
KNOWN_PAYLOAD_KEYS = {'forward', 'backward', 'method', 'backward_skipped', 'cached_scores'}
B_RULE = ('either-order-yes-v1: 뜻 쌍은 status가 judged(두 입력 순서 모두 판정)이고 valid_yes=1(한 순서 이상 YES)이며 '
          '저장 score(YES 점수 최댓값)가 기준 이상일 때만 승인. partial(한 순서만 판정)·invalid(응답 오류)는 YES가 있어도 승인하지 않음. '
          '단어 쌍은 승인된 뜻 조합이 하나라도 있으면 승인. 아니면 저장 YES 기준 미달(below) > 오류·불완전(error) > 미검사 조합(unchecked) > '
          '모든 현재 조합이 NO(rejected) 순으로 상태를 정함(kw_relations.RelationDB.word_state). 미검사는 기록이 없는 조합.')


def log(progress, text):
    if progress:
        progress(text)


def load_vocabulary(src):
    """Prepared vocabulary as Python play reads it (SemanticLexicon/WordView field by field), with the same
    consistency checks; nothing is trimmed or regenerated."""
    vocab = src.vocab_dir
    ready = src.ready()
    records = src.records()
    words = ready['words_by_rank']
    vectors = np.load(vocab / 'vectors.npy', mmap_mode='r', allow_pickle=False)
    ft = np.load(vocab / 'fasttext.npy', mmap_mode='r', allow_pickle=False)
    ftm = json.loads((vocab / 'fasttext.json').read_text(encoding='utf-8'))
    fail = []
    import hashlib
    if hashlib.sha256(json.dumps([r['sense_id'] for r in records]).encode()).hexdigest() != ready['sense_order_sha256']:
        fail.append('selected_senses.json 뜻 순서가 ready.json sense_order_sha256과 다름')
    if vectors.dtype != np.float32 or vectors.ndim != 2 or vectors.shape[0] != len(records):
        fail.append(f'E5 벡터 형식 {vectors.dtype} {vectors.shape} (뜻 {len(records)}개)')
    if ft.dtype != np.float32 or ft.ndim != 2 or ft.shape[0] != len(words):
        fail.append(f'fastText 벡터 형식 {ft.dtype} {ft.shape} (표기 {len(words)}개)')
    if len(set(words)) != len(words):
        fail.append('표기 목록 중복')
    rank = {w: i for i, w in enumerate(words)}
    outside = sorted({r['word'] for r in records if r['word'] not in rank})
    if outside:
        fail.append(f'표기 목록에 없는 뜻 {len(outside)}개 표기: {outside[:5]}')
    if len({r['sense_id'] for r in records}) != len(records):
        fail.append('뜻 ID 중복')
    if fail:
        raise ExportError('준비 데이터 불일치: ' + '; '.join(fail))
    groups = np.array([rank[r['word']] for r in records], dtype=np.int32)
    word_rows = [[] for _ in words]
    for i, g in enumerate(groups):
        word_rows[int(g)].append(i)
    empty = [words[i] for i, rows in enumerate(word_rows) if not rows]
    if empty:
        raise ExportError(f'뜻이 없는 표기 {len(empty)}개: {empty[:5]}')
    # Values: finite, unit E5 rows, fastText rows either unit (usable) or exactly zero (masked, kw_semantic.build_fasttext).
    if not np.isfinite(vectors).all():
        raise ExportError('E5 벡터에 NaN/무한대가 있습니다.')
    if not np.isfinite(ft).all():
        raise ExportError('fastText 벡터에 NaN/무한대가 있습니다.')
    norms = np.linalg.norm(vectors, axis=1)
    ft_norms = np.linalg.norm(ft, axis=1)
    ft_ok = ft_norms > 0
    if (norms == 0).any():
        raise ExportError(f'E5 영벡터 {int((norms == 0).sum())}행')
    missing = [w for w, ok in zip(words, ft_ok) if not ok]
    if sorted(missing) != sorted(ftm.get('missing', [])) or ftm.get('words') != len(words):
        raise ExportError('fastText 유효성 표시(fasttext.json missing)와 벡터 행이 다릅니다.')
    stats = dict(words=len(words), senses=len(records), e5_dim=int(vectors.shape[1]), fasttext_dim=int(ft.shape[1]),
                 fasttext_valid=int(ft_ok.sum()), e5_norm_min=float(norms.min()), e5_norm_max=float(norms.max()),
                 fasttext_norm_min=float(ft_norms[ft_ok].min()) if ft_ok.any() else None,
                 fasttext_norm_max=float(ft_norms[ft_ok].max()) if ft_ok.any() else None,
                 ready_words=ready['words'], ready_senses=ready['senses'])
    if stats['words'] != ready['words'] or stats['senses'] != ready['senses']:
        raise ExportError(f"ready.json 수량({ready['words']}/{ready['senses']})과 실제({stats['words']}/{stats['senses']})가 다릅니다.")
    return dict(ready=ready, records=records, words=words, vectors=vectors, ft=ft, ftm=ftm, groups=groups,
                word_rows=word_rows, ft_ok=ft_ok, stats=stats)


def extract_relations(py, src, snapshot, records):
    """Stored sense-pair judgments of the current judge version whose senses and definition signatures match the
    current vocabulary -- exactly the rows kw_relations.RelationDB loads -- plus counts of everything left out."""
    sigs = {r['sense_id']: py.relations.signature(r) for r in records}
    index = {r['sense_id']: i for i, r in enumerate(records)}
    con = sqlite3.connect(Path(snapshot).resolve().as_uri() + '?mode=ro', uri=True)
    try:
        total, = con.execute('SELECT COUNT(*) FROM relation_judgments').fetchone()
        rows_all, last = con.execute('SELECT COUNT(*),MAX(rowid) FROM relation_judgments').fetchone()
        other, = con.execute('SELECT COUNT(*) FROM relation_judgments WHERE version!=?', (src.relation_version,)).fetchone()
        legacy = 0
        if con.execute("SELECT 1 FROM sqlite_master WHERE name='judgments'").fetchone():
            legacy, = con.execute('SELECT COUNT(*) FROM judgments').fetchone()
        rule_row = con.execute("SELECT value FROM settings WHERE key='rule'").fetchone() if con.execute(
            "SELECT 1 FROM sqlite_master WHERE name='settings'").fetchone() else None
        stored_rule = rule_row[0] if rule_row else None
        kept, excluded, seen = [], Counter(), {}
        statuses, answers, methods, created, unknown_keys = Counter(), Counter(), Counter(), Counter(), Counter()
        mismatches = []
        for rowid, a, b, asig, bsig, status, valid, score, payload, created_at in con.execute(
                'SELECT rowid,a,b,a_sig,b_sig,status,valid_yes,score,payload,created_at FROM relation_judgments '
                'WHERE version=? ORDER BY rowid', (src.relation_version,)):
            if a not in sigs or b not in sigs:
                excluded['outside_vocabulary'] += 1
                continue
            if sigs[a] != asig or sigs[b] != bsig:
                excluded['signature_mismatch'] += 1
                continue
            ia, ib = index[a], index[b]
            key = (min(ia, ib), max(ia, ib))
            if key in seen:
                raise ExportError(f'같은 뜻 쌍의 호환 판정이 두 행 있습니다({a},{b}; rowid {seen[key]},{rowid}). '
                                  'Python 조회 결과가 행 순서에 의존하므로 내보내지 않습니다.')
            seen[key] = rowid
            if not isinstance(score, float) or not isinstance(valid, int):
                raise ExportError(f'score/valid_yes 형식 오류 rowid {rowid}: {score!r} {valid!r}')
            value = json.loads(payload)
            unknown_keys.update(k for k in value if k not in KNOWN_PAYLOAD_KEYS)
            fwd, bwd = value.get('forward'), value.get('backward')
            cached = value.get('cached_scores')
            for x in (fwd, bwd, *(cached or [])):
                if x is not None:
                    answers[x['answer']] += 1
            expected = py.relations.decide(fwd, bwd) if fwd is not None or bwd is not None else None
            if expected is None or expected[0] != status or expected[1] != valid or expected[2] != score:
                mismatches.append(dict(rowid=rowid, a=a, b=b, stored=[status, valid, score],
                                       derived=list(expected) if expected else None))
            statuses[status] += 1
            methods[value.get('method', '')] += 1
            created[created_at or ''] += 1
            kept.append((ia, ib, status, int(valid), float(score), fwd, bwd, value.get('method', ''),
                         value.get('backward_skipped'), cached, created_at or '', rowid))
        prepared, prepared_excluded = [], 0
        if con.execute("SELECT 1 FROM sqlite_master WHERE name='prepared_sources'").fetchone():
            for scope, sid, sig, top_k in con.execute(
                    'SELECT scope,sense_id,sig,top_k FROM prepared_sources WHERE version=? ORDER BY rowid', (src.relation_version,)):
                if sigs.get(sid) == sig:
                    prepared.append((index[sid], scope, int(top_k)))
                else:
                    prepared_excluded += 1
    finally:
        con.close()
    status_names = [s for s in STATUS_ORDER] + sorted(set(statuses) - set(STATUS_ORDER))
    answer_names = [s for s in ANSWER_ORDER] + sorted(set(answers) - set(ANSWER_ORDER))
    method_names = sorted(methods)
    created_names = sorted(created)
    if len(created_names) > 65535 or len(method_names) > 255 or len(answer_names) > 254 or len(status_names) > 255:
        raise ExportError('관계 문자열 표가 레코드 형식 범위를 넘습니다.')
    sidx = {s: i for i, s in enumerate(status_names)}
    aidx = {s: i + 1 for i, s in enumerate(answer_names)}
    midx = {s: i for i, s in enumerate(method_names)}
    cidx = {s: i for i, s in enumerate(created_names)}
    rec = np.zeros(len(kept), dtype=F.RELATION_DTYPE)
    nan = float('nan')
    for k, (ia, ib, status, valid, score, fwd, bwd, method, skipped, cached, created_at, _) in enumerate(kept):
        flags = 0
        if skipped is not None:
            flags |= F.FLAG_SKIPPED_PRESENT | (F.FLAG_SKIPPED_VALUE if skipped else 0)
        if cached is not None:
            if len(cached) != 2:
                raise ExportError(f'cached_scores 형식 오류: {cached!r}')
            flags |= F.FLAG_CACHED
        c = cached or [None, None]
        rec[k] = (ia, ib, sidx[status], valid, aidx[fwd['answer']] if fwd else 0, aidx[bwd['answer']] if bwd else 0,
                  midx[method], flags, aidx[c[0]['answer']] if c[0] else 0, aidx[c[1]['answer']] if c[1] else 0,
                  cidx[created_at], 0, 0, score, fwd['score'] if fwd else nan, bwd['score'] if bwd else nan,
                  c[0]['score'] if c[0] else nan, c[1]['score'] if c[1] else nan)
    order = np.lexsort((np.maximum(rec['a'], rec['b']), np.minimum(rec['a'], rec['b'])))
    rec = rec[order]
    scopes = sorted({s for _, s, _ in prepared})
    meta = dict(status_names=status_names, answer_names=answer_names, method_names=method_names, created_names=created_names,
                counts=dict(stored_rows_all_versions=total, current_version_rows=total - other, compatible=len(kept),
                            excluded_outside_vocabulary=excluded['outside_vocabulary'],
                            excluded_signature_mismatch=excluded['signature_mismatch'], other_version_rows=other,
                            legacy_judgments_table_rows=legacy, by_status=dict(statuses),
                            valid_yes=int(rec['valid'].sum()), decide_mismatches=len(mismatches),
                            prepared_rows=len(prepared), prepared_excluded=prepared_excluded),
                snapshot_id=f'{src.relation_version[:12]}:{py.relations.RULE}:{rows_all}:{last}',
                stored_rule=stored_rule, unknown_payload_keys=dict(unknown_keys), mismatch_examples=mismatches[:20],
                prepared=dict(scopes=scopes, sense_rows=[p[0] for p in prepared], scope_index=[scopes.index(p[1]) for p in prepared],
                              top_k=[p[2] for p in prepared]))
    if stored_rule != py.relations.RULE:
        raise ExportError(f'관계 DB 규칙 {stored_rule!r}가 현재 Python 규칙 {py.relations.RULE!r}과 다릅니다. '
                          'Python에서 관계 DB를 한 번 열어 규칙을 다시 적용한 뒤 내보내세요.')
    # Same rule: Python play (RelationDB) uses the stored columns as they are, so they are exported unchanged and
    # any disagreement with the payload is only reported (relations.json counts.decideMismatches).
    return rec, meta


def settings_document(py, src, cfg, notices, steps, raw, ladder, package_id):
    effective = asdict(cfg)
    entries = []
    for f in fields(cfg):
        value = effective[f.name]
        present = f.name in raw
        change = 'default' if not present else 'same' if raw[f.name] == value and type(raw[f.name]) is type(value) else 'changed'
        entries.append(dict(key=f.name, type=type(value).__name__, valueJson=json.dumps(value, ensure_ascii=False),
                            rawJson=json.dumps(raw[f.name], ensure_ascii=False) if present else '', rawPresent=present, change=change))
    known = {f.name for f in fields(cfg)}
    dropped = [dict(key=k, rawJson=json.dumps(v, ensure_ascii=False),
                    reason='폐기된 설정(무시)' if k in py.engine.OBSOLETE else '어휘 기본값 표시(GUI 내부용)' if k == py.app_constants['VOCAB_MARK'] else '알 수 없는 설정(무시)')
               for k, v in raw.items() if k not in known]
    return dict(packageId=package_id, source=src.settings_path.name, sourceSha256=F.sha256_file(src.settings_path),
                loader='kw_app.App.load_saved_config → kw_semantic_engine.load_config (쓰기 없이 같은 절차를 재현)',
                appVersion=src.app_version, entries=entries, droppedRawKeys=dropped, notices=notices, migrationSteps=steps,
                guiRoundTripNotes=gui_round_trip_notes(py, cfg),
                thresholdLadder=[dict(text=repr(t), bits=F.double_bits(t)) for t in ladder])


def restricted_document(src, cfg, words, package_id):
    path = src.root / cfg.fasttext_restricted if cfg.fasttext_restricted else None
    rank = {w: i for i, w in enumerate(words)}
    if path is None:
        return dict(packageId=package_id, listVersion='', sourcePath='', sourceSha256='', purpose='', effect='',
                    application=[], entries=[], inVocabulary=0), None
    data = json.loads(path.read_text(encoding='utf-8'))
    entries = []
    for e in data['entries']:
        entries.append(dict(word=e['word'], rank=rank.get(e['word'], -1), selection=e.get('selection', ''),
                            rules=list(e.get('rules', [])), reason=e.get('reason', ''),
                            functionNeighborShare=float(e['function_neighbor_share']) if e.get('function_neighbor_share') is not None else -1.0))
    application = [
        'fastText 보충 연결에서 제한 표기가 한쪽이라도 포함된 연결은 약한 연결로 계산(후보 금지 아님, kw_semantic_engine.ft_candidates)',
        '목표 근접도 계산에서 제한 표기의 fastText 값은 제외(중립 0.5), 목표가 제한 표기면 E5만 사용(target_closeness)',
        '관계 DB 승인(B 규칙)과 E5 뜻 벡터에는 적용하지 않음',
    ]
    return dict(packageId=package_id, listVersion=data.get('version', ''), sourcePath=str(cfg.fasttext_restricted),
                sourceSha256=F.sha256_file(path), purpose=data.get('purpose', ''), effect=data.get('effect', ''),
                application=application, entries=entries, inVocabulary=sum(e['rank'] >= 0 for e in entries)), path


def build_package(py, src, snapshot, snapshot_info, out_dir, progress=print):
    """Write every package file into `out_dir` (a fresh staging folder). Returns the build summary."""
    started = time.monotonic()
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=False)
    log(progress, '준비 데이터 읽기·검사')
    v = load_vocabulary(src)
    ready, records, words = v['ready'], v['records'], v['words']
    raw_settings = json.loads(src.settings_path.read_text(encoding='utf-8'))
    cfg, notices, steps = effective_settings(py, raw_settings)
    ladder = py.engine.threshold_ladder(cfg)
    log(progress, '관계 판정 추출(스냅샷)')
    rel, rel_meta = extract_relations(py, src, snapshot, records)
    if len(rel) and (rel['a'].max() >= len(records) or rel['b'].max() >= len(records)):
        raise ExportError('관계 레코드가 뜻 범위를 벗어났습니다.')
    rel_bytes = rel.tobytes()
    prepared_json = F.json_bytes(rel_meta['prepared'])
    restricted_probe, restricted_path = restricted_document(src, cfg, words, '')
    source_files = []
    for path in [src.vocab_dir / n for n in ('ready.json', 'selected_senses.json', 'vectors.npy', 'fasttext.npy', 'fasttext.json', 'verified.json')] + \
            [src.settings_path, src.models_manifest, src.judge_manifest] + ([restricted_path] if restricted_path else []):
        if path.exists():
            source_files.append(dict(name=str(path.relative_to(src.root)).replace('\\', '/'), bytes=path.stat().st_size,
                                     sha256=F.sha256_file(path)))
    models = json.loads(src.models_manifest.read_text(encoding='utf-8')) if src.models_manifest.exists() else {}
    emb = models.get('embedding', ready.get('models', {}).get('embedding', {}))
    dictionary = ready.get('dictionary', {})
    identity = dict(
        sourceKind=src.kind, appVersion=src.app_version, vocabularyName=ready.get('name', src.vocab_dir.name),
        vocabularyCreatedAt=ready.get('created_at', ''), senseOrderSha256=ready['sense_order_sha256'],
        filterVersion=ready.get('filter_version', ''), vocabularyLimit=int(ready['limit']), minLength=int(ready['min_length']),
        maxLength=int(ready['max_length']), excludeJeok=bool(ready['exclude_jeok']),
        dictionarySourceName=dictionary.get('source', {}).get('name', ''), dictionarySourceBytes=int(dictionary.get('source', {}).get('bytes', 0)),
        dictionarySourceMtimeNs=str(dictionary.get('source', {}).get('mtime_ns', '')), dictionaryCreatedAt=dictionary.get('created_at', ''),
        dictionarySchema=int(dictionary.get('schema', 0)),
        embeddingModel=f"{emb.get('repo', '')}@{emb.get('revision', '')}", embeddingInput=ready.get('input', ''),
        fasttextVersion=v['ftm'].get('version', ''), fasttextModel=v['ftm'].get('model', ''), fasttextModelSha256=v['ftm'].get('model_sha256', ''),
        judgeModel=f"{src.judge.get('repo', '')}@{src.judge.get('revision', '')}", judgePromptSha256=src.judge.get('prompt_sha256', ''),
        judgePromptVersion=src.judge.get('prompt_version', ''), relationVersion=src.relation_version, relationRule=py.relations.RULE,
        relationSnapshotId=rel_meta['snapshot_id'], relationContentSha256=F.sha256_bytes(rel_bytes + prepared_json),
        restrictedListVersion=restricted_probe['listVersion'], settingsSha256=F.sha256_file(src.settings_path),
        effectiveSettingsSha256=F.sha256_bytes(F.json_bytes(asdict(cfg))), sourceFiles=source_files)
    package_id = F.sha256_bytes(F.json_bytes(dict(format=F.FORMAT, schema=F.SCHEMA, identity=identity)))
    log(progress, f'패키지 ID {package_id[:16]}…')
    files = []

    def add(name, role, info, records_count, content='json', **extra):
        files.append(dict(name=name, role=role, bytes=info['bytes'], sha256=info['sha256'], records=int(records_count),
                          contentType=content, **extra))

    offsets = [0]
    flat = []
    for rows in v['word_rows']:
        flat.extend(rows)
        offsets.append(len(flat))
    words_doc = dict(packageId=package_id, count=len(words), words=words, lengths=[len(w) for w in words],
                     senseOffsets=offsets, senseRows=flat, fasttextValid=[int(x) for x in v['ft_ok']])
    add(F.WORDS, 'words', F.write_bytes(out_dir / F.WORDS, F.json_bytes(words_doc)), len(words))
    senses_doc = dict(packageId=package_id, count=len(records), senseIds=[r['sense_id'] for r in records],
                      wordRanks=[int(g) for g in v['groups']], entryIds=[r.get('entry_id', '') for r in records],
                      raws=[r.get('raw', '') for r in records], pos=[r['pos'] for r in records],
                      kinds=[r.get('kind', '') for r in records], categories=[r.get('category', '') for r in records],
                      definitions=[r['definition'] for r in records])
    add(F.SENSES, 'senses', F.write_bytes(out_dir / F.SENSES, F.json_bytes(senses_doc)), len(records))

    def matrix(name, role, array, row_key, key_text):
        rows, cols = array.shape
        data = np.ascontiguousarray(array, dtype='<f4')
        with open(out_dir / name, 'wb') as f:
            f.write(F.header(F.MAGIC_MATRIX, rows, cols, F.DTYPE_FLOAT32, row_key, 4, package_id))
            f.write(memoryview(data).cast('B'))
        info = dict(bytes=(out_dir / name).stat().st_size, sha256=F.sha256_file(out_dir / name))
        add(name, role, info, rows, 'binary', rows=int(rows), cols=int(cols), dtype='float32', byteOrder='little',
            layout='row-major (C order)', rowKey=key_text, headerBytes=F.HEADER.size, elementBytes=4,
            payloadSha256=F.sha256_bytes(memoryview(data).cast('B')))

    log(progress, 'E5 뜻 벡터 쓰기')
    matrix(F.E5, 'e5_sense_vectors', v['vectors'], F.ROW_KEY_SENSE, 'sense row = senses.json index')
    log(progress, 'fastText 표기 벡터 쓰기')
    matrix(F.FASTTEXT, 'fasttext_word_vectors', v['ft'], F.ROW_KEY_WORD, 'word rank = words.json index')
    with open(out_dir / F.RELATIONS, 'wb') as f:
        f.write(F.header(F.MAGIC_RELATIONS, len(rel), F.RELATION_DTYPE.itemsize, F.DTYPE_RELATION_V1, F.ROW_KEY_RELATION,
                         F.RELATION_DTYPE.itemsize, package_id))
        f.write(rel_bytes)
    add(F.RELATIONS, 'relation_records', dict(bytes=(out_dir / F.RELATIONS).stat().st_size, sha256=F.sha256_file(out_dir / F.RELATIONS)),
        len(rel), 'binary', rows=len(rel), cols=F.RELATION_DTYPE.itemsize, dtype='relation-record-v1', byteOrder='little',
        layout='row-major records sorted by (min sense row, max sense row)', rowKey='relation record',
        headerBytes=F.HEADER.size, elementBytes=F.RELATION_DTYPE.itemsize, payloadSha256=F.sha256_bytes(rel_bytes))
    c = rel_meta['counts']
    rel_doc = dict(packageId=package_id, relationVersion=src.relation_version, rule=py.relations.RULE, storedRule=rel_meta['stored_rule'],
                   bRule=B_RULE, approvedStatusName='judged',
                   judge=dict(repo=src.judge.get('repo', ''), revision=src.judge.get('revision', ''), license=src.judge.get('license', ''),
                              promptSha256=src.judge.get('prompt_sha256', ''), promptVersion=src.judge.get('prompt_version', '')),
                   statusNames=rel_meta['status_names'], answerNames=rel_meta['answer_names'], methodNames=rel_meta['method_names'],
                   createdAtValues=rel_meta['created_names'], recordCount=len(rel),
                   counts=dict(storedRowsAllVersions=c['stored_rows_all_versions'], currentVersionRows=c['current_version_rows'],
                               compatible=c['compatible'], excludedOutsideVocabulary=c['excluded_outside_vocabulary'],
                               excludedSignatureMismatch=c['excluded_signature_mismatch'], otherVersionRows=c['other_version_rows'],
                               legacyJudgmentsTableRows=c['legacy_judgments_table_rows'], validYes=c['valid_yes'],
                               decideMismatches=c['decide_mismatches'], preparedRows=c['prepared_rows'], preparedExcluded=c['prepared_excluded'],
                               byStatus=[dict(name=k, count=n) for k, n in sorted(c['by_status'].items())]),
                   snapshot=dict(method=snapshot_info['method'], snapshotId=rel_meta['snapshot_id'],
                                 sourceFiles=snapshot_info['source_files']),
                   unknownPayloadKeys=sorted(rel_meta['unknown_payload_keys']),
                   preparedSources=dict(scopes=rel_meta['prepared']['scopes'], senseRows=rel_meta['prepared']['sense_rows'],
                                        scopeIndex=rel_meta['prepared']['scope_index'], topK=rel_meta['prepared']['top_k']),
                   thresholdLadder=[dict(text=repr(t), bits=F.double_bits(t)) for t in ladder])
    add(F.RELATIONS_META, 'relation_metadata', F.write_bytes(out_dir / F.RELATIONS_META, F.json_bytes(rel_doc)), len(rel))
    restricted_doc, _ = restricted_document(src, cfg, words, package_id)
    add(F.RESTRICTED, 'fasttext_restricted', F.write_bytes(out_dir / F.RESTRICTED, F.json_bytes(restricted_doc)), len(restricted_doc['entries']))
    settings_doc = settings_document(py, src, cfg, notices, steps, raw_settings, ladder, package_id)
    add(F.SETTINGS, 'effective_settings', F.write_bytes(out_dir / F.SETTINGS, F.json_bytes(settings_doc)), len(settings_doc['entries']))
    counts = dict(words=len(words), senses=len(records), e5Dimensions=v['stats']['e5_dim'], fasttextDimensions=v['stats']['fasttext_dim'],
                  fasttextValidWords=v['stats']['fasttext_valid'], relationRecords=len(rel), restrictedEntries=len(restricted_doc['entries']),
                  restrictedInVocabulary=restricted_doc['inVocabulary'], preparedSourceRows=c['prepared_rows'])
    manifest = dict(format=F.FORMAT, schemaVersion=F.SCHEMA, packageId=package_id, createdAt=time.strftime('%Y-%m-%dT%H:%M:%S'),
                    exporter=dict(name='Tools/DataExport/export_unity_data.py', version=F.EXPORTER_VERSION,
                                  python=platform.python_version(), numpy=np.__version__),
                    identity=identity, counts=counts, files=files,
                    notes=['원본 데이터는 읽기만 했습니다. 벡터·점수는 반올림·양자화·재계산 없이 원본 정밀도로 저장했습니다.',
                           '바이너리 파일: 64바이트 헤더(형식 식별자·스키마·행·열·숫자 형식·바이트 순서·배치·행 키·패키지 ID 앞 16바이트) 뒤에 본문.'])
    F.write_bytes(out_dir / F.MANIFEST, F.json_bytes(manifest, compact=False))
    return dict(package_id=package_id, manifest=manifest, cfg=cfg, ladder=ladder, vocabulary=v, relation_meta=rel_meta,
                relations=rel, seconds=time.monotonic() - started, raw_settings=raw_settings, settings_doc=settings_doc)


def verify_package(py, src, snapshot, pkg_dir, progress=print):
    """Independent re-read of a written package checked against the source files (not against the build state)."""
    pkg_dir = Path(pkg_dir)
    fail = []
    manifest = json.loads((pkg_dir / F.MANIFEST).read_text(encoding='utf-8'))
    pid = manifest['packageId']
    if manifest['format'] != F.FORMAT or manifest['schemaVersion'] != F.SCHEMA:
        fail.append('manifest 형식')
    for entry in manifest['files']:
        path = pkg_dir / entry['name']
        if not path.exists():
            fail.append(f"{entry['name']} 없음")
            continue
        if path.stat().st_size != entry['bytes'] or F.sha256_file(path) != entry['sha256']:
            fail.append(f"{entry['name']} 크기·해시")
    if fail:
        raise ExportError('패키지 검증 실패: ' + '; '.join(fail))
    load = lambda n: json.loads((pkg_dir / n).read_text(encoding='utf-8'))
    ready, records = src.ready(), src.records()
    words_doc, senses_doc = load(F.WORDS), load(F.SENSES)
    for name, doc in ((F.WORDS, words_doc), (F.SENSES, senses_doc), (F.RELATIONS_META, load(F.RELATIONS_META)),
                      (F.RESTRICTED, load(F.RESTRICTED)), (F.SETTINGS, load(F.SETTINGS))):
        if doc['packageId'] != pid:
            fail.append(f'{name} packageId')
    if words_doc['words'] != ready['words_by_rank']:
        fail.append('표기 목록·빈도 순서')
    for col, key in (('senseIds', 'sense_id'), ('pos', 'pos'), ('categories', 'category'), ('definitions', 'definition'),
                     ('entryIds', 'entry_id'), ('raws', 'raw'), ('kinds', 'kind')):
        if senses_doc[col] != [r.get(key, '') for r in records]:
            fail.append(f'뜻 {col}')
    by_word = {}
    for r in records:
        by_word.setdefault(r['word'], []).append(r['sense_id'])
    off, rows = words_doc['senseOffsets'], words_doc['senseRows']
    for k, w in enumerate(words_doc['words']):
        if [senses_doc['senseIds'][i] for i in rows[off[k]:off[k + 1]]] != by_word.get(w):
            fail.append(f'단어별 뜻 {w}')
            break
    if [words_doc['words'][g] for g in senses_doc['wordRanks']] != [r['word'] for r in records]:
        fail.append('뜻→표기 대응')
    for name, source_file in ((F.E5, src.vocab_dir / 'vectors.npy'), (F.FASTTEXT, src.vocab_dir / 'fasttext.npy')):
        original = np.load(source_file, mmap_mode='r')
        data = (pkg_dir / name).read_bytes()
        h = F.read_header(data)
        if (h['magic'], h['schema'], h['rows'], h['cols'], h['dtype'], h['byte_order']) != (
                F.MAGIC_MATRIX, F.SCHEMA, original.shape[0], original.shape[1], F.DTYPE_FLOAT32, F.BYTE_ORDER_MARK):
            fail.append(f'{name} 헤더')
        if h['package_prefix'] != bytes.fromhex(pid)[:16]:
            fail.append(f'{name} 패키지 ID')
        if data[F.HEADER.size:] != np.ascontiguousarray(original, dtype='<f4').tobytes():
            fail.append(f'{name} 본문 바이트')
    rel = np.frombuffer((pkg_dir / F.RELATIONS).read_bytes(), dtype=F.RELATION_DTYPE, offset=F.HEADER.size)
    meta = load(F.RELATIONS_META)
    ids = senses_doc['senseIds']
    got = {}
    for r in rel:
        a, b = ids[int(r['a'])], ids[int(r['b'])]
        got[(a, b)] = (meta['statusNames'][int(r['status'])], int(r['valid']), float(r['score']))
    con = sqlite3.connect(Path(snapshot).resolve().as_uri() + '?mode=ro', uri=True)
    sigs = {r['sense_id']: py.relations.signature(r) for r in records}
    expected = {}
    for a, b, asig, bsig, status, valid, score in con.execute(
            'SELECT a,b,a_sig,b_sig,status,valid_yes,score FROM relation_judgments WHERE version=?', (src.relation_version,)):
        if sigs.get(a) == asig and sigs.get(b) == bsig:
            expected[(a, b)] = (status, valid, score)
    con.close()
    if got != expected:
        diff = [k for k in set(got) | set(expected) if got.get(k) != expected.get(k)]
        fail.append(f'관계 레코드 {len(diff)}개 불일치 {diff[:3]}')
    if fail:
        raise ExportError('패키지 검증 실패: ' + '; '.join(fail))
    log(progress, f'패키지 검증 통과: 파일 {len(manifest["files"])}개, 관계 {len(rel):,}행')
    return dict(files=len(manifest['files']), relations=len(rel))


# Activation of the verified package together with its baseline and report: wr_swap.py.
