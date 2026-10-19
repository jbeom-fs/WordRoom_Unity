"""Replacing the active bundle -- data package, Python baseline (python_reference.json) and export report -- as one
versioned set, with a journal so an interrupted replacement can be identified and rolled back by the next run.

The bundle is three separate filesystem items, so replacing it is a sequence of renames, not one atomic operation.
Every step is a rename on the same drive (no copy, no delete), each item's old version is moved into one backup
folder before the new one is moved in, and the journal records where every version is. Rollback is decided from
what is actually at each location (backup present → the item at the live location is the new one), checked against
the packageId each item carries. Nothing is deleted during a replacement or a rollback; when a rollback cannot finish,
every version stays where it is and the journal names the locations.

Order: plan_backup → swap (backup + install per item → verify packageId → commit) → cleanup (old superseded backups).
`recover_pending` runs before a new export: a committed journal is finalized, any other journal is rolled back.

Fault injection for tests on a separate copy only: WR_SWAP_FAULT=point[,point...] with points
  backup:<item>  install:<item>  verify  rollback:<item>  cleanup  -- raise OSError at that step
  crash:after-backup:<item>  crash:after-install:<item>            -- end the process immediately (exit code 97)
where <item> is package, reference or report. Unset (the normal case), it does nothing."""
import json
import os
import shutil
import time
from dataclasses import dataclass
from pathlib import Path

JOURNAL_SCHEMA = 1
BUNDLE_FILE = 'bundle.json'          # marker inside a backup folder: what it holds and whether it is superseded
FAULT_ENV = 'WR_SWAP_FAULT'


class SwapError(Exception):
    """Replacement refused or failed before anything could be rolled back (no change made)."""


class RecoveryError(Exception):
    pass


class SwapFailed(Exception):
    """Replacement failed. recovered=True: every item is back to its previous state."""

    def __init__(self, cause, recovered, journal_path, backup_dir, work_dir, problems=()):
        super().__init__(str(cause))
        self.cause, self.recovered, self.journal_path = cause, recovered, journal_path
        self.backup_dir, self.work_dir, self.problems = backup_dir, work_dir, list(problems)


class UnresolvedSwap(Exception):
    """An earlier replacement was interrupted and its state could not be made consistent; nothing new may start."""

    def __init__(self, message, journal_path, problems=()):
        super().__init__(message)
        self.journal_path, self.problems = journal_path, list(problems)


def fault(point):
    spec = os.environ.get(FAULT_ENV, '')
    if not spec or point not in [p.strip() for p in spec.split(',')]:
        return
    if point.startswith('crash:'):
        print(f'[{FAULT_ENV}] 시험용 강제 종료: {point}', flush=True)
        os._exit(97)
    raise OSError(f'[{FAULT_ENV}] 시험용 오류 주입: {point}')


@dataclass(frozen=True)
class Target:
    """Live locations of one bundle and the tool's own bookkeeping folders (all on the same drive)."""
    label: str
    package: Path            # e.g. Assets/StreamingAssets/WordRoomData
    reference: Path          # e.g. DataValidation/real/python_reference.json
    report: Path             # e.g. DataValidation/real/export_report.json
    previous: Path           # backup bundles
    journal: Path            # in-progress replacement record

    def items(self):
        return [('package', self.package, True), ('reference', self.reference, False), ('report', self.report, False)]


def read_id(path, is_dir):
    """packageId carried by a bundle item; None when absent, '?' when present but unreadable."""
    path = Path(path)
    if not path.exists():
        return None
    try:
        data = json.loads((path / 'manifest.json' if is_dir else path).read_text(encoding='utf-8'))
        return data.get('packageId') or '?'
    except Exception:
        return '?'


def write_json_atomic(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + '.tmp')
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def move(src, dst, attempts=10):
    """Rename that never overwrites: refuses an existing destination, retries while a file is briefly locked."""
    src, dst = Path(src), Path(dst)
    if dst.exists():
        raise OSError(f'이동 대상이 이미 있어 덮어쓰지 않습니다: {dst}')
    for k in range(attempts):
        try:
            os.replace(src, dst)
            return
        except PermissionError:
            if k == attempts - 1:
                raise
            time.sleep(0.5)


