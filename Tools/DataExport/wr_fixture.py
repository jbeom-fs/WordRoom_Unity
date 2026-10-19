"""Synthetic source data for cases the real data does not contain (partial and invalid judgments, scores exactly on
or just below a threshold, excluded rows, a word without a fastText vector, non-BMP text). It is laid out like a
prepared Python vocabulary and exported and baselined by the same code as the real data. The relation DB is created
with the original kw_relations.open_db and decide, in a temporary folder only."""
import hashlib
import json
from contextlib import closing
from pathlib import Path
import numpy as np
from wr_source import Source

VERSION = hashlib.sha256(b'wordroom-fixture-relation-version-1').hexdigest()
OLD_VERSION = hashlib.sha256(b'wordroom-fixture-relation-version-0').hexdigest()

# (sense_id, word, pos, category, definition); records are sorted by integer sense_id like the real cache.
SENSES = [
    ('99', '배', '명사', '교통', '사람이나 짐 따위를 싣고 물 위로 떠다니도록 나무나 쇠로 만든 물건.'),
    ('102', '경기', '명사', '체육', '일정한 규칙 아래 기량과 기술을 겨룸. 「競技」 — "따옴표"와 \\역슬래시\\ 포함.'),
    ('205', '사과', '명사', '식물', '사과나무의 열매. 둥글고 붉음.'),
    ('210', '배', '명사', '식물', '배나무의 열매.'),
    ('300', '체육', '명사', '교육', '일정한 운동 따위를 통하여 신체를 튼튼하게 단련시키는 일.'),
    ('301', '경영', '명사', '경제', '기업이나 사업 따위를 관리하고 운영함.'),
    ('410', '과일', '명사', '', '사람이 먹을 수 있는 나무 따위의 열매.'),
    ('512', '바다', '명사', '지리', '지구 위에서 육지를 제외한 부분으로 짠물이 괴어 하나로 이어진 넓고 큰 부분. 𝄞 비BMP 문자'),
    ('600', '사과', '명사', '', '자기의 잘못을 인정하고 용서를 빎.'),
    ('700', '선박', '명사', '교통', '사람이나 물건 따위를 싣고 물 위로 떠다니도록 만든 배.'),
    ('801', '감정', '명사', '심리', '어떤 현상이나 일에 대하여 일어나는 마음이나 느끼는 기분.'),
    ('900', '조각', '명사', '미술', '재료를 새기거나 깎아서 입체 형상을 만듦.'),
    ('1000', '경기', '명사', '경제', '매매나 거래에 나타나는 호황·불황 따위의 경제 활동 상태. 「經紀」'),
    ('1001', '배', '명사', '의학', '사람이나 동물의 몸에서 위장, 창자, 콩팥 따위의 내장이 들어 있는 곳.'),
    ('1100', '가나다라마바사아', '명사', '', '여덟 글자 표기 시험용 뜻풀이.'),
    ('1200', '나', '명사', '음악', '서양 음악의 칠음 체계에서 일곱째 음 이름. 🎵'),
]
WORDS = ['경기', '체육', '경영', '배', '사과', '과일', '바다', '선박', '감정', '조각', '가나다라마바사아', '나']
RESTRICTED = [dict(word='감정', selection='auto', rules=['R2'], function_neighbor_share=0.6, reason='시험용 제한 표기'),
              dict(word='나', selection='manual', rules=['R1'], function_neighbor_share=0.7, reason='시험용 제한 표기(1글자)'),
              dict(word='없는표기', selection='manual', rules=['R3'], function_neighbor_share=0.5, reason='어휘에 없는 제한 표기')]
SETTINGS = {'vocabulary_limit': 12, 'min_word_length': 2, 'max_word_length': 8, 'search_budget': 128, 'candidate_count': 64,
            'relation_threshold': 0.8, 'diversity': 0.6, 'goal_weight': 0.0, 'unknown_key': 1}


def yes(score):return dict(answer='YES', score=score)


def no(score=0.01):return dict(answer='NO', score=score)


