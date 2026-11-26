"""Headword rules compared in Docs/WordRoom_단어링크_판정개선_제안서.md.

  current      the C# rule as it is (ShelfLinkText.Headword)
  compare      option A: the comparison list matched by stem (같고, 같으며, 비슷한데 …)
  context      option B (recommended): candidates filtered by the 받침 rule, 와/과 boundary conflicts decided by context
  context-raw  option B without the per-sense and per-eojeol split exceptions (which exceptions B reproduces)
  context-all  option B with the same decision for every whole-eojeol vs particle conflict (follow-up scope, 2.3)

Every rule has headword(eojeol, prev, next, next2, senseId) -> (headword or None, reason). Neighbors are None
when anything but spaces separates them, as in ShelfLinkText.FindCandidates.
"""
from collections import Counter

from wr_links import GENERAL_FIELDS, CsTables, eojeols, final_consonant


class Current:
    name = 'current'

    def __init__(self, pkg, tables=None):
        self.pkg = pkg
        self.t = tables or CsTables()

    def exceptions(self, e, prev, sid):
        t, entry = self.t, self.pkg.is_entry
        h = t.sense_splits.get(f'{sid}|{e}') if sid is not None else None
        if h and e.startswith(h) and entry(h):
            return h, 'sense'
        if e in t.not_noun:
            return None, 'notnoun'
        h = t.counter_forms.get(e)
        if h and t.is_quantity(prev) and e.startswith(h) and entry(h):
            return h, 'counter'
        h = t.word_splits.get(e)
        if h and e.startswith(h) and entry(h):
            return h, 'word'
        return None

    def comparison(self, e, nxt):
        if nxt is None or nxt not in self.t.comparison_words or len(e) < 2 or e[-1] not in '와과':
            return None
        stem = e[:-1]
        j = final_consonant(stem[-1])
        if j is not None and (j != 0) != (e[-1] == '과'):
            return None
        return stem if self.pkg.is_entry(stem) else None

    def unique_split(self, e):
        found = None
        for end in self.t.endings:
            if len(e) <= len(end) or not e.endswith(end):
                continue
            stem = e[:-len(end)]
            if not self.pkg.is_entry(stem):
                continue
            if found is not None:
                return None, 'ambig'
            found = stem
        return found, ('split' if found else 'none')

    def headword(self, e, prev, nxt, nxt2, sid):
        r = self.exceptions(e, prev, sid)
        if r:
            return r
        c = self.comparison(e, nxt)
        if c:
            return c, 'comp'
        if self.pkg.is_entry(e):
            return e, 'whole'
        return self.unique_split(e)


# ---- option A ---------------------------------------------------------------------------------------------------------

COMPARISON_STEMS = ('같', '다르', '다른', '달리', '달라', '비슷', '닮', '유사', '동일', '흡사')


def fits(stem, ending):
    """받침 rule: the particle allomorph fits the stem's last syllable (lenient for copula forms with 이)."""
    j = final_consonant(stem[-1])
    if j is None:
        return True
    cons, rieul = j != 0, j == 8
    if ending.startswith('과'): return cons
    if ending.startswith('와'): return not cons
    if ending in ('은', '이', '을'): return cons
    if ending in ('는', '가', '를'): return not cons
    if ending.startswith('으로'): return cons and not rieul
    if ending.startswith('로'): return not cons or rieul
    if ending.startswith('라') or ending.startswith('였'): return not cons
    return True


class CompareExtended(Current):
    name = 'compare'

    def comparison(self, e, nxt):
        if nxt is None or len(e) < 2 or e[-1] not in '와과' or not nxt.startswith(COMPARISON_STEMS):
            return None
        stem = e[:-1]
        return stem if fits(stem, e[-1]) and self.pkg.is_entry(stem) else None


# ---- option B ---------------------------------------------------------------------------------------------------------

WA_ENDINGS = {'와', '과', '와의', '과의', '와는', '과는', '와도', '과도'}
# 3-1: comparison, comitative and reciprocal predicates after a bare "X과"
CUE_PREDICATE = COMPARISON_STEMS + (
    '딴판', '함께', '더불어', '공동', '관련', '관계되', '관계없', '관계있', '관계가', '관계를',
    '싸우', '싸움', '맞서', '부딪', '견주', '사귀', '벗하', '결합', '반응', '대립', '연합', '동맹', '협력', '화합',
    '교류', '접촉', '연락', '어울리', '섞', '혼동', '일치', '구별', '구분', '비교', '대비', '작용', '충돌', '경쟁',
    '상대하', '짝')
# 3-2: relation nouns after "X과의 / X과는 / X과도" (next or the one after)
CUE_RELATION = CUE_PREDICATE + ('관계', '대응', '융화', '조화', '차이', '사이', '거래', '전쟁', '교섭', '갈등', '연관', '대조')
STEM_USE = ['을', '를', '이', '가', '은', '는', '에', '에서', '로', '으로', '에게', '도', '만']
WORD_USE = ['을', '를', '이', '가', '에', '에서', '로', '으로', '에게', '만']   # no 는/도/의: after 과/와 they form 과는/과도/과의


