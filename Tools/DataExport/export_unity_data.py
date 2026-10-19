"""WordRoom → Unity data package exporter (explicit, offline; never run by Unity).

  python export_unity_data.py export  --source D:\\Pro_KW\\WordRoom    prepare bundle, then replace it as one set
  python export_unity_data.py verify  --source D:\\Pro_KW\\WordRoom    re-check the active package against the source
  python export_unity_data.py fixture --source D:\\Pro_KW\\WordRoom    rebuild the synthetic test fixture

Run it with the WordRoom Python environment (numpy), e.g. D:\\Pro_KW\\WordRoom\\semantic_runtime\\Scripts\\python.exe -B.
Outputs (relative to the Unity project). The first three form one bundle that is replaced together:
  Assets/StreamingAssets/WordRoomData/          active package
  DataValidation/real/python_reference.json     Python baseline for the Unity comparison (same packageId)
  DataValidation/real/export_report.json        counts, exclusions, settings differences, timings (same packageId)
  DataPackages/previous/<time>_<old id>/        replaced bundles (bundle.json marks them); the newest --keep-previous are kept
  DataPackages/swap/<real|fixture>.journal.json exists only while a replacement is unfinished
  DataPackages/staging/, DataPackages/failed/   work folders of running and failed exports
  Assets/WordRoom/Tests/Fixtures~/Synthetic/    fixture package, baseline and report (fixture command, same procedure)

Exit codes: 0 success (warnings possible), 1 preparation failed (nothing changed), 2 replacement failed and the
previous bundle was restored, 3 manual recovery needed (see the printed paths)."""
import argparse
import json
import shutil
import sys
import tempfile
import time
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import wr_format as F  # noqa: E402
from wr_export import build_package, verify_package  # noqa: E402
from wr_reference import build_reference  # noqa: E402
from wr_source import ExportError, load_python, real_source, snapshot_database  # noqa: E402
from wr_swap import (SwapError, SwapFailed, Target, UnresolvedSwap, cleanup, finalize_commit, free_path,  # noqa: E402
                     plan_backup, read_id, recover_pending, swap)

EXIT_PREPARE_FAILED, EXIT_RESTORED, EXIT_MANUAL = 1, 2, 3


class PrepareFailed(Exception):
    pass


REAL_SCOPES = [('full', 15000, 1, 8), ('top10000', 10000, 1, 8), ('top10000_min2', 10000, 2, 8), ('full_min2', 15000, 2, 8),
               ('top5000_len1to3', 5000, 1, 3), ('over_prepared', 15001, 1, 8), ('max9', 15000, 1, 9), ('min0', 15000, 0, 8)]


def stamp():
    return time.strftime('%Y%m%d_%H%M%S')


def progress(text):
    print(time.strftime('%H:%M:%S'), text, flush=True)


def targets(unity):
    """Live bundle locations (unchanged from earlier versions) and the tool's bookkeeping folders."""
    out = unity / 'Assets/WordRoom/Tests/Fixtures~/Synthetic'
    return dict(
        real=Target('real', unity / 'Assets/StreamingAssets' / F.PACKAGE_DIR, unity / 'DataValidation/real/python_reference.json',
                    unity / 'DataValidation/real/export_report.json', unity / 'DataPackages/previous',
                    unity / 'DataPackages/swap/real.journal.json'),
        fixture=Target('fixture', out / F.PACKAGE_DIR, out / 'python_reference.json', out / 'export_report.json',
                       unity / 'DataPackages/fixture_previous', unity / 'DataPackages/swap/fixture.journal.json'))


def set_aside(work, failed_dir):
    """Keep a failed work folder for inspection; returns where it is now."""
    if not work.exists():
        return None
    try:
        failed_dir.mkdir(parents=True, exist_ok=True)
        dest = free_path(failed_dir / work.name)
        shutil.move(str(work), str(dest))
        return dest
    except Exception as e:
        progress(f'작업 폴더를 failed로 옮기지 못해 그대로 둡니다({e}): {work}')
        return work


