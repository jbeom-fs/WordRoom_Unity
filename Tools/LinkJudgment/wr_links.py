"""Python port of the shelf link extraction (ShelfLinkText + ShelfLinkGraph) for offline rule experiments.

Never run by Unity. The data tables (endings, exceptions, comparison words, quantity words) are parsed from
ShelfLinkText.cs on every run, so they follow the C# source; the decision logic is ported by hand and must be kept
in step with ShelfLinkText.Headword / FindCandidates and ShelfLinkGraph.CandidateLinks / SetGoal.
Known simplifications: the link setting has no per-room selection (the tests use none), and char.IsLetterOrDigit is
approximated with str.isalnum.
"""
import json
import re
from collections import Counter, deque
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]                       # the Unity project
PACKAGE = ROOT / 'Assets' / 'StreamingAssets' / 'WordRoomData'
LINK_TEXT_CS = ROOT / 'Assets' / 'WordRoom' / 'Scripts' / 'Shelf' / 'ShelfLinkText.cs'
OUT = ROOT / 'DataValidation' / 'links'                          # git-ignored

GENERAL_FIELDS = {'', '없음'}


# ---- Hangul -------------------------------------------------------------------------------------------------------

def is_hangul(c):
    return '가' <= c <= '힣'


def final_consonant(c):
    """Jongseong index of a Hangul syllable (0: none, 8: ㄹ), or None for any other character."""
    return (ord(c) - 0xAC00) % 28 if is_hangul(c) else None


def has_batchim(c):
    j = final_consonant(c)
    return j is not None and j != 0


def _is_han(c):
    o = ord(c)
    return 0x4E00 <= o <= 0x9FFF or 0x3400 <= o <= 0x4DBF or 0xF900 <= o <= 0xFAFF or o >= 0x20000


# ---- C# tables ------------------------------------------------------------------------------------------------------

class CsTables:
    """The data tables of ShelfLinkText.cs (comments stripped before reading the string literals)."""

    def __init__(self, path=LINK_TEXT_CS):
        src = re.sub(r'//[^\n]*', '', Path(path).read_text(encoding='utf-8'))

        def body(name):
            m = re.search(r'\b' + name + r'\s*=[^{;]*\{(.*?)\}\s*;', src, re.S)
            if not m:
                raise ValueError(f'{name} not found in {path}')
            return m.group(1)

        def strings(name):
            return re.findall(r'"([^"]*)"', body(name))

        def pairs(name):
            return dict(re.findall(r'\{\s*"([^"]*)",\s*"([^"]*)"\s*\}', body(name)))

        self.particle_endings = strings('ParticleEndings')
        self.copula_endings = strings('CopulaEndings')
        self.endings = sorted(set(self.particle_endings) | set(self.copula_endings), key=lambda a: (-len(a), a))
        self.sense_splits = pairs('SenseSplits')
        self.word_splits = pairs('WordSplits')
        self.not_noun = set(strings('NotNounForms'))
        self.comparison_words = set(strings('ComparisonWords'))
        self.counter_forms = pairs('CounterForms')
        self.quantity_words = set(strings('QuantityWords'))
        self.units = strings('Units')
        self.tens = strings('Tens')

    def is_quantity(self, e):
        if not e:
            return False
        if e in self.quantity_words:
            return True
        digits = len(e) - 1 if e.endswith('여') else len(e)
        if digits > 0 and all('0' <= c <= '9' for c in e[:digits]):
            return True
        for t in self.tens:
            if e.startswith(t) and (len(e) == len(t) or e[len(t):] in self.units):
                return True
        return False


# ---- text -----------------------------------------------------------------------------------------------------------

def display_definition(raw):
    """ShelfLinkText.DisplayDefinition: parenthesized spans with a Han character are removed with their brackets."""
    if not raw or ('(' not in raw and '（' not in raw):
        return raw
    out, i = [], 0
    while i < len(raw):
        if raw[i] in '(（':
            depth, close = 0, -1
            for j in range(i, len(raw)):
                if raw[j] in '(（':
                    depth += 1
                elif raw[j] in ')）':
                    depth -= 1
                    if depth == 0:
                        close = j
                        break
            if close > i and any(_is_han(c) for c in raw[i + 1:close]):
                i = close + 1
                if (not out or out[-1].isspace()) and i < len(raw) and raw[i].isspace():
                    i += 1
                continue
        out.append(raw[i])
        i += 1
    return ''.join(out)


def eojeols(text):
    """Maximal runs of letters and digits: (start, text, only spaces up to the next eojeol)."""
    spans, i = [], 0
    while i < len(text):
        if not text[i].isalnum():
            i += 1
            continue
        s = i
        while i < len(text) and text[i].isalnum():
            i += 1
        spans.append((s, i))
    result = []
    for k, (s, e) in enumerate(spans):
        spaces = k + 1 < len(spans) and all(text[j].isspace() for j in range(e, spans[k + 1][0]))
        result.append((s, text[s:e], spaces))
    return result


def bracket(text, links):
    """`text` with each (start, headword) link shown as [headword]."""
    out, cur = [], 0
    for s, w in sorted(links):
        out.append(text[cur:s])
        out.append('[' + text[s:s + len(w)] + ']')
        cur = s + len(w)
    out.append(text[cur:])
    return ''.join(out)