class Context(Current):
    name = 'context'
    use_exceptions = True

    def __init__(self, pkg, tables=None):
        super().__init__(pkg, tables)
        self.uses = Counter()                                        # eojeol -> count over every display definition
        for text in pkg.display:
            if text:
                for _, e, _ in eojeols(text):
                    self.uses[e] += 1
        self.sense_field = {pkg.sense_ids[i]: pkg.categories[i] for i in range(pkg.count)}

    def stem_uses(self, x):
        return self.uses[x] + sum(self.uses[x + p] for p in STEM_USE)

    def word_uses(self, w):
        return sum(self.uses[w + p] for p in WORD_USE)

    def candidates(self, e):
        out = [(e, '')] if self.pkg.is_entry(e) else []
        for end in self.t.endings:
            if len(e) > len(end) and e.endswith(end):
                stem = e[:-len(end)]
                if self.pkg.is_entry(stem) and fits(stem, end):
                    out.append((stem, end))
        return out

    @staticmethod
    def wa_conflict(cands):
        """(X, X과, rest) when the candidates differ only at the 와/과 boundary: X + 과(의) vs X과 (+ 의)."""
        for x, end in cands:
            if end in WA_ENDINGS:
                w, rest = x + end[0], end[1:]
                if (w, rest) in cands:
                    return x, w, rest
        return None

    def is_wa_eojeol(self, t):
        return bool(t) and len(t) >= 2 and t[-1] in '와과' and fits(t[:-1], t[-1]) and self.pkg.is_entry(t[:-1])

    def field_match(self, w, nxt, sid):
        wf = self.pkg.fields(w)
        if not wf:
            return False
        ctx = {self.sense_field.get(sid, '')} - GENERAL_FIELDS
        if nxt:
            for y, end in self.candidates(nxt):
                if end in STEM_USE + ['의', ''] and self.pkg.only_specialized(y):
                    ctx |= self.pkg.fields(y)
        return bool(wf & ctx)

    def decide(self, x, w, rest, prev, nxt, nxt2, sid):
        bare = rest == ''
        if nxt and bare and nxt.startswith(CUE_PREDICATE):
            return x, 'cue'                                                      # 3-1
        if nxt and not bare and (nxt.startswith(CUE_RELATION) or (nxt2 and nxt2.startswith(CUE_RELATION))):
            return x, 'rel'                                                      # 3-2
        if nxt and bare:
            if nxt == x or any(nxt == x + p for p in STEM_USE + ['의']):
                return x, 'rep'                                                  # 3-3
            if nxt2 in ('아울러', '통틀어') and nxt[-1] in '을를' and not self.is_wa_eojeol(prev):
                return x, 'formula'                                              # 3-4
        if bare and not nxt:
            return w, 'end'                                                      # 3-5
        if bare and nxt.startswith('따위'):
            return w, 'ttawi'                                                    # 3-6
        if self.field_match(w, nxt, sid):
            return w, 'field'                                                    # 3-7
        uw, ux = self.word_uses(w), self.stem_uses(x)                            # 3-8
        if uw >= 2 * max(ux, 1) and uw >= 3:
            return w, 'use-w'
        if bare and ux >= 2 * max(uw, 1):
            return x, 'use-x'
        return None, 'undecided'                                                 # 3-9

    def other_conflict(self, cands, prev, nxt, nxt2, sid):
        return None

    def headword(self, e, prev, nxt, nxt2, sid):
        if self.use_exceptions:
            r = self.exceptions(e, prev, sid)
        else:                                                      # keep the verb form and counter exceptions only
            r = self.exceptions(e, prev, None)
            if r and r[1] == 'word':
                r = None
        if r:
            return r
        cands = self.candidates(e)
        if not cands:
            return None, 'none'
        k = self.wa_conflict(cands)
        if k:
            w, why = self.decide(*k, prev, nxt, nxt2, sid)
            return w, 'K:' + why
        g = self.other_conflict(cands, prev, nxt, nxt2, sid)
        if g:
            return g
        if cands[0][1] == '':
            return e, 'whole'
        if len(cands) == 1:
            return cands[0][0], 'split'
        return None, 'ambig'


class ContextRaw(Context):
    name = 'context-raw'
    use_exceptions = False


class ContextAll(Context):
    """Follow-up scope (proposal 2.3): the 3-5, 3-7 and 3-8 cues for whole eojeol vs any particle, not only 와/과."""
    name = 'context-all'

    def other_conflict(self, cands, prev, nxt, nxt2, sid):
        if cands[0][1] != '':
            return None
        w = cands[0][0]
        for x, end in cands[1:]:
            if end in self.t.particle_endings:
                if not nxt:
                    return w, 'G:end'
                if self.field_match(w, nxt, sid):
                    return w, 'G:field'
                uw, ux = self.word_uses(w), self.stem_uses(x)
                if uw >= 2 * max(ux, 1) and uw >= 3:
                    return w, 'G:use-w'
                if ux >= 2 * max(uw, 1):
                    return x, 'G:use-x'
                return None, 'G:undecided'
        return None


RULES = {r.name: r for r in (Current, CompareExtended, Context, ContextRaw, ContextAll)}
