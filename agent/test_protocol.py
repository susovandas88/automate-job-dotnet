import json
import os
import subprocess
import threading
import unittest


class AgentProtocolTests(unittest.TestCase):
    def test_process_fills_saved_email_without_clicking_submit(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
        python = os.path.join(root, "agent", ".venv", "Scripts", "python.exe")
        env = os.environ.copy()
        env["PYTHONPATH"] = root
        env["OPENAI_API_KEY"] = "sk-test"
        env["OPENAI_BASE_URL"] = "http://127.0.0.1:9/v1"
        process = subprocess.Popen(
            [python, "-u", "-m", "agent.apply_graph"],
            cwd=root,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        stderr = []
        threading.Thread(target=lambda: stderr.extend(process.stderr.readlines() if process.stderr else []), daemon=True).start()
        timer = threading.Timer(20, process.kill)
        timer.start()
        assert process.stdin is not None and process.stdout is not None
        process.stdin.write(json.dumps({
            "resume": "Ada Lovelace ada@example.com",
            "profile": {"email": "ada@example.com"},
        }) + "\n")
        process.stdin.flush()

        filled = None
        typed = {}
        while True:
            line = process.stdout.readline()
            self.assertTrue(line, "".join(stderr) or "agent closed stdout")
            message = json.loads(line)
            if message["tool"] == "observe":
                process.stdin.write(json.dumps({
                    "fields": [
                        {"key": "0", "label": "Email Address", "kind": "text", "options": [], "currentValue": typed.get("0", "")},
                        {"key": "1", "label": "Retype Email Address", "kind": "text", "options": [], "currentValue": typed.get("1", "")},
                        {"key": "2", "label": "Choose Password", "kind": "password", "options": [], "currentValue": ""},
                    ],
                    "buttons": ["Create Account"],
                    "screenshot": "",
                }) + "\n")
                process.stdin.flush()
            elif message["tool"] == "fill":
                filled = {item["label"]: item["value"] for item in message["actions"]}
                for item in message["actions"]:
                    typed[item["key"]] = item["value"]
                process.stdin.write(json.dumps({"ok": True}) + "\n")
                process.stdin.flush()
            elif message["tool"] == "read_page_text":
                process.stdin.write(json.dumps({"ok": True, "message": "Headings: Create Account\nMessages: "}) + "\n")
                process.stdin.flush()
            elif message["tool"] == "reopen_attempt":
                process.stdin.write(json.dumps({"ok": True}) + "\n")
                process.stdin.flush()
            elif message["tool"] == "open_gate":
                process.stdin.write(json.dumps({"opened": False, "waiting": False}) + "\n")
                process.stdin.flush()
            elif message["tool"] == "tab_open":
                process.stdin.write(json.dumps({"open": False}) + "\n")
                process.stdin.flush()
            elif message["tool"] == "done":
                self.assertEqual("ada@example.com", filled["Email Address"])
                self.assertEqual("ada@example.com", filled["Retype Email Address"])
                self.assertNotIn("Choose Password", filled)
                self.assertIn("Choose Password", message["blank"])
                self.assertNotIn("click", json.dumps(message))
                break
            else:
                self.fail(message["tool"])

        process.wait(timeout=20)
        timer.cancel()


if __name__ == "__main__":
    unittest.main()
