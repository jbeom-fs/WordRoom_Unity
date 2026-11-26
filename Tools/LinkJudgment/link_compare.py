"""Link rule experiments on the active dictionary package (offline; never run by Unity).

  python link_compare.py diff context        shown links of a rule vs the current rule, with the change list
  python link_compare.py diff compare        the same for option A (rules: see wr_link_rules.py)
  python link_compare.py cases               the sentences of the proposal's table (section 5), current vs context
  python link_compare.py exceptions          split exceptions that the context rule reproduces without them
  python link_compare.py cues                와/과 boundary decisions of the context rule by cue
  python link_compare.py conflicts           the headwords that read like "noun + 와/과", with their use counts

Options: --goal (default 지구), --limit (vocabulary, default 15000), --min-length (default 2), as in the scene and
the EditMode tests. Any Python 3.9+ works (standard library only). Outputs, besides the console summary:
  DataValidation/links/diff_<rule>.tsv        every changed shown link: kind, before, after, reason, room, context
  DataValidation/links/cues_whole.tsv         eojeols the context rule reads as the whole headword, with the cue
The numbers of Docs/WordRoom_단어링크_판정개선_제안서.md come from these commands on package 6773adfc….
"""
import argparse
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from wr_link_rules import RULES, Context, ContextRaw, Current  # noqa: E402
from wr_links import OUT, Graph, Package, bracket, eojeols, is_hangul, has_batchim  # noqa: E402

# (SenseId, fragment of its display definition) — the real sentences of the proposal's table
CASES = [
    ('35148', '말과 같으며'), ('598839', '말과 같고'), ('126851', '말과의 포유류. 말과 비슷한데'),
    ('146729', '말과의 포유류'), ('397604', '그 말과 다른 말과의 문법적 관계'), ('25981', '그 말과 다른 말과의 문법적 관계'),
    ('17331', '말과 동작으로'), ('64490', '말과 글로'), ('65548', '정신과 육체를'), ('14271', '자연과 벗하며'),
    ('138335', '자연과의 융화'), ('244222', '행정과는 달리'), ('563106', '콩과 식물의'), ('1966', '콩과의'),
    ('535316', '산과 의사'), ('13536', '산과 들'), ('315312', '강과 시내를'), ('32104', '등과 날개는'),
    ('23535', '윗사람을 도와'), ('64690', '원인과 결과 사이'), ('407574', '결과 당선자가'), ('92918', '결과.'),
    ('29811', '결과의 비율'), ('68510', '영화용 음화'), ('438835', '신경과 의사'), ('15109', '방과 후에'),
    ('368393', '원인과 경과 따위'), ('66639', '성과 이름을'), ('1396731', '공과 계열'), ('377494', '공과 대학'),
    ('16392', '핵과 이를'), ('570548', '경과 중에도'), ('70168', '인과 관계와'),
]
# sentences not in the package, judged on its headwords (no sense, so no sense field)
SYNTHETIC = ['사과 같은', '사과 주스를 마셨다', '정신과 진료를 받았다', '생각과 다른', '결과와 비슷한',
             '말과 포유류', '말과, 다른', '말과 글로 표현하다', '말과 동작으로 보여 주다']


def room_label(pkg, row):
    return f'{pkg.word_of(row)}({pkg.sense_ids[row]},{pkg.categories[row]})'


def snippet(text, start, before=10, after=18):
    return text[max(0, start - before):start + after].replace('\n', ' ')


def fragment_links(links, start, length):
    return [(s - start, w) for s, w in links if start <= s < start + length]


# ---- diff -------------------------------------------------------------------------------------------------------------

