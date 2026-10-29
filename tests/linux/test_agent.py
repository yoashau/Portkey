import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("agent", ROOT / "src/Portkey.Infrastructure/Remote/agent.py")
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class Transactions(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="portkey-agent-")
        self.home = Path(self.temp.name)
        self.state = self.home / ".local/state/portkey"
        self.state.mkdir(parents=True)
        self.external = patch.object(agent, "run", return_value=(127, "not available"))
        self.external.start()

    def tearDown(self):
        self.external.stop()
        self.temp.cleanup()

    def change(self, path, content):
        return {"path": path, "beforeHash": agent.read_file(self.home, path)["hash"], "content": content}

    def apply(self, changes, **kwargs):
        return agent.apply(self.home, {"changes": changes, **kwargs}, self.state)

    def test_preserve_permissions_and_bytes(self):
        path = self.home / ".bashrc"
        path.write_bytes(b"# original\r\n")
        path.chmod(0o640)
        self.apply([self.change(".bashrc", "# changed\r\n")])
        self.assertEqual(path.read_bytes(), b"# changed\r\n")
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o640)
        self.assertFalse((self.state / "transaction.json").exists())
        record = json.loads(next((self.state / "backups").glob("*.json")).read_text())
        self.assertEqual(record["entries"][0]["before"]["content"], "# original\r\n")

    def test_stale_preview_rejected_without_backup_or_write(self):
        path = self.home / ".bashrc"
        path.write_text("original")
        change = self.change(".bashrc", "portkey")
        path.write_text("user changed")
        with self.assertRaisesRegex(ValueError, "changed after preview"):
            self.apply([change])
        self.assertEqual(path.read_text(), "user changed")
        self.assertFalse((self.state / "backups").exists())

    def test_latest_backup_is_chronological_within_same_second(self):
        first = self.apply([self.change(".bashrc", "first")])
        second = self.apply([self.change(".bashrc", "second")])
        self.assertGreater(second["backupId"], first["backupId"])
        snapshot = agent.inspect(self.home)
        self.assertEqual(snapshot["backups"][0]["id"], second["backupId"])

    def test_expected_manifest_change_invalidates_unrelated_write(self):
        original = self.change(".bashrc", "new")
        path = self.home / ".profile"
        path.write_text("new login selection")
        with self.assertRaisesRegex(ValueError, "Environment changed"):
            self.apply([original], expected={".profile": None})
        self.assertFalse((self.home / ".bashrc").exists())

    def test_fail_on_second_write_rolls_back_first(self):
        (self.home / ".bashrc").write_text("original")
        changes = [self.change(".bashrc", "portkey"), self.change(".profile", "new")]
        real = agent.write_checked
        def failing(home, relative, expected, content, mode):
            if relative == ".profile":
                raise PermissionError("simulated permission failure")
            return real(home, relative, expected, content, mode)
        with patch.object(agent, "write_checked", side_effect=failing):
            with self.assertRaises(PermissionError):
                self.apply(changes)
        self.assertEqual((self.home / ".bashrc").read_text(), "original")
        self.assertFalse((self.home / ".profile").exists())
        self.assertFalse((self.state / "transaction.json").exists())

    def test_real_write_permission_failure(self):
        if os.getuid() == 0:
            self.skipTest("Root bypasses directory permissions")
        config = self.home / ".config"
        config.mkdir(mode=0o500)
        try:
            with self.assertRaises(PermissionError):
                self.apply([self.change(".config/portkey/proxy.sh", "export x=y\n")])
            self.assertFalse((config / "portkey/proxy.sh").exists())
        finally:
            config.chmod(0o700)

    def interrupted(self, current="portkey"):
        path = self.home / ".bashrc"
        path.write_text(current)
        record = {"version": 1, "entries": [{"path": ".bashrc", "before": {
            "hash": agent.digest(b"original"), "content": "original", "mode": 0o640},
            "afterHash": agent.digest(b"portkey")} ]}
        journal = self.state / "transaction.json"
        agent.atomic_json(journal, record)
        return journal, path

    def test_recover_interrupted_deployment(self):
        journal, path = self.interrupted()
        agent.recover(self.home, journal)
        self.assertEqual(path.read_text(), "original")
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o640)
        self.assertFalse(journal.exists())

    def test_recovery_preserves_concurrent_user_edit(self):
        journal, path = self.interrupted("user edited")
        with self.assertRaisesRegex(ValueError, "user edits preserved"):
            agent.recover(self.home, journal)
        self.assertEqual(path.read_text(), "user edited")
        self.assertTrue(journal.exists())

    def test_inspection_is_read_only_even_with_pending_journal(self):
        self.interrupted()
        before = {str(p): p.read_bytes() for p in self.home.rglob("*") if p.is_file()}
        agent.inspect(self.home)
        after = {str(p): p.read_bytes() for p in self.home.rglob("*") if p.is_file()}
        self.assertEqual(before, after)

    def test_symlink_file_and_parent_refused(self):
        target = self.home / "user.txt"
        target.write_text("preserve")
        (self.home / ".bashrc").symlink_to(target)
        with self.assertRaisesRegex(ValueError, "symlink"):
            agent.read_file(self.home, ".bashrc")
        (self.home / ".config").symlink_to(self.state, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symlink"):
            agent.read_file(self.home, ".config/portkey/proxy.sh")
        self.assertEqual(target.read_text(), "preserve")

    def test_symlink_backup_directory_refused(self):
        (self.state / "backups").symlink_to(self.home, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Unsafe state"):
            agent.check_state(self.home)

    def test_unknown_paths_and_duplicate_writes_refused(self):
        with self.assertRaises(ValueError):
            self.apply([{"path": "../../etc/environment", "beforeHash": None, "content": "x"}])
        with self.assertRaises(ValueError):
            self.apply([self.change(".bashrc", "x"), self.change(".bashrc", "y")])
        with self.assertRaises(ValueError):
            self.apply([{"path": ".config/environment.d/other.conf", "beforeHash": None, "content": "x"}])

    def test_non_utf8_content_refused_without_corruption(self):
        path = self.home / ".bashrc"
        path.write_bytes(b"\xff\xfeoriginal")
        with self.assertRaises(UnicodeDecodeError):
            agent.read_file(self.home, ".bashrc")
        self.assertEqual(path.read_bytes(), b"\xff\xfeoriginal")

    def test_systemd_install_reloads_user_manager_only(self):
        with patch.object(agent, "run", return_value=(0, "")) as run:
            agent.sync_systemd(self.home, [])
            self.assertIn((["systemctl", "--user", "daemon-reload"],), [call.args for call in run.call_args_list])

    def test_systemd_uninstall_keeps_third_party_assignment(self):
        directory = self.home / ".config/environment.d"
        directory.mkdir(parents=True)
        (directory / "99-other.conf").write_text("https_proxy=http://127.0.0.1:17890\n")
        entries = [{"path": ".config/environment.d/90-portkey.conf", "before": {"content": "https_proxy=http://127.0.0.1:17890\nhttp_proxy=http://127.0.0.1:17890\n"}}]
        environment = "https_proxy=http://127.0.0.1:17890\nhttp_proxy=http://127.0.0.1:17890\n"
        with patch.object(agent, "run", return_value=(0, environment)) as run:
            agent.sync_systemd(self.home, entries)
            args = [call.args[0] for call in run.call_args_list]
            self.assertIn(["systemctl", "--user", "unset-environment", "http_proxy"], args)
            self.assertFalse(any("unset-environment" in x and "https_proxy" in x for x in args))

    def test_systemd_offline_is_explicit(self):
        self.assertIn("unavailable", agent.sync_systemd(self.home, []))

    def test_concurrent_second_deploy_is_locked(self):
        import base64
        import fcntl
        import hashlib
        import subprocess
        lock = Path("/tmp") / ("portkey-" + str(os.getuid()) + "-" + hashlib.sha256(str(self.home).encode()).hexdigest()[:16] + ".lock")
        try:
            with lock.open("w+") as stream:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                payload = base64.b64encode(json.dumps({"operation": "apply", "changes": []}).encode()).decode()
                result = subprocess.run(["python3", str(ROOT / "src/Portkey.Infrastructure/Remote/agent.py"), payload], env={**os.environ, "HOME": str(self.home)}, capture_output=True, text=True, timeout=10)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("正在写入", result.stderr)
        finally:
            lock.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
