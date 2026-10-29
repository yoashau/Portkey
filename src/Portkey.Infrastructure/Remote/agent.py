#!/usr/bin/env python3
"""Portkey's bounded, user-only deployment helper. Sent via SSH stdin; no installation.

All editable paths are fixed. Snapshots and writes share a flock, and each write
requires an expected digest. A durable journal permits conservative recovery.
"""
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import stat
import subprocess
import sys
import tempfile
import time
import uuid

MANAGED = {".config/portkey/proxy.sh", ".config/portkey/manifest.json",
           ".config/environment.d/90-portkey.conf"}
SHELLS = {".bashrc", ".profile", ".bash_profile", ".bash_login"}
ALLOWED = MANAGED | SHELLS
LIMIT = 512 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest() if data is not None else None


def safe_path(home, relative):
    if relative not in ALLOWED and not re.fullmatch(r"\.config/environment\.d/[^/]+\.conf", relative):
        raise ValueError("Path is not allowed: " + relative)
    path = home / relative
    cursor = home
    for part in Path(relative).parts:
        cursor /= part
        if cursor.is_symlink():
            raise ValueError("Refusing symlink: " + relative)
        if cursor.exists() and cursor.stat().st_uid != os.getuid():
            raise ValueError("File/directory is not owned by target user: " + relative)
    return path


def read_file(home, relative):
    path = safe_path(home, relative)
    try:
        with path.open("rb") as stream:
            data = stream.read(LIMIT + 1)
        if len(data) > LIMIT or not stat.S_ISREG(path.stat().st_mode):
            raise ValueError("File is too large or not regular: " + relative)
        # Refuse unknown encodings instead of corrupting existing shell files.
        text = data.decode("utf-8")
        if "\0" in text:
            raise ValueError("Binary file: " + relative)
        return {"content": text, "hash": digest(data), "mode": stat.S_IMODE(path.stat().st_mode)}
    except FileNotFoundError:
        return {"content": None, "hash": None, "mode": 0o600}


def atomic_json(path, obj):
    atomic_bytes(path, (json.dumps(obj, ensure_ascii=False) + "\n").encode(), 0o600)


def atomic_bytes(path, data, mode):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".portkey-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
            os.fchmod(stream.fileno(), mode & 0o777)
        os.replace(name, path)
        fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def run(args, timeout=10):
    try:
        result = subprocess.run(args, capture_output=True, text=True, timeout=timeout,
                                env={**os.environ, "LC_ALL": "C"})
        return result.returncode, (result.stdout + result.stderr)[:32768]
    except (OSError, subprocess.TimeoutExpired) as error:
        return 127, str(error)


def inspect(home):
    account = pwd.getpwuid(os.getuid())
    files = {name: read_file(home, name) for name in sorted(ALLOWED)}
    environment_dir = safe_path(home, ".config/environment.d/90-portkey.conf").parent
    if environment_dir.exists():
        paths = sorted(environment_dir.glob("*.conf"))
        if len(paths) > 64:
            raise ValueError("Too many environment.d files; manual review required")
        for path in paths:
            files[str(path.relative_to(home))] = read_file(home, str(path.relative_to(home)))
    code, raw_manager = run(["systemctl", "--user", "show-environment"])
    _, listeners = run(["ss", "-lnt"])
    os_info = Path("/etc/os-release").read_text()[:8192]
    # Only these environment variables are returned, never the complete environment.
    manager = "\n".join(line for line in raw_manager.splitlines() if re.match(r"(?i)^(https?|all|no)_proxy=", line)) if code == 0 else ""
    shell_env = "\n".join(key + "=" + value for key, value in os.environ.items() if re.fullmatch(r"(?i)(https?|all|no)_proxy", key))
    warnings = []
    if os.environ.get("XDG_CONFIG_HOME") not in (None, "", str(home / ".config")):
        raise ValueError("Custom XDG_CONFIG_HOME requires manual configuration")
    if "XDG_CONFIG_HOME=" in raw_manager:
        # A custom manager environment must not silently redirect environment.d.
        for line in raw_manager.splitlines():
            if line.startswith("XDG_CONFIG_HOME=") and line.split("=", 1)[1] != str(home / ".config"):
                raise ValueError("Custom systemd XDG_CONFIG_HOME requires manual configuration")
    if code == 0:
        _, units = run(["systemctl", "--user", "list-unit-files", "--type=service", "--no-pager", "--no-legend"])
        for line in units.splitlines():
            if re.search(r"(?i)(proxy|mihomo|clash|tunnel|ssh)", line):
                warnings.append("Possible third-party user service (preserved): " + line)
    backups = []
    check_state(home)
    backup_dir = home / ".local/state/portkey/backups"
    if backup_dir.exists():
        for path in sorted(backup_dir.glob("*.json"), reverse=True)[:10]:
            if path.is_symlink() or path.stat().st_size > 8 * LIMIT:
                raise ValueError("Unsafe backup record")
            record = json.loads(path.read_text())
            if record.get("status") == "committed":
                backups.append({"id": path.stem, "restore": [
                    {"path": entry["path"], "beforeHash": entry["afterHash"], "content": entry["before"]["content"], "mode": entry["before"]["mode"]}
                    for entry in record["entries"]]})
    return {"uid": os.getuid(), "user": account.pw_name, "home": str(home),
            "shell": account.pw_shell, "os": os_info, "systemd": code == 0,
            "systemdEnvironment": manager, "shellEnvironment": shell_env,
            "listeners": listeners, "files": files, "warnings": warnings, "backups": backups}