def cmd_diff(pkg, args):
    base = Graph(pkg, Current(pkg), args.goal)
    rule = RULES[args.rule](pkg)
    new = Graph(pkg, rule, args.goal)
    print(f'package {pkg.package_id[:8]} · goal {args.goal} · top {pkg.vocabulary_limit:,} · {pkg.min_length}+ letters')
    for name, g in (('current', base), (args.rule, new)):
        print(f'  {name:12} rooms {len(g.exits):,}  valid {sum(g.valid.values()):,}  shown links {g.shown_count():,}')

    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / f'diff_{args.rule}.tsv'
    kinds, causes, targets = Counter(), Counter(), Counter()
    with path.open('w', encoding='utf-8', newline='\n') as f:
        f.write('kind\tbefore\tafter\treason\troom\tcontext\n')
        for row in sorted(base.extractions):
            a, b = dict(base.shown(row)), dict(new.shown(row))
            if a == b:
                continue
            reasons = {s: why for s, _, _, why in new.extractions[row].decisions}
            decided_before = {s: w for s, _, w, _ in base.extractions[row].decisions}
            decided_after = {s: w for s, _, w, _ in new.extractions[row].decisions}
            text = pkg.display[row]
            for s in sorted(set(a) | set(b)):
                if a.get(s) == b.get(s):
                    continue
                kind = 'changed' if s in a and s in b else ('removed' if s in a else 'added')
                why = reasons.get(s, '')
                # same decision here: the link only moved (an earlier repeat appeared or disappeared)
                moved = decided_before.get(s) == decided_after.get(s)
                cause = ('와/과 경계' if why.startswith('K:') else '다른 조사 충돌' if why.startswith('G:')
                         else '중복 제거 위치 이동' if moved else '받침 규칙·기타')
                kinds[kind] += 1
                causes[(cause, kind)] += 1
                if why.startswith('K:'):
                    targets[a.get(s) or b.get(s)] += 1
                f.write(f'{kind}\t{a.get(s)}\t{b.get(s)}\t{why}\t{room_label(pkg, row)}\t{snippet(text, s)}\n')
    print(f'changed shown links: {dict(kinds)}  (total {sum(kinds.values())})')
    for (cause, kind), n in sorted(causes.items()):
        print(f'  {cause} · {kind}: {n}')
    if targets:
        print('와/과 boundary changes by headword: ' + ', '.join(f'{w} {n}' for w, n in targets.most_common(15)))
        print_extraction_split(pkg, base, new)
    print(f'change list: {path}')


def print_extraction_split(pkg, base, new):
    """와/과 boundary decisions that differ: shown on screen, or only in the extraction (and why)."""
    c = Counter()
    for row, ex in new.extractions.items():
        old = base.extractions[row]
        shown = set(base.shown(row))
        word = pkg.word_of(row)
        for (s, e, w0, _), (_, _, w1, why) in zip(old.decisions, ex.decisions):
            if not why.startswith('K:') or w0 == w1:
                continue
            if not w0 or not pkg.is_door(w0):
                c['no link now'] += 1
            elif (s, w0) in shown:
                c['shown'] += 1
            elif not base.is_room_sense(row):
                c['extraction only: sense without a room'] += 1
            elif w0 == word:
                c['extraction only: own room'] += 1
            elif any(w == w0 and st < s for st, w in old.links):
                c['extraction only: repeat in the sense'] += 1
            else:
                c['extraction only: target room without exit'] += 1
    print(f'와/과 decisions that differ: {sum(c.values())}')
    for k, n in c.most_common():
        print(f'  {k}: {n}')


# ---- cases ------------------------------------------------------------------------------------------------------------

def cmd_cases(pkg, args):
    base = Graph(pkg, Current(pkg), args.goal)
    rule = Context(pkg)
    new = Graph(pkg, rule, args.goal)
    for sid, frag in CASES:
        row = pkg.row_of[sid]
        text = pkg.display[row]
        k = text.find(frag)
        if k < 0:
            print(f'!! {sid}: "{frag}" not found')
            continue
        room = base.is_room_sense(row)
        before = base.shown(row) if room else base.extractions[row].links
        after = new.shown(row) if room else new.extractions[row].links
        why = [f'{e}→{w or "-"} {r}' for s, e, w, r in new.extractions[row].decisions if k <= s < k + len(frag)]
        print(f'{room_label(pkg, row)}{"" if room else " [no room: extraction]"}\n'
              f'  current {bracket(frag, fragment_links(before, k, len(frag)))}\n'
              f'  context {bracket(frag, fragment_links(after, k, len(frag)))}   ({"; ".join(why)})')
    print('\nsynthetic (package headwords, no sense field):')
    for text in SYNTHETIC:
        out = []
        for name, r in (('current', Current(pkg)), ('context', rule)):
            ej = eojeols(text)
            links, why = [], []
            for i, (s, e, spaces) in enumerate(ej):
                prev = ej[i - 1][1] if i > 0 and ej[i - 1][2] else None
                nxt = ej[i + 1][1] if spaces else None
                nxt2 = ej[i + 2][1] if spaces and i + 2 < len(ej) and ej[i + 1][2] else None
                w, reason = r.headword(e, prev, nxt, nxt2, None)
                if w and pkg.is_door(w):
                    links.append((s, w))
                why.append(f'{e}→{w or "-"} {reason}')
            out.append(f'  {name} {bracket(text, links)}' + (f'   ({"; ".join(why)})' if name == 'context' else ''))
        print(text + '\n' + '\n'.join(out))


# ---- exceptions -------------------------------------------------------------------------------------------------------

