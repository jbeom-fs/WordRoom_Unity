"""Build a Unity-readable link sidecar for the installed WordRoom dictionary.

Dependency: python -m pip install kiwipiepy==0.23.2 kiwipiepy_model==0.23.0
Run from the project root: python Tools/LinkJudgment/export_link_db.py
"""

import argparse
import hashlib
import json
import re
import tempfile
from functools import lru_cache
from importlib.metadata import version
from pathlib import Path

from kiwipiepy import Kiwi

from wr_links import PACKAGE, display_definition, eojeols


# The ambiguity threshold is a model score difference, not a probability.
SCORE_WINDOW = 2.5
ATTRIBUTIVE_SCORE_WINDOW = 3.0
NOUN_TAGS = {"NNG", "NNP"}
HANGUL = re.compile(r"[가-힣]+\Z")
DICT_REFERENCE = re.compile(
    r"‘(?P<word>[가-힣]+)(?:(?P<entry>[0-9]{2})(?:「[0-9]+」)?|「[0-9]+」)’"
)
HOMONYM_NUMBER = re.compile(r"[0-9]{2}\Z")
MEASUREMENT = re.compile(r"(?:길이|높이|너비|넓이|깊이|폭|지름|두께|무게|크기)(?:는|은|가|이)?\Z")
SEQUENTIAL_ENDINGS = {"아서", "어서", "여서"}


def read_package(folder):
    manifest = json.loads((folder / "manifest.json").read_bytes())
    package_id = manifest["packageId"]
    expected = {item["name"]: item["sha256"] for item in manifest["files"]}
    documents = []
    for name in ("words.json", "senses.json", "restricted.json"):
        data = (folder / name).read_bytes()
        if hashlib.sha256(data).hexdigest() != expected[name]:
            raise ValueError(f"{name} differs from the manifest")
        document = json.loads(data)
        if document["packageId"] != package_id:
            raise ValueError(f"{name} has a different packageId")
        documents.append(document)
    return package_id, *documents


def noun_prefix(tokens, text, word_start, word_end, targets):
    """One linked noun at the start of an eojeol, using the model's lexical span."""
    first = tokens[0]
    if first.start != word_start or first.tag not in NOUN_TAGS:
        return None
    end = first.end
    if end > word_end or first.form != text[word_start:end]:
        return None
    noun_end = end if first.form in targets else None
    # Rejoin contiguous noun + noun-forming suffix when the resulting noun is
    # itself registered (포유+류). A plural suffix cannot form a new target.
    for token in tokens[1:]:
        if token.start != end or token.end > word_end or token.tag != "XSN" or token.form == "들":
            break
        if token.form != text[token.start:token.end]:
            break
        end = token.end
        if text[word_start:end] in targets:
            noun_end = end
    # An independent noun or verb still inside the same written word signals
    # a compound or a doubtful boundary. Do not link fragments such as 문 in 문정맥.
    for token in tokens[1:]:
        if token.start < end:
            continue
        if token.tag.startswith(("J", "E", "X")) or token.tag in {"VCP", "VCN"}:
            continue
        if token.tag.startswith("VV") and token.form in {"하", "되", "시키"}:
            continue
        return None
    return noun_end


def candidate_spans(tokens, text, words, targets):
    cursor = 0
    spans = set()
    for start, word, _ in words:
        end = start + len(word)
        while cursor < len(tokens) and tokens[cursor].end <= start:
            cursor += 1
        if cursor == len(tokens) or tokens[cursor].start != start:
            continue
        stop = cursor
        while stop < len(tokens) and tokens[stop].start < end:
            stop += 1
        noun_end = noun_prefix(tokens[cursor:stop], text, start, end, targets)
        if noun_end is not None:
            spans.add((start, noun_end))
    return spans


def attributive_check(kiwi):
    """Look for a competing single-syllable verb/adjective + attributive ending."""

    @lru_cache(maxsize=4096)
    def check(context, start, noun_start, measured, previous_end):
        analyses = kiwi.analyze(context, top_n=5)
        if not analyses:
            return False
        best_score = analyses[0][1]
        for tokens, score in analyses:
            if best_score - score > ATTRIBUTIVE_SCORE_WINDOW:
                break
            for first, ending, noun in zip(tokens, tokens[1:], tokens[2:]):
                if (
                    first.start == start
                    and first.tag.startswith(("VV", "VA"))
                    and ending.tag == "ETM"
                    and ending.end == start + 1
                    and noun.start == noun_start
                    and noun.tag in NOUN_TAGS
                ):
                    if measured or any(
                        connector.end == previous_end
                        and connector.tag == "EC"
                        and connector.form in SEQUENTIAL_ENDINGS
                        and verb.tag.startswith(("VV", "XSV"))
                        for verb, connector in zip(tokens, tokens[1:])
                    ):
                        return True
        return False

    return check