def make_report(src, target, built, verified, ref, timings, staging, backup_dir, portable):
    m = built['manifest']
    sd = built['settings_doc']
    report = dict(packageId=built['package_id'], createdAt=m['createdAt'], sourceKind=src.kind,
                  counts=m['counts'], vocabularyStats=built['vocabulary']['stats'], relations=built['relation_meta']['counts'],
                  relationMismatchExamples=built['relation_meta']['mismatch_examples'],
                  unknownPayloadKeys=built['relation_meta']['unknown_payload_keys'],
                  files=[dict(name=f['name'], bytes=f['bytes'], sha256=f['sha256'], records=f['records']) for f in m['files']],
                  totalBytes=sum(f['bytes'] for f in m['files']) + (staging / F.MANIFEST).stat().st_size,
                  settings=dict(changed=[e for e in sd['entries'] if e['change'] == 'changed'],
                                defaulted=[e['key'] for e in sd['entries'] if e['change'] == 'default'],
                                dropped=sd['droppedRawKeys'], notices=sd['notices'], migrationSteps=sd['migrationSteps'],
                                guiRoundTripNotes=sd['guiRoundTripNotes']),
                  thresholds=[x['text'] for x in sd['thresholdLadder']], verification=verified,
                  timingsSeconds=dict(timings, note='준비 단계 시간(교체·정리 시간 제외)'),
                  bundle=dict(note='패키지·python_reference.json·export_report.json은 같은 packageId의 한 묶음으로 교체됩니다.'),
                  referenceSummary=dict(wordStatePairs=len(ref['wordStates']['a']), evidence=len(ref['evidence']['word']),
                                        rejectedPairs=len(ref['rejected']['a']),
                                        categories={c['name']: c['count'] for c in ref['categories']},
                                        approvedWordPairs=[g['approvedWordPairs'] for g in ref['neighbors']]))
    if not portable:
        report.update(source=str(src.root), vocabulary=str(src.vocab_dir), activePackage=str(target.package),
                      previousBundle=str(backup_dir) if backup_dir else None)
    return report