def free_path(path):
    """`path`, or path.1, path.2, ... -- the first name that does not exist yet."""
    path = Path(path)
    candidate, n = path, 0
    while candidate.exists():
        n += 1
        candidate = path.with_name(f'{path.name}.{n}')
    return candidate


def plan_backup(target):
    """Backup folder the next swap will create (None when there is nothing to back up). Never an existing folder."""
    if not any(live.exists() for _, live, _ in target.items()):
        return None
    old = read_id(target.package, True)
    name = f"{time.strftime('%Y%m%d_%H%M%S')}_{(old or 'none')[:12] if old != '?' else 'unreadable'}"
    return free_path(target.previous / name)


def _save(target, journal):
    journal['updatedAt'] = time.strftime('%Y-%m-%dT%H:%M:%S')
    write_json_atomic(target.journal, journal)


def swap(target, staged, new_id, work_dir, backup_dir, progress=print):
    """Install the staged bundle. Returns (backup folder or None on a first export, journal). Raises SwapError when
    refused before any change, SwapFailed after a failure (recovered tells whether the previous state is back)."""
    if target.journal.exists():
        raise SwapError(f'미완료 교체 기록이 있어 새 교체를 시작하지 않습니다: {target.journal}')
    items = []
    for name, live, is_dir in target.items():
        src = Path(staged[name])
        if read_id(src, is_dir) != new_id:
            raise SwapError(f'준비된 {name}의 packageId가 새 패키지와 다릅니다: {src}')
        items.append(dict(name=name, isDir=is_dir, live=str(live), staged=str(src), existedBefore=live.exists(),
                          oldId=read_id(live, is_dir), backup=None, step='pending'))
    if any(it['existedBefore'] for it in items):
        if backup_dir is None:
            raise SwapError('기존 항목이 생겼는데 백업 위치가 정해지지 않았습니다. 다시 실행하세요.')
        backup_dir = Path(backup_dir)
        for it in items:
            if it['existedBefore']:
                it['backup'] = str(backup_dir / Path(it['live']).name)
    else:
        backup_dir = None
    journal = dict(schema=JOURNAL_SCHEMA, label=target.label, state='swapping', newId=new_id, workDir=str(work_dir),
                   backupDir=str(backup_dir) if backup_dir else None, items=items, createdParents=[],
                   startedAt=time.strftime('%Y-%m-%dT%H:%M:%S'))
    # Record first, then create the backup folder: no live item has moved yet, so a failure here changes nothing.
    created = False
    try:
        _save(target, journal)
        if backup_dir:
            backup_dir.parent.mkdir(parents=True, exist_ok=True)
            backup_dir.mkdir(exist_ok=False)      # never reuse an existing backup folder
            created = True
            write_json_atomic(backup_dir / BUNDLE_FILE, dict(state='in_progress', label=target.label, replacedBy=new_id,
                                                             journal=str(target.journal),
                                                             items=[dict(name=it['name'], oldId=it['oldId'], live=it['live'])
                                                                    for it in items if it['existedBefore']]))
    except Exception as exc:
        for undo in ((lambda: (backup_dir / BUNDLE_FILE).unlink(missing_ok=True)) if created else None,
                     (lambda: backup_dir.rmdir()) if created else None, lambda: target.journal.unlink(missing_ok=True)):
            try:
                if undo:
                    undo()
            except OSError:
                pass
        raise SwapError(f'교체 준비(기록·백업 폴더 생성) 실패, 활성 항목은 옮기지 않았습니다: {exc}') from exc
    try:
        for it in items:
            name, live = it['name'], Path(it['live'])
            if it['existedBefore']:
                it['step'] = 'backing_up'
                _save(target, journal)
                fault(f'backup:{name}')
                move(live, it['backup'])
                it['step'] = 'backed_up'
                _save(target, journal)
                fault(f'crash:after-backup:{name}')
            missing = [p for p in reversed(live.parents) if not p.exists()]
            if missing:
                journal['createdParents'] += [str(p) for p in missing]
                _save(target, journal)
                for p in missing:
                    p.mkdir()
            it['step'] = 'installing'
            _save(target, journal)
            fault(f'install:{name}')
            move(it['staged'], live)
            it['step'] = 'installed'
            _save(target, journal)
            fault(f'crash:after-install:{name}')
        fault('verify')
        wrong = {it['name']: read_id(it['live'], it['isDir']) for it in items}
        wrong = {k: v for k, v in wrong.items() if v != new_id}
        if wrong:
            raise SwapError(f'교체 후 packageId 불일치: {wrong} (기대 {new_id[:12]})')
    except BaseException as exc:
        journal['state'] = 'rolling_back'
        journal['error'] = f'{type(exc).__name__}: {exc}'
        try:
            _save(target, journal)
        except Exception:
            pass                                 # the 'swapping' record already allows the next run to roll back
        progress(f'교체 실패({journal["error"]}) → 이전 상태로 복구 시작')
        try:
            rollback(target, journal, progress)
        except RecoveryError as r:
            raise SwapFailed(exc, False, target.journal, backup_dir, work_dir, r.args[1] if len(r.args) > 1 else [str(r)]) from exc
        raise SwapFailed(exc, True, target.journal, backup_dir, work_dir) from exc
    journal['state'] = 'committed'
    journal['committedAt'] = time.strftime('%Y-%m-%dT%H:%M:%S')
    try:
        _save(target, journal)
    except Exception as e:
        # Every item already carries the new packageId; an unsaved 'committed' only means the next run rolls this
        # replacement back to the backup, which is still complete. Report it instead of rolling back now.
        journal['commitRecordError'] = str(e)
    return backup_dir, journal