def write_checked(home, relative, expected, content, mode):
    path = safe_path(home, relative)
    if read_file(home, relative)["hash"] != expected:
        raise ValueError("File changed after preview: " + relative)
    if content is None:
        if path.exists():
            path.unlink()
            fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
    else:
        atomic_bytes(path, content.encode("utf-8"), mode)


def recover(home, journal):
    if journal.stat().st_size > 8 * LIMIT:
        raise ValueError("Transaction journal is too large")
    record = json.loads(journal.read_text())
    if record.get("version") != 1 or any(entry["path"] not in ALLOWED for entry in record["entries"]):
        raise ValueError("Invalid transaction journal")
    conflicts = []
    for entry in reversed(record["entries"]):
        current = read_file(home, entry["path"])
        if current["hash"] == entry["before"]["hash"]:
            continue
        if current["hash"] != entry["afterHash"]:
            conflicts.append(entry["path"])
            continue
        write_checked(home, entry["path"], entry["afterHash"], entry["before"]["content"], entry["before"]["mode"])
    if conflicts:
        raise ValueError("Recovery needs manual review; user edits preserved: " + ", ".join(conflicts))
    journal.unlink()
    backup = journal.parent / "backups" / (record.get("backupId", "invalid") + ".json")
    if backup.exists():
        record["status"] = "rolled-back"
        atomic_json(backup, record)
    return "Recovered interrupted Portkey transaction"


def sync_systemd(home, entries):
    code, before = run(["systemctl", "--user", "show-environment"])
    if code:
        return "systemd unavailable; shell configuration only"
    code, output = run(["systemctl", "--user", "daemon-reload"])
    if code:
        return "systemd reload failed: " + output
    proxy_entry = next((entry for entry in entries if entry["path"] == ".config/environment.d/90-portkey.conf"), None)
    if not proxy_entry or read_file(home, proxy_entry["path"])["content"] is not None:
        return "systemd reloaded"
    # On uninstall, remove only manager values that still match the owned file,
    # and leave any variable also assigned by a remaining user environment file.
    variables = dict(line.split("=", 1) for line in (proxy_entry["before"]["content"] or "").splitlines()
                     if re.match(r"(?i)^(https?|all|no)_proxy=", line))
    current = dict(line.split("=", 1) for line in before.splitlines() if "=" in line)
    other_keys = set()
    directory = home / ".config/environment.d"
    for path in directory.glob("*.conf"):
        for line in read_file(home, str(path.relative_to(home)))["content"].splitlines():
            if "=" in line:
                other_keys.add(line.split("=", 1)[0])
    # System-level environment sources may contain a fallback belonging to the
    # administrator. A reload can expose those: never unset a changed value.
    _, after = run(["systemctl", "--user", "show-environment"])
    reloaded = dict(line.split("=", 1) for line in after.splitlines() if "=" in line)
    keys = [key for key, value in variables.items() if current.get(key) == value
            and reloaded.get(key) == value and key not in other_keys]
    if keys:
        code, output = run(["systemctl", "--user", "unset-environment", *keys])
        if code:
            return "systemd cleanup failed: " + output
    return "systemd reloaded; removed only unchanged Portkey proxy variables"