def export(py, src, unity, scopes, target, samples=None, keep=2, portable=False):
    """Prepare → back up → replace → confirm → clean up. Raises PrepareFailed (nothing changed), SwapFailed
    (replacement failed; recovered or not) or UnresolvedSwap (an earlier replacement could not be settled)."""
    recover_pending(target, progress)            # an interrupted earlier replacement is settled first
    failed_dir = unity / 'DataPackages/failed'
    timings = {}
    t = time.monotonic()
    work = free_path(unity / 'DataPackages' / 'staging' / f'{stamp()}_{target.label}')
    staging = work / F.PACKAGE_DIR
    snapshot = work / 'relations_snapshot.sqlite'
    # 1. Preparation: the new package, its Python baseline and its report are completed in the work folder only.
    try:
        work.mkdir(parents=True, exist_ok=False)
        progress(f'관계 DB 스냅샷: {src.relations_db}')
        t0 = time.monotonic()
        snap_info = snapshot_database(src.relations_db, snapshot)
        timings['snapshot'] = time.monotonic() - t0
        t0 = time.monotonic()
        built = build_package(py, src, snapshot, snap_info, staging, progress)
        timings['build'] = time.monotonic() - t0
        t0 = time.monotonic()
        verified = verify_package(py, src, snapshot, staging, progress)
        timings['verify'] = time.monotonic() - t0
        t0 = time.monotonic()
        ref = build_reference(py, src, snapshot, built['package_id'], built['cfg'], built['raw_settings'], scopes, samples=samples,
                              progress=progress)
        (work / 'python_reference.json').write_bytes(F.json_bytes(ref))
        timings['reference'] = time.monotonic() - t0
        timings['prepare_total'] = time.monotonic() - t
        backup_dir = plan_backup(target)
        report = make_report(src, target, built, verified, ref, timings, staging, backup_dir, portable)
        (work / 'export_report.json').write_bytes(F.json_bytes(report, compact=False))
        snapshot.unlink(missing_ok=True)
        # Release the memory-mapped source vectors now: an exception traceback would otherwise keep them open and
        # (fixture) block the removal of the temporary source folder.
        for key in ('vectors', 'ft'):
            built['vocabulary'].pop(key, None)
    except BaseException as exc:
        try:
            snapshot.unlink(missing_ok=True)
        except OSError:
            pass
        kept = set_aside(work, failed_dir)
        raise PrepareFailed(f'{type(exc).__name__}: {exc}' + (f' · 작업 폴더: {kept}' if kept else '')) from exc
    # 2-3. Backup and replacement of the whole bundle, confirmed by packageId; rolled back on any failure.
    staged = dict(package=staging, reference=work / 'python_reference.json', report=work / 'export_report.json')
    try:
        backup, journal = swap(target, staged, built['package_id'], work, backup_dir, progress)
    except SwapError as exc:
        kept = set_aside(work, failed_dir)
        raise PrepareFailed(f'교체를 시작하지 않았습니다: {exc}' + (f' · 작업 폴더: {kept}' if kept else '')) from exc
    except SwapFailed as exc:
        if exc.recovered:
            exc.work_dir = set_aside(work, failed_dir)   # the new bundle went back to the work folder
        raise
    # 4. Confirmed: the new bundle is active. Everything after this point is housekeeping and only warns.
    warnings = []
    if journal.get('commitRecordError'):
        warnings.append(f"교체 확정 기록 저장 실패({journal['commitRecordError']}): 기록이 남으면 다음 실행이 이 교체를 되돌릴 수 있습니다.")
    warnings += finalize_commit(target, journal)
    # 5. Cleanup of superseded backups beyond --keep-previous and of the finished work folder.
    warnings += cleanup(target, keep, extra=[] if target.journal.exists() else [work])
    m = built['manifest']
    progress(f"새 묶음 활성화 완료(패키지 {built['package_id'][:16]}…): 표기 {m['counts']['words']:,} · 뜻 {m['counts']['senses']:,} · "
             f"관계 {m['counts']['relationRecords']:,} · {report['totalBytes'] / 1e6:.1f} MB · {time.monotonic() - t:.1f}초"
             + (f' · 이전 묶음 보관: {backup}' if backup else ' · 이전 묶음 없음(최초 내보내기)'))
    for w in warnings:
        progress(f'경고(내보내기는 성공, 정리 단계): {w}')
    return report, warnings


MANUAL_STEPS = (
    '수동 복구 방법:\n'
    '  1) 교체 기록 파일의 items마다 live(활성 위치)·backup(이전 버전)·staged(새 버전 작업 위치)와 oldId/newId를 확인합니다.\n'
    '  2) 원인(예: Unity 에디터나 탐색기가 파일을 잡고 있음)을 없앤 뒤 같은 export/fixture 명령을 다시 실행하면\n'
    '     기록을 읽어 자동 복구를 먼저 다시 시도합니다(새 교체는 복구가 끝난 뒤에만 진행).\n'
    '  3) 직접 되돌릴 때는 각 항목의 이전 버전(oldId)을 live 위치로, 새 버전(newId)을 workDir로 옮긴 뒤 기록 파일을 지웁니다.\n'
    '  어떤 위치의 파일도 삭제하지 마세요. 이전 버전·새 버전·백업이 모두 보존돼 있습니다.')