def finalize_commit(target, journal):
    """After a committed swap: mark the backup superseded, then remove the journal. Failures here are warnings --
    the new bundle is already active, and a remaining committed journal is finalized again by the next run."""
    warnings = []
    if journal.get('backupDir'):
        marker = Path(journal['backupDir']) / BUNDLE_FILE
        try:
            data = json.loads(marker.read_text(encoding='utf-8')) if marker.exists() else {}
            data.update(state='superseded', supersededAt=time.strftime('%Y-%m-%dT%H:%M:%S'), replacedBy=journal['newId'])
            write_json_atomic(marker, data)
        except Exception as e:
            warnings.append(f'백업 표시({marker}) 갱신 실패: {e}')
    if not warnings:
        try:
            target.journal.unlink()
        except Exception as e:
            warnings.append(f'교체 기록({target.journal}) 삭제 실패: {e}')
    return warnings


def _restore_item(it, journal):
    """Put one item back to its pre-swap state, using what is at each location. Moves only."""
    name, is_dir = it['name'], it['isDir']
    live, staged = Path(it['live']), Path(it['staged'])
    backup = Path(it['backup']) if it.get('backup') else None
    new_id = journal['newId']
    fault(f'rollback:{name}')
    if backup is not None and backup.exists():
        # The old item was moved away, so whatever is at the live location is the new one.
        found = read_id(backup, is_dir)
        if found != it['oldId']:
            raise RecoveryError(f'백업 {backup}의 packageId {found}가 기록된 이전 값 {it["oldId"]}과 다릅니다')
        if live.exists():
            found = read_id(live, is_dir)
            if found != new_id:
                raise RecoveryError(f'{live}에 예상하지 못한 항목이 있습니다 (packageId {found}, 기대 {new_id[:12]})')
            _set_aside(live, staged)
        move(backup, live)
    elif it['existedBefore']:
        # The backup move never happened: the live location must still hold the old item.
        if not live.exists():
            raise RecoveryError(f'이전 {name}이 활성 위치({live})와 백업({backup}) 어디에도 없습니다')
        if read_id(live, is_dir) != it['oldId']:
            raise RecoveryError(f'{live}의 packageId가 기록된 이전 값과 달라 이전 항목인지 확정할 수 없습니다')
    elif live.exists():
        # Did not exist before: anything there now is the new item.
        found = read_id(live, is_dir)
        if found != new_id:
            raise RecoveryError(f'{live}에 예상하지 못한 항목이 있습니다 (packageId {found})')
        _set_aside(live, staged)


def _set_aside(live, staged):
    """Move the new item back to its work folder (next to it if that name is taken). Never deletes it."""
    staged.parent.mkdir(parents=True, exist_ok=True)
    move(live, free_path(staged))