# ---- package ----------------------------------------------------------------------------------------------------------

class Package:
    """The dictionary package and the link candidate conditions of ShelfLinkGraph."""

    def __init__(self, folder=PACKAGE, vocabulary_limit=15000, min_length=2):
        folder = Path(folder)
        s = json.loads((folder / 'senses.json').read_text(encoding='utf-8'))
        w = json.loads((folder / 'words.json').read_text(encoding='utf-8'))
        r = json.loads((folder / 'restricted.json').read_text(encoding='utf-8'))
        self.package_id = s['packageId']
        self.words = w['words']
        self.word_set = set(self.words)
        self.rank = {x: i for i, x in enumerate(self.words)}
        self.restricted = {e['rank'] for e in r['entries']}
        self.count = s['count']
        self.sense_ids = s['senseIds']
        self.word_ranks = s['wordRanks']
        self.categories = s['categories']
        self.definitions = s['definitions']
        self.raws = s['raws']
        self.vocabulary_limit = vocabulary_limit
        self.min_length = min_length
        self.row_of = {sid: i for i, sid in enumerate(self.sense_ids)}
        self.rows_of_rank = {}
        self.categories_of = {}                                      # surface -> set of sense categories
        for i in range(self.count):
            self.rows_of_rank.setdefault(self.word_ranks[i], []).append(i)
            self.categories_of.setdefault(self.words[self.word_ranks[i]], set()).add(self.categories[i])
        self.has_definition = {self.word_ranks[i] for i in range(self.count) if self.definitions[i]}
        self.display = [display_definition(d) if d else d for d in self.definitions]

    def word_of(self, row):
        return self.words[self.word_ranks[row]]

    def is_entry(self, surface):
        return surface in self.word_set

    def is_door(self, surface):
        r = self.rank.get(surface)
        return (r is not None and r < self.vocabulary_limit and len(surface) >= self.min_length
                and r not in self.restricted and r in self.has_definition)

    def fields(self, surface):
        """Specialized fields of a surface's senses (empty and 없음 left out)."""
        return self.categories_of.get(surface, set()) - GENERAL_FIELDS

    def only_specialized(self, surface):
        cats = self.categories_of.get(surface)
        return bool(cats) and not (cats & GENERAL_FIELDS)


# ---- extraction and graph ---------------------------------------------------------------------------------------------

class Extraction:
    """One rule applied to one sense: every eojeol's decision, the candidates before and after the room filters."""

    def __init__(self, pkg, rule, row):
        self.row = row
        self.text = pkg.display[row]
        sid = pkg.sense_ids[row]
        word = pkg.word_of(row)
        self.decisions = []                                          # (start, eojeol, headword or None, reason)
        ej = eojeols(self.text)
        for k, (s, t, spaces) in enumerate(ej):
            prev = ej[k - 1][1] if k > 0 and ej[k - 1][2] else None
            nxt = ej[k + 1][1] if spaces else None
            nxt2 = ej[k + 2][1] if spaces and k + 2 < len(ej) and ej[k + 1][2] else None
            w, why = rule.headword(t, prev, nxt, nxt2, sid)
            self.decisions.append((s, t, w, why))
        self.candidates = [(s, w) for (s, t, w, why) in self.decisions if w and pkg.is_door(w)]
        self.links = []                                              # ShelfLinkGraph.CandidateLinks
        seen = set()
        for s, w in self.candidates:
            if w == word or w in seen:
                continue
            seen.add(w)
            self.links.append((s, w))


class Graph:
    """ShelfLinkGraph for one rule and goal: rooms, valid rooms and the links each sense shows."""

    def __init__(self, pkg, rule, goal='지구'):
        self.pkg = pkg
        self.rule = rule
        self.goal = goal
        self.extractions = {i: Extraction(pkg, rule, i) for i in range(pkg.count) if pkg.definitions[i]}
        self.exits = {}
        for r, w in enumerate(pkg.words):
            if not pkg.is_door(w):
                continue
            targets = set()
            for i in pkg.rows_of_rank.get(r, []):
                if i in self.extractions:
                    targets.update(x for _, x in self.extractions[i].links)
            self.exits[w] = targets
        self.valid = {w: True for w in self.exits}
        left = {w: len(t) for w, t in self.exits.items()}
        linked = {}
        for w, t in self.exits.items():
            for x in t:
                linked.setdefault(x, []).append(w)
        queue = deque()
        for w in self.exits:
            if left[w] <= 0 and w != goal:
                self.valid[w] = False
                queue.append(w)
        while queue:
            x = queue.popleft()
            for room in linked.get(x, []):
                if self.valid[room]:
                    left[room] -= 1
                    if left[room] <= 0 and room != goal:
                        self.valid[room] = False
                        queue.append(room)

    def is_room_sense(self, row):
        return self.pkg.word_of(row) in self.exits

    def shown(self, row):
        """The links the reading screen shows for a sense (ShelfLinkGraph.Doors), [] for a sense without a room."""
        if not self.is_room_sense(row) or row not in self.extractions:
            return []
        return [(s, w) for s, w in self.extractions[row].links if self.valid.get(w)]

    def shown_count(self):
        return sum(len(self.shown(i)) for i in self.extractions)