def is_uncertain_attributive(text, words, word_index, check):
    start, word, next_is_adjacent = words[word_index]
    if not next_is_adjacent or not HANGUL.fullmatch(word) or (ord(word) - ord("가")) % 28 not in (4, 8):
        return False
    # Keep preceding words inside the clause so e.g. 우리 편 retains its nominal context.
    left = word_index
    while left > max(0, word_index - 2) and words[left - 1][2]:
        left -= 1
    right = word_index + 1
    while right < min(len(words) - 1, word_index + 3) and words[right][2]:
        right += 1
    context_start = words[left][0]
    context_end = words[right][0] + len(words[right][1])
    noun_start, noun, adjacent = words[word_index + 1]
    measured = adjacent and noun.endswith("의") and MEASUREMENT.fullmatch(words[word_index + 2][1]) is not None
    previous_end = -1
    if left < word_index:
        previous_start, previous_word, _ = words[word_index - 1]
        previous_end = previous_start + len(previous_word) - context_start
    if not measured and previous_end < 0:
        return False
    return check(text[context_start:context_end], start - context_start, noun_start - context_start, measured, previous_end)


def extract(text, analyses, targets, homonyms, source, check_attributive):
    words = eojeols(text)
    spans = set()
    if analyses:
        tokens, best_score = analyses[0]
        spans = candidate_spans(tokens, text, words, targets)
        for tokens, score in analyses[1:]:
            if best_score - score <= SCORE_WINDOW:
                spans.intersection_update(candidate_spans(tokens, text, words, targets))

    references = list(DICT_REFERENCE.finditer(text))
    explicit_spans = {
        match.span("word")
        for match in references
        if match["word"] in targets
        and match["word"] != source
        and (match["entry"] is None or (match["word"], match["entry"]) in homonyms)
    }
    explicit_targets = {text[start:end] for start, end in explicit_spans}
    for index, (start, word, _) in enumerate(words):
        if len(word) == 1 and (start, start + 1) in spans and is_uncertain_attributive(text, words, index, check_attributive):
            spans.remove((start, start + 1))
    # An explicit citation takes precedence over a model link, including a
    # repetition elsewhere in the same sense (the reader keeps the first link).
    spans = {
        (start, end)
        for start, end in spans
        if text[start:end] not in explicit_targets
        and all(end <= match.start() or match.end() <= start for match in references)
    }
    spans.update(explicit_spans)
    offsets = [0]
    for character in text:
        offsets.append(offsets[-1] + (2 if ord(character) > 0xFFFF else 1))
    links = []
    for start, end in sorted(spans):
        noun = text[start:end]
        if noun != source:
            links.append({"start": offsets[start], "length": offsets[end] - offsets[start], "text": noun, "target": noun})
    return links


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, default=PACKAGE)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or args.package / "links.json"
    package_id, words, senses, restricted = read_package(args.package)
    vocabulary = words["words"]
    defined_ranks = {rank for rank, definition in zip(senses["wordRanks"], senses["definitions"], strict=True) if definition}
    restricted_ranks = {entry["rank"] for entry in restricted["entries"]}
    targets = {vocabulary[rank] for rank in defined_ranks - restricted_ranks if HANGUL.fullmatch(vocabulary[rank])}
    homonyms = {
        (vocabulary[rank], number.group())
        for rank, raw in zip(senses["wordRanks"], senses["raws"], strict=True)
        if raw and (number := HOMONYM_NUMBER.search(raw))
    }
    definitions = [display_definition(raw) if raw else "" for raw in senses["definitions"]]
    kiwi = Kiwi(num_workers=4)
    check_attributive = attributive_check(kiwi)
    analyses_by_definition = kiwi.analyze(definitions, top_n=3)
    entries = []
    for sense_id, rank, text, analyses in zip(senses["senseIds"], senses["wordRanks"], definitions, analyses_by_definition, strict=True):
        links = extract(text, analyses, targets, homonyms, vocabulary[rank], check_attributive)
        entries.append({"senseId": sense_id, "links": links})
    data = {
        "format": "WordRoomLinkDatabase",
        "schemaVersion": 1,
        "packageId": package_id,
        "offsetUnit": "utf16",
        "textBasis": "ShelfLinkText.DisplayDefinition",
        "generator": {"analyzer": "kiwipiepy", "version": version("kiwipiepy"), "modelVersion": version("kiwipiepy_model")},
        "senses": entries,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=output.parent, suffix=".tmp", delete=False) as temporary:
        temporary_path = Path(temporary.name)
    try:
        with temporary_path.open("w", encoding="utf-8", newline="\n") as stream:
            stream.write("{\n")
            for key, value in data.items():
                if key != "senses":
                    stream.write(f"  {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)},\n")
            stream.write('  "senses": [\n')
            for index, entry in enumerate(entries):
                comma = "," if index + 1 < len(entries) else ""
                stream.write(f"    {json.dumps(entry, ensure_ascii=False, separators=(',', ':'))}{comma}\n")
            stream.write("  ]\n}\n")
        temporary_path.replace(output)
    finally:
        temporary_path.unlink(missing_ok=True)
    print(f"{output}: {len(entries):,} senses, {sum(len(entry['links']) for entry in entries):,} links")


if __name__ == "__main__":
    main()