def print_paths(journal_path):
    try:
        j = json.loads(Path(journal_path).read_text(encoding='utf-8'))
    except Exception:
        progress(f'  교체 기록: {journal_path} (읽을 수 없음)')
        return
    progress(f"  교체 기록: {journal_path} (상태 {j.get('state')}, 새 packageId {j.get('newId', '')[:16]})")
    progress(f"  백업 폴더: {j.get('backupDir')} · 작업 폴더: {j.get('workDir')}")
    for it in j.get('items', []):
        progress(f"  {it['name']}: live={it['live']} backup={it.get('backup')} staged={it['staged']} "
                 f"이전 packageId={(it.get('oldId') or '없음')[:16]} 단계={it.get('step')}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('command', choices=['export', 'verify', 'fixture'])
    ap.add_argument('--source', required=True, help='WordRoom Python 프로젝트 폴더 (예: D:\\Pro_KW\\WordRoom)')
    ap.add_argument('--unity', default=str(HERE.parents[1]), help='Unity 프로젝트 폴더 (기본: 이 도구가 있는 프로젝트)')
    ap.add_argument('--keep-previous', type=int, default=2, help='보관할 이전 묶음(패키지+검증 기준+보고서) 수. 교체 성공 뒤에만 정리 (fixture는 최소 1)')
    args = ap.parse_args()
    unity = Path(args.unity).resolve()
    if not (unity / 'ProjectSettings/ProjectVersion.txt').exists():
        raise SystemExit(f'Unity 프로젝트가 아닙니다: {unity}')
    py = load_python(args.source)
    tg = targets(unity)
    target = tg['fixture' if args.command == 'fixture' else 'real']
    try:
        if args.command == 'export':
            src = real_source(py)
            export(py, src, unity, REAL_SCOPES, target, keep=args.keep_previous)
        elif args.command == 'verify':
            if target.journal.exists():
                progress(f'미완료 교체 기록이 있어 활성 묶음이 확정되지 않았습니다. export를 다시 실행하면 먼저 복구합니다: {target.journal}')
                sys.exit(EXIT_MANUAL)
            src = real_source(py)
            with tempfile.TemporaryDirectory() as tmp:
                snapshot = Path(tmp) / 'relations_snapshot.sqlite'
                snapshot_database(src.relations_db, snapshot)
                verify_package(py, src, snapshot, target.package, progress)
            ids = {name: read_id(path, is_dir) for name, path, is_dir in target.items()}
            if len(set(ids.values())) != 1:
                progress(f'경고: 활성 패키지와 검증 기준·보고서의 packageId가 다릅니다: {ids}')
                sys.exit(EXIT_MANUAL)
        else:
            from wr_fixture import SCOPES, build_fixture_source
            # The fixture source lives in a temporary folder; everything the swap needs (work folder, backups,
            # journal) is under DataPackages, so nothing a recovery may need is removed with the temp folder.
            # ignore_cleanup_errors: a file still open in the temporary source folder must not replace the export's
            # own outcome (success, PrepareFailed, SwapFailed) with a cleanup PermissionError.
            with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
                src = build_fixture_source(py, Path(tmp) / 'source')
                export(py, src, unity, SCOPES, target, samples=dict(vector_rows=100, reversed=50, untouched=40, ft_targets=11, e5_pairs=200),
                       keep=max(1, args.keep_previous), portable=True)
    except PrepareFailed as exc:
        progress(f'준비 실패 — 기존 활성 패키지·검증 기준·보고서는 변경하지 않았습니다. 원인: {exc}')
        sys.exit(EXIT_PREPARE_FAILED)
    except SwapFailed as exc:
        if exc.recovered:
            progress(f'교체 실패 → 기존 묶음으로 복구 완료. 활성 패키지·검증 기준·보고서는 이전 버전입니다. 원인: {exc}')
            progress(f'  새 묶음 작업 폴더: {exc.work_dir}')
            sys.exit(EXIT_RESTORED)
        progress(f'교체 실패, 복구 실패 — 수동 복구가 필요합니다. 원인: {exc}')
        for p in exc.problems:
            progress(f'  복구 문제: {p}')
        print_paths(exc.journal_path)
        progress(MANUAL_STEPS)
        sys.exit(EXIT_MANUAL)
    except UnresolvedSwap as exc:
        progress(f'중단 — {exc} 새 내보내기를 진행하지 않았습니다.')
        for p in exc.problems:
            progress(f'  복구 문제: {p}')
        print_paths(exc.journal_path)
        progress(MANUAL_STEPS)
        sys.exit(EXIT_MANUAL)
    except ExportError as exc:
        progress(f'오류: {exc}')
        sys.exit(EXIT_PREPARE_FAILED)
    except Exception:
        traceback.print_exc()
        sys.exit(EXIT_PREPARE_FAILED)


if __name__ == '__main__':
    main()