def rollback(target, journal, progress=print):
    """Return every item to its recorded pre-swap state. On any problem nothing more is moved back or deleted, the
    journal is kept with state 'rollback_failed' and RecoveryError(message, problems) is raised."""
    problems = []
    for it in reversed(journal['items']):
        try:
            _restore_item(it, journal)
        except Exception as e:
            problems.append(f"{it['name']}: {e}")
    if not problems:
        for it in journal['items']:
            now = read_id(it['live'], it['isDir'])
            expected = it['oldId'] if it['existedBefore'] else None
            if now != expected:
                problems.append(f"{it['name']}: 복구 후 {it['live']}의 packageId {now}, 기대 {expected}")
    if problems:
        journal['state'] = 'rollback_failed'
        journal['problems'] = problems
        try:
            _save(target, journal)
        except Exception as e:
            problems.append(f'교체 기록 저장 실패: {e}')
        raise RecoveryError('복구 실패', problems)
    for p in reversed(journal.get('createdParents', [])):      # folders this swap created, only when empty
        try:
            Path(p).rmdir()
        except OSError:
            pass
    if journal.get('backupDir'):
        b = Path(journal['backupDir'])
        try:
            (b / BUNDLE_FILE).unlink(missing_ok=True)
            b.rmdir()                                            # empty again: every old item went back
        except OSError:
            pass
    journal['state'] = 'rolled_back'
    target.journal.unlink(missing_ok=True)
    progress('이전 상태로 복구 완료: ' + ', '.join(f"{it['name']}={(it['oldId'] or '없음')[:12]}" for it in journal['items']))


def recover_pending(target, progress=print):
    """Before a new export: settle a replacement an earlier run left unfinished. Returns a description or None."""
    if not target.journal.exists():
        return None
    try:
        journal = json.loads(target.journal.read_text(encoding='utf-8'))
        if journal.get('schema') != JOURNAL_SCHEMA or 'items' not in journal:
            raise ValueError('알 수 없는 교체 기록 형식')
    except Exception as e:
        raise UnresolvedSwap(f'교체 기록을 읽을 수 없습니다({e}).', target.journal) from e
    if journal.get('state') == 'committed':
        warnings = finalize_commit(target, journal)
        if warnings:
            raise UnresolvedSwap('완료된 교체의 마무리에 실패했습니다.', target.journal, warnings)
        progress(f"이전 실행의 교체는 완료돼 있었습니다(패키지 {journal['newId'][:12]}). 기록을 정리했습니다.")
        return 'committed'
    progress(f"미완료 교체 발견(상태 {journal.get('state')}, 시작 {journal.get('startedAt')}): 이전 상태로 복구합니다.")
    journal['state'] = 'rolling_back'
    try:
        rollback(target, journal, progress)
    except RecoveryError as r:
        raise UnresolvedSwap('미완료 교체를 일관된 상태로 되돌리지 못해 새 교체를 시작하지 않습니다.', target.journal,
                             r.args[1] if len(r.args) > 1 else [str(r)]) from r
    return 'rolled_back'


def cleanup(target, keep, extra=()):
    """After a committed swap only. Deletes superseded backup bundles beyond the newest `keep`, then `extra` paths
    (the finished work folder). Bundles without a 'superseded' marker (in progress, failed recovery, older tool
    versions) are never touched. Returns warnings; a failure here does not undo the export."""
    warnings = []
    bundles = []
    protected = set()
    if target.journal.exists():          # a replacement record still points at its backup: never delete that one
        try:
            b = json.loads(target.journal.read_text(encoding='utf-8')).get('backupDir')
            if b:
                protected.add(Path(b).resolve())
        except Exception:
            warnings.append(f'교체 기록 {target.journal}을 읽지 못해 백업 정리를 건너뜁니다.')
            return warnings
    if target.previous.exists():
        for p in sorted(target.previous.iterdir(), key=lambda p: p.name):
            try:
                state = json.loads((p / BUNDLE_FILE).read_text(encoding='utf-8')).get('state') if (p / BUNDLE_FILE).exists() else None
            except Exception:
                state = None
            if p.is_dir() and state == 'superseded' and p.resolve() not in protected:
                bundles.append(p)
    for p in bundles[:-keep] if keep > 0 else bundles:
        try:
            fault('cleanup')
            shutil.rmtree(p)
        except Exception as e:
            warnings.append(f'이전 백업 삭제 실패 {p}: {e}')
    for p in extra:
        try:
            fault('cleanup')
            if Path(p).exists():
                shutil.rmtree(p)
        except Exception as e:
            warnings.append(f'작업 폴더 삭제 실패 {p}: {e}')
    return warnings