def apply(home, request, state_dir):
    changes = request["changes"]
    if len(changes) > len(ALLOWED) or len({x["path"] for x in changes}) != len(changes):
        raise ValueError("Invalid changes")
    entries = []
    for change in changes:
        if change["path"] not in ALLOWED:
            raise ValueError("Cannot modify unowned path")
        current = read_file(home, change["path"])
        if current["hash"] != change["beforeHash"]:
            raise ValueError("File changed after preview: " + change["path"])
        content = change["content"]
        if content is not None and (not isinstance(content, str) or len(content.encode()) > LIMIT):
            raise ValueError("Invalid content")
        entries.append({"path": change["path"], "before": current,
                        "afterHash": digest(content.encode() if content is not None else None)})
    if not entries:
        return {"message": "No file changes", "backupId": None}
    # Preserve the full preview snapshot as well as changed files: concurrent edits
    # to the ownership manifest or shell selection also invalidate the plan.
    for path, expected in request.get("expected", {}).items():
        if read_file(home, path)["hash"] != expected:
            raise ValueError("Environment changed after preview: " + path)
    # Subsecond ordering matters: an install followed immediately by a port
    # update must restore the update, rather than a random UUID from that second.
    backup_id = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + f"{time.time_ns():020d}" + "-" + uuid.uuid4().hex[:8]
    record = {"version": 1, "status": "pending", "entries": entries, "backupId": backup_id}
    backup_dir = state_dir / "backups"
    backup_dir.mkdir(mode=0o700, exist_ok=True)
    backup_file = backup_dir / (backup_id + ".json")
    journal = state_dir / "transaction.json"
    atomic_json(backup_file, record)
    atomic_json(journal, record)
    try:
        for change, entry in zip(changes, entries):
            mode = change.get("mode")
            if mode is not None and (not isinstance(mode, int) or mode < 0 or mode > 0o777):
                raise ValueError("Invalid file permissions")
            write_checked(home, change["path"], change["beforeHash"], change["content"], entry["before"]["mode"] if mode is None else mode)
        for entry in entries:
            if read_file(home, entry["path"])["hash"] != entry["afterHash"]:
                raise ValueError("Write verification failed")
        record["status"] = "committed"
        atomic_json(backup_file, record)
        journal.unlink()
    except BaseException:
        recover(home, journal)
        raise
    # Keep at most 20 completed backups. Never discard an unresolved transaction.
    for old in sorted(backup_dir.glob("*.json"), reverse=True)[20:]:
        if not old.is_symlink():
            old.unlink()
    try:
        manager = sync_systemd(home, entries)
    except Exception as error:
        manager = "systemd synchronization needs review: " + str(error)
    return {"message": "Files committed and verified", "backupId": backup_id, "systemd": manager}


def check_state(home):
    cursor = home
    for part in Path(".local/state/portkey/backups").parts:
        cursor /= part
        if cursor.is_symlink() or (cursor.exists() and cursor.stat().st_uid != os.getuid()):
            raise ValueError("Unsafe state directory")
    journal = home / ".local/state/portkey/transaction.json"
    if journal.is_symlink() or (journal.exists() and journal.stat().st_uid != os.getuid()):
        raise ValueError("Unsafe transaction journal")


def main():
    if os.getuid() == 0:
        raise ValueError("Refusing root; select a regular SSH user")
    request = json.loads(base64.b64decode(sys.argv[1], validate=True))
    home = Path.home()
    if not home.is_absolute() or home.is_symlink() or home.stat().st_uid != os.getuid():
        raise ValueError("Unsafe HOME")
    if request["operation"] == "inspect":
        # Read-only inspection must never create directories or recover files.
        check_state(home)
        state = home / ".local/state/portkey"
        if (state / "transaction.json").exists():
            raise ValueError("Interrupted deployment found. Use Recover to restore its backed-up files before previewing again.")
        print(json.dumps(inspect(home), ensure_ascii=False))
        return
    # The lock is a per-user file in /tmp, not a shell config file. It only
    # serializes Portkey writers; other editors are caught by digest checks.
    lock_path = Path("/tmp") / ("portkey-" + str(os.getuid()) + "-" + hashlib.sha256(str(home).encode()).hexdigest()[:16] + ".lock")
    fd = os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "r+") as lock:
        if os.fstat(lock.fileno()).st_uid != os.getuid():
            raise ValueError("Unsafe lock owner")
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("另一个 Portkey 部署正在写入，请稍后重试") from error
        state = home / ".local/state/portkey"
        check_state(home)
        state.mkdir(mode=0o700, parents=True, exist_ok=True)
        if (state / "transaction.json").is_symlink():
            raise ValueError("Unsafe transaction journal")
        if request["operation"] == "recover":
            journal = state / "transaction.json"
            message = recover(home, journal) if journal.exists() else "No interrupted transaction"
            print(json.dumps({"message": message}))
        elif request["operation"] == "apply":
            if (state / "transaction.json").exists():
                raise ValueError("Recover the interrupted transaction first")
            print(json.dumps(apply(home, request, state)))
        else:
            raise ValueError("Unknown operation")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Portkey: " + str(error), file=sys.stderr)
        sys.exit(1)