# (sense a, sense b, forward, backward, method, extra payload, version, signature override)
JUDGMENTS = [
    ('102', '300', yes(0.93), yes(0.91), 'prefix-b16', {}, VERSION, None),                 # 경기(競技)–체육 approved
    ('1000', '300', no(), no(0.02), 'prefix-b16', {}, VERSION, None),                       # 경기(經紀)–체육 NO
    ('1000', '301', no(0.2), yes(0.8), 'prefix-b16', {}, VERSION, None),                    # 경기(經紀)–경영 YES exactly 0.8
    ('512', '99', yes(0.7999999999999999), no(0.3), 'prefix-b16', {}, VERSION, None),       # 배(ship)–바다 YES just below 0.8
    ('210', '512', no(), no(), 'prefix-b16', {}, VERSION, None),                            # 배(pear)–바다 NO (belly unchecked)
    ('700', '99', yes(0.99), None, 'prefix-b16', {}, VERSION, None),                        # 배(ship)–선박 partial: one order only
    ('210', '700', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('1001', '700', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('210', '410', dict(answer='INPUT_TOO_LONG', score=0.0), yes(0.95), 'prefix-b16', {}, VERSION, None),   # invalid
    ('1001', '410', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('410', '99', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('205', '410', yes(0.97), yes(0.96), 'prefix-b16', {}, VERSION, None),                  # 사과(apple)–과일
    ('410', '600', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('205', '99', no(), no(), 'prefix-b16', {}, VERSION, None),                             # 사과×배: every combination NO
    ('205', '210', no(0.4), no(0.45), 'prefix-b16', {}, VERSION, None),
    ('1001', '205', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('600', '99', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('210', '600', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('1001', '600', no(), no(), 'prefix-b16', {}, VERSION, None),
    ('600', '801', no(), no(), 'prefix-b16', {}, VERSION, None),                            # 감정×사과: one NO, one unchecked
    ('300', '301', yes(0.5000000000000001), no(0.4), 'legacy-b8-recheck',
     dict(cached_scores=[yes(0.61), no(0.3)]), VERSION, None),                              # 체육–경영 just above 0.5
    ('1100', '900', yes(0.65), yes(0.6), 'prefix-b16', {}, VERSION, None),                  # 조각–가나다라마바사아
    ('1200', '512', yes(0.85), no(0.2), 'legacy-b8', dict(backward_skipped=False), VERSION, None),   # 나–바다
    ('512', '700', yes(0.88), yes(0.87), 'legacy-b8-recheck',
     dict(cached_scores=[yes(0.9), yes(0.7)], backward_skipped=False), VERSION, None),      # 선박–바다
    ('210', '99', yes(0.9), yes(0.9), 'prefix-b16', {}, VERSION, None),                     # same surface (배–배)
    ('102', '300', yes(0.99), yes(0.99), 'prefix-b16', {}, OLD_VERSION, None),             # other judge version: excluded
    ('300', '512', yes(0.95), yes(0.95), 'prefix-b16', {}, VERSION, ('bad-signature-0', None)),   # changed definition: excluded
    ('512', '999999', yes(0.95), yes(0.95), 'prefix-b16', {}, VERSION, None),             # sense outside vocabulary: excluded
]
SCOPES = [('full', 12, 1, 8), ('top8', 8, 1, 8), ('top8_min2', 8, 2, 8), ('len1to4', 12, 1, 4),
          ('over_prepared', 13, 1, 8), ('max9', 12, 1, 9), ('min0', 12, 0, 8)]


def write_json(path, data):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')


def build_fixture_source(py, root):
    root = Path(root)
    vocab = root / 'vocab'
    records = [dict(sense_id=s, entry_id=f'E{s}', word=w, raw=w + ('1' if s in ('102', '99', '205') else ''), pos=p,
                    kind='일반어', category=c, definition=d, usable=1, reason='') for s, w, p, c, d in SENSES]
    records.sort(key=lambda r: int(r['sense_id']))
    stamp = hashlib.sha256(json.dumps([r['sense_id'] for r in records]).encode()).hexdigest()
    rng = np.random.default_rng(7)
    e5 = rng.standard_normal((len(records), 16)).astype(np.float32)
    e5 /= np.linalg.norm(e5, axis=1, keepdims=True)
    ft = rng.standard_normal((len(WORDS), 8)).astype(np.float32)
    ft /= np.linalg.norm(ft, axis=1, keepdims=True)
    ft[WORDS.index('조각')] = 0                    # masked like kw_semantic.build_fasttext: no usable vector
    vocab.mkdir(parents=True, exist_ok=True)
    np.save(vocab / 'vectors.npy', e5)
    np.save(vocab / 'fasttext.npy', ft)
    write_json(vocab / 'selected_senses.json', records)
    dictionary = dict(schema=1, source=dict(name='fixture_dictionary.zip', bytes=1234, mtime_ns=1), counts=dict(senses=len(records)),
                      created_at='2026-10-19T00:00:00')
    write_json(vocab / 'ready.json', dict(schema=1, name='fixture_v1', dictionary=dictionary,
                                          models=dict(embedding=dict(repo='fixture/e5', revision='0')), limit=12, min_length=1,
                                          max_length=8, exclude_jeok=True, filter_version='fixture-filter-v1', senses=len(records),
                                          words=len(WORDS), sense_order_sha256=stamp, words_by_rank=WORDS,
                                          input='fixture', created_at='2026-10-19T00:00:00'))
    write_json(vocab / 'fasttext.json', dict(version='fixture-ft-1', model='fixture fastText', model_sha256='0' * 64, words=len(WORDS),
                                             with_vector=len(WORDS) - 1, missing=['조각'], usable=['known', 'subword']))
    write_json(vocab / 'verified.json', dict(ok=True))
    write_json(root / 'settings.json', SETTINGS)
    write_json(root / 'configs/fasttext_restricted.json', dict(version='fixture-restrict-1', purpose='시험용', effect='시험용',
                                                               entries=RESTRICTED))
    judge = dict(repo='fixture/judge', revision='0', license='n/a')
    write_json(root / 'models/judge_manifest.json', judge)
    write_json(root / 'models/manifest.json', dict(embedding=dict(repo='fixture/e5', revision='0')))
    db_path = root / 'relations.sqlite'
    db_path.unlink(missing_ok=True)
    sigs = {r['sense_id']: py.relations.signature(r) for r in records}
    with closing(py.relations.open_db(db_path)) as con:
        for n, (a, b, fwd, bwd, method, extra, version, sig) in enumerate(JUDGMENTS):
            a, b = py.relations.pair_key(a, b)
            payload = dict(forward=fwd, backward=bwd, method=method, **extra)
            status, valid, score = py.relations.decide(fwd, bwd)
            asig = sig[0] if sig and sig[0] else sigs.get(a, 'outside0000000000')
            bsig = sig[1] if sig and sig[1] else sigs.get(b, 'outside0000000000')
            con.execute('INSERT INTO relation_judgments VALUES (?,?,?,?,?,?,?,?,?,?)',
                        (version, a, b, asig, bsig, status, valid, score, json.dumps(payload), f'2026-10-19T00:00:{n:02d}'))
        for sid, scope, k in (('102', 'fixture-scope-a', 32), ('300', 'fixture-scope-a', 32), ('99', 'fixture-scope-b', 16)):
            con.execute('INSERT INTO prepared_sources VALUES (?,?,?,?,?,?)', (VERSION, scope, sid, sigs[sid], k, '2026-10-19T00:00:00'))
        con.execute('INSERT INTO prepared_sources VALUES (?,?,?,?,?,?)', (VERSION, 'fixture-scope-a', '512', 'stale-signature', 32, 'x'))
        con.commit()
    judge_full = dict(judge, prompt_sha256='0' * 64, prompt_version='fixture-prompt')
    return Source(kind='synthetic', root=root, vocab_dir=vocab, relations_db=db_path, relation_version=VERSION, judge=judge_full,
                  settings_path=root / 'settings.json', app_version=py.engine.VERSION, models_manifest=root / 'models/manifest.json',
                  judge_manifest=root / 'models/judge_manifest.json')