def cmd_exceptions(pkg, args):
    raw = ContextRaw(pkg)
    t = raw.t
    found = {}
    for row in range(pkg.count):
        if not pkg.definitions[row]:
            continue
        ej = eojeols(pkg.display[row])
        sid = pkg.sense_ids[row]
        for i, (s, e, spaces) in enumerate(ej):
            if e not in t.word_splits and f'{sid}|{e}' not in t.sense_splits:
                continue
            prev = ej[i - 1][1] if i > 0 and ej[i - 1][2] else None
            nxt = ej[i + 1][1] if spaces else None
            nxt2 = ej[i + 2][1] if spaces and i + 2 < len(ej) and ej[i + 1][2] else None
            found.setdefault((sid, e), []).append(raw.headword(e, prev, nxt, nxt2, sid))
    redundant = []
    print('per-sense splits (SenseSplits):')
    for key, h in t.sense_splits.items():
        sid, e = key.split('|', 1)
        got = found.get((sid, e), [])
        ok = bool(got) and all(w == h for w, _ in got)
        redundant += [key] if ok else []
        print(f'  {"REDUNDANT " if ok else "          "}{key} → {h}   rule: {sorted(set(got))}')
    print('eojeol splits (WordSplits):')
    for e, h in t.word_splits.items():
        got = [d for (sid, x), ds in found.items() if x == e for d in ds]
        ok = bool(got) and all(w == h for w, _ in got)
        redundant += [e] if ok else []
        print(f'  {"REDUNDANT " if ok else "          "}{e} → {h}   uses {len(got)}, rule: {sorted(set(got))}')
    total = len(t.sense_splits) + len(t.word_splits)
    print(f'reproduced by the context rule: {len(redundant)} of {total}')


# ---- cues -------------------------------------------------------------------------------------------------------------

def cmd_cues(pkg, args):
    rule = Context(pkg)
    counts = Counter()
    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / 'cues_whole.tsv'
    with path.open('w', encoding='utf-8', newline='\n') as f:
        f.write('cue\teojeol\troom\tcontext\n')
        for row in range(pkg.count):
            if not pkg.definitions[row]:
                continue
            text = pkg.display[row]
            ej = eojeols(text)
            sid = pkg.sense_ids[row]
            for i, (s, e, spaces) in enumerate(ej):
                prev = ej[i - 1][1] if i > 0 and ej[i - 1][2] else None
                nxt = ej[i + 1][1] if spaces else None
                nxt2 = ej[i + 2][1] if spaces and i + 2 < len(ej) and ej[i + 1][2] else None
                w, why = rule.headword(e, prev, nxt, nxt2, sid)
                if not why.startswith('K:'):
                    continue
                k = rule.wa_conflict(rule.candidates(e))
                side = 'headword' if w and k and w == k[1] else ('particle' if w else 'plain')
                counts[(why[2:], side)] += 1
                if side == 'headword':
                    f.write(f'{why[2:]}\t{e}\t{room_label(pkg, row)}\t{snippet(text, s, 12, len(e) + 14)}\n')
    print(f'와/과 boundary conflicts decided: {sum(counts.values())}')
    for (cue, side), n in sorted(counts.items()):
        print(f'  {cue:10} {side:9} {n}')
    print(f'whole-headword picks: {path}')


# ---- conflicts ----------------------------------------------------------------------------------------------------------

def cmd_conflicts(pkg, args):
    rule = Context(pkg)
    rows = []
    for w in pkg.words:
        if len(w) >= 2 and w[-1] in '와과':
            x = w[:-1]
            if pkg.is_entry(x) and is_hangul(x[-1]) and has_batchim(x[-1]) == (w[-1] == '과'):
                rows.append((w, sorted(pkg.categories_of.get(w, set())), rule.word_uses(w), rule.stem_uses(x), rule.uses[w]))
    print(f'headwords that read like "noun + 와/과": {len(rows)}')
    print('headword  fields  uses-with-particle  stem-uses  bare-eojeols')
    for w, cats, uw, ux, bare in sorted(rows, key=lambda r: -r[4]):
        print(f'  {w}\t{",".join(c or "-" for c in cats)}\t{uw}\t{ux}\t{bare}')


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('command', choices=['diff', 'cases', 'exceptions', 'cues', 'conflicts'])
    p.add_argument('rule', nargs='?', default='context', choices=sorted(RULES))
    p.add_argument('--goal', default='지구')
    p.add_argument('--limit', type=int, default=15000)
    p.add_argument('--min-length', type=int, default=2)
    args = p.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    pkg = Package(vocabulary_limit=args.limit, min_length=args.min_length)
    {'diff': cmd_diff, 'cases': cmd_cases, 'exceptions': cmd_exceptions, 'cues': cmd_cues,
     'conflicts': cmd_conflicts}[args.command](pkg, args)


if __name__ == '__main__':
    main()
