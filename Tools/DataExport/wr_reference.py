"""Python baseline for the Unity comparison, computed from the ORIGINAL sources with the ORIGINAL Python code:
kw_relations.RelationDB (word_state, words, rejected, evidence), kw_semantic.SemanticLexicon.eligible_ids and
WordView.eligible_ids (vocabulary range), kw_semantic_engine.Simulation.evidence (best sense pair),
threshold_ladder, load_config and numpy dot products exactly as the engine writes them.
It does not read the exported package, so Unity is compared with Python, not with the exporter."""
import dataclasses
import json
import random
import time
from pathlib import Path
from types import SimpleNamespace
import numpy as np
import wr_format as F

STATE_CODE = {'approved': 'A', 'below': 'B', 'error': 'E', 'unchecked': 'U', 'rejected': 'R'}


def sha_rows(array):
    return F.sha256_bytes(memoryview(np.ascontiguousarray(array, dtype='<f4')).cast('B'))


def build_reference(py, src, snapshot, package_id, cfg, raw_settings, scopes, seed=20261019, samples=None, progress=print):
    started = time.monotonic()
    samples = samples or {}
    rng = random.Random(seed)
    ready, records = src.ready(), src.records()
    words = ready['words_by_rank']
    rank = {w: i for i, w in enumerate(words)}
    groups = np.array([rank[r['word']] for r in records], dtype=np.int32)          # SemanticLexicon.groups
    word_senses = {}
    for i, r in enumerate(records):                                                # SemanticLexicon.word_senses
        word_senses.setdefault(r['word'], []).append(i)
    senses_of = [word_senses[w] for w in words]                                    # WordView.senses
    vectors = np.load(src.vocab_dir / 'vectors.npy', mmap_mode='r', allow_pickle=False)
    ft = np.load(src.vocab_dir / 'fasttext.npy', allow_pickle=False)
    ft_ok = np.linalg.norm(ft, axis=1) > 0                                         # WordView.ft_ok
    db = py.relations.RelationDB(Path(snapshot), src.relation_version, records, groups)
    ladder = py.engine.threshold_ladder(cfg)
    progress(f'Python 기준값: RelationDB 스냅샷 {db.snapshot_id}, 뜻 쌍 {len(db.status):,}개')

    ref = dict(referenceSchema=1, packageId=package_id, generatedAt=time.strftime('%Y-%m-%dT%H:%M:%S'), sourceKind=src.kind,
               generator='Tools/DataExport/wr_reference.py: kw_relations.RelationDB, kw_semantic.SemanticLexicon.eligible_ids, '
                         'kw_semantic_engine.Simulation.evidence/threshold_ladder/load_config, numpy float32 dot',
               relationSnapshotId=db.snapshot_id)
    ref['counts'] = dict(words=len(words), senses=len(records), e5Dimensions=int(vectors.shape[1]), fasttextDimensions=int(ft.shape[1]),
                         fasttextValid=int(ft_ok.sum()), compatibleSensePairs=len(db.status))
    ref['words'] = list(words)
    ref['wordLengths'] = [len(w) for w in words]
    flat, offsets = [], [0]
    for w in words:
        flat.extend(records[i]['sense_id'] for i in word_senses[w])
        offsets.append(len(flat))
    ref['wordSenseOffsets'], ref['wordSenseIds'] = offsets, flat
    ref['senses'] = dict(senseIds=[r['sense_id'] for r in records], words=[r['word'] for r in records], pos=[r['pos'] for r in records],
                         categories=[r.get('category', '') for r in records], definitions=[r['definition'] for r in records],
                         entryIds=[r.get('entry_id', '') for r in records], raws=[r.get('raw', '') for r in records],
                         kinds=[r.get('kind', '') for r in records])

    # Vectors: whole-array byte hash plus per-row hashes and leading values looked up by sense ID / surface.
    def vector_samples(array, keys, count, label_of):
        rows = sorted(set([0, len(keys) - 1] + rng.sample(range(len(keys)), min(count, len(keys)))))
        return [dict(key=label_of(i), rowSha256=sha_rows(array[i:i + 1]), leadingBits=[F.float_bits(x) for x in np.asarray(array[i, :4])])
                for i in rows]
    ref['e5'] = dict(rows=int(vectors.shape[0]), cols=int(vectors.shape[1]), sha256=sha_rows(vectors),
                     samples=vector_samples(vectors, records, samples.get('vector_rows', 400), lambda i: records[i]['sense_id']))
    ref['fasttext'] = dict(rows=int(ft.shape[0]), cols=int(ft.shape[1]), sha256=sha_rows(ft), valid=[int(x) for x in ft_ok],
                           samples=vector_samples(ft, words, samples.get('vector_rows', 400), lambda i: words[i]))
    ref['thresholds'] = [dict(text=repr(t), bits=F.double_bits(t)) for t in ladder]

    # Vocabulary ranges, through the original eligible_ids (errors included).
    meta = dict(ready, name=ready.get('name', src.vocab_dir.name))
    lex_ns = SimpleNamespace(meta=meta, words_by_rank=words, groups=groups, _eligible={})
    scope_out = []
    for name, limit, low, high in scopes:
        c = dataclasses.replace(cfg, vocabulary_limit=limit, min_word_length=low, max_word_length=high)
        try:
            ids = np.unique(groups[py.semantic.SemanticLexicon.eligible_ids(lex_ns, c)]).astype(np.int32)   # WordView.eligible_ids
            scope_out.append(dict(name=name, limit=limit, minLength=low, maxLength=high, expectError=False, count=len(ids),
                                  wordsSha256=F.sha256_bytes(json.dumps([int(x) for x in ids]).encode()), ranks=[int(x) for x in ids]
                                  if len(ids) <= samples.get('scope_full', 20000) else []))
        except ValueError as exc:
            scope_out.append(dict(name=name, limit=limit, minLength=low, maxLength=high, expectError=True, count=0, wordsSha256='',
                                  ranks=[], error=str(exc)))
    ref['scopes'] = scope_out

    # Approved word graph per threshold (RelationDB.words), as sorted neighbor ranks.
    graphs = []
    for k, t in enumerate(ladder):
        g = db.words(t)
        offs, nbs = [0], []
        for w in range(len(words)):
            nbs.extend(sorted(g.get(w, {})))
            offs.append(len(nbs))
        graphs.append(dict(thresholdIndex=k, offsets=offs, neighbors=nbs, approvedWordPairs=len(nbs) // 2))
    ref['neighbors'] = graphs
    # Range-filtered neighbors as the generator forms candidates (graph ∩ allowed surfaces).
    scoped = []
    for s in scope_out:
        if s['expectError'] or not s['ranks'] or s['name'] == 'full':
            continue
        allowed = set(s['ranks'])
        pool = [w for w in range(len(words)) if db.words(ladder[-1]).get(w)]
        for w in rng.sample(pool, min(samples.get('scoped_words', 150), len(pool))):
            for k in (0, len(ladder) - 1):
                scoped.append(dict(scope=s['name'], thresholdIndex=k, word=w, neighbors=sorted(set(db.words(ladder[k]).get(w, {})) & allowed)))
    ref['scopedNeighbors'] = scoped

    # Word-pair states at every ladder threshold (RelationDB.word_state) for every word pair with a stored judgment,
    # some reversed pairs and random pairs without any record.
    touched = set()
    for (i, j) in db.status:
        a, b = int(groups[i]), int(groups[j])
        if a != b:
            touched.add((min(a, b), max(a, b)))
    pairs = sorted(touched)
    pairs += [(b, a) for a, b in rng.sample(pairs, min(samples.get('reversed', 500), len(pairs)))]
    untouched = []
    while len(untouched) < min(samples.get('untouched', 500), len(words) * (len(words) - 1) // 2 - len(touched)):
        a, b = rng.randrange(len(words)), rng.randrange(len(words))
        if a != b and (min(a, b), max(a, b)) not in touched and (a, b) not in untouched:
            untouched.append((a, b))
    pairs += untouched
    ref['wordStates'] = dict(a=[p[0] for p in pairs], b=[p[1] for p in pairs],
                             states=[''.join(STATE_CODE[db.word_state(a, b, t)] for t in ladder) for a, b in pairs],
                             touchedPairs=len(touched), untouchedSamples=len(untouched))

    # Best evidence (Simulation.evidence: highest YES score, then E5 similarity, then sense order) in both directions.
    sim = SimpleNamespace(lex=SimpleNamespace(vectors=vectors), graph=db.words, ladder=ladder)
    ev = dict(word=[], neighbor=[], thresholdIndex=[], senseId=[], neighborSenseId=[], scoreBits=[])
    for k in sorted({0, len(ladder) - 1}):
        g = db.words(ladder[k])
        for w in sorted(g):
            for n in sorted(g[w]):
                i, j, s = py.engine.Simulation.evidence(sim, w, n, ladder[k])
                ev['word'].append(w); ev['neighbor'].append(n); ev['thresholdIndex'].append(k)
                ev['senseId'].append(records[i]['sense_id']); ev['neighborSenseId'].append(records[j]['sense_id'])
                ev['scoreBits'].append(F.double_bits(s))
    ref['evidence'] = ev

    rejected = sorted({(min(w, x), max(w, x)) for w in range(len(words)) for x in db.rejected(w)})
    ref['rejected'] = dict(a=[p[0] for p in rejected], b=[p[1] for p in rejected])

    # Sense-pair records and stored per-order answers (RelationDB.status and RelationDB.evidence).
    keys = sorted(db.status)
    picked = keys if len(keys) <= samples.get('sense_pairs', 3000) else rng.sample(keys, samples.get('sense_pairs', 3000))
    sp = dict(senseA=[], senseB=[], status=[], validYes=[], scoreBits=[], forwardAnswer=[], forwardScoreBits=[],
              backwardAnswer=[], backwardScoreBits=[], method=[], createdAt=[], hasPayload=[])
    for n, (i, j) in enumerate(sorted(picked)):
        status, valid, score = db.status[(i, j)]
        sp['senseA'].append(records[i]['sense_id']); sp['senseB'].append(records[j]['sense_id'])
        sp['status'].append(status); sp['validYes'].append(int(valid)); sp['scoreBits'].append(F.double_bits(score))
        e = db.evidence(i, j) if n < samples.get('payloads', 600) else None
        sp['hasPayload'].append(int(e is not None))
        f, b = (e or {}).get('forward'), (e or {}).get('backward')
        sp['forwardAnswer'].append(f['answer'] if f else ''); sp['forwardScoreBits'].append(F.double_bits(f['score']) if f else '')
        sp['backwardAnswer'].append(b['answer'] if b else ''); sp['backwardScoreBits'].append(F.double_bits(b['score']) if b else '')
        sp['method'].append((e or {}).get('method', '')); sp['createdAt'].append((e or {}).get('created_at', ''))
    # Orientation: RelationDB.evidence looks the row up by the sorted sense-ID pair, so senseA/senseB follow the
    # stored order only after sorting; the comparer looks pairs up unordered.
    ref['sensePairs'] = sp

    # Category summary at the start threshold (examples double as targeted checks in Unity).
    t0 = ladder[0]
    cats = {k: [] for k in ('multi_combo_one_approved', 'approved_with_no', 'approved_with_unchecked', 'all_no',
                            'unchecked_remaining', 'below_threshold_yes', 'error_or_partial')}
    counts = {k: 0 for k in cats}
    for a, b in sorted(touched):
        combos = [(i, j) for i in senses_of[a] for j in senses_of[b]]
        rows = [db.status.get((min(i, j), max(i, j))) for i, j in combos]
        approved = sum(1 for r in rows if r and r[0] == 'judged' and r[1] and r[2] >= t0)
        no = sum(1 for r in rows if r and r[0] == 'judged' and not r[1])
        missing = sum(1 for r in rows if r is None)
        state = db.word_state(a, b, t0)
        tags = []
        if approved == 1 and len(combos) > 1: tags.append('multi_combo_one_approved')
        if state == 'approved' and no: tags.append('approved_with_no')
        if state == 'approved' and missing: tags.append('approved_with_unchecked')
        if state == 'rejected': tags.append('all_no')
        if state != 'approved' and missing: tags.append('unchecked_remaining')
        if state == 'below': tags.append('below_threshold_yes')
        if state == 'error': tags.append('error_or_partial')
        for tag in tags:
            counts[tag] += 1
            if len(cats[tag]) < 40:
                cats[tag].append((a, b, state))
    ref['categories'] = [dict(name=k, thresholdIndex=0, count=counts[k], a=[x[0] for x in v], b=[x[1] for x in v],
                              states=[x[2] for x in v]) for k, v in cats.items()]

    # Similarities exactly as the engine computes them: fastText ft@ft[w] (matrix-vector, float32) and E5 v[i]@v[j].
    fts = dict(a=[], b=[], f32Bits=[], f64Bits=[])
    usable = np.flatnonzero(ft_ok)
    if len(usable):
        bases = [int(x) for x in rng.sample(list(usable), min(samples.get('ft_targets', 300), len(usable)))]
        for b in bases:
            col = ft @ ft[b]
            others = rng.sample(list(usable), min(10, len(usable))) + [b]
            for a in others:
                fts['a'].append(int(a)); fts['b'].append(b); fts['f32Bits'].append(F.float_bits(col[a]))
                fts['f64Bits'].append(F.double_bits(float(np.dot(ft[a].astype(np.float64), ft[b].astype(np.float64)))))
    ref['fasttextSimilarity'] = fts
    es = dict(senseA=[], senseB=[], f32Bits=[], f64Bits=[])
    pairs_e5 = [(rng.randrange(len(records)), rng.randrange(len(records))) for _ in range(samples.get('e5_pairs', 3000))]
    pairs_e5 += [(i, i) for i in rng.sample(range(len(records)), min(20, len(records)))]
    for i, j in pairs_e5:
        es['senseA'].append(records[i]['sense_id']); es['senseB'].append(records[j]['sense_id'])
        es['f32Bits'].append(F.float_bits(vectors[i] @ vectors[j]))
        es['f64Bits'].append(F.double_bits(float(np.dot(np.asarray(vectors[i], dtype=np.float64), np.asarray(vectors[j], dtype=np.float64)))))
    ref['e5Similarity'] = es

    # Restricted surfaces as Simulation marks them (restricted_words ∩ vocabulary).
    if cfg.fasttext_restricted:
        if src.kind == 'real':
            names = py.engine.restricted_words(cfg.fasttext_restricted)
        else:
            names = {e['word'] for e in json.loads((src.root / cfg.fasttext_restricted).read_text(encoding='utf-8'))['entries']}
    else:
        names = set()
    ref['restricted'] = dict(ranks=sorted(rank[w] for w in names if w in rank), outsideVocabulary=sorted(w for w in names if w not in rank))
    ref['settings'] = [dict(key=k, valueJson=json.dumps(v, ensure_ascii=False)) for k, v in dataclasses.asdict(cfg).items()]
    ref['generatorSeconds'] = time.monotonic() - started
    progress(f'Python 기준값 생성 {ref["generatorSeconds"]:.1f}초: 단어 쌍 상태 {len(pairs):,}개, 근거 {len(ev["word"]):,}개')
    return ref
