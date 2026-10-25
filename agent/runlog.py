import os
import re
from datetime import datetime


def redact(text):
    text = re.sub(r"sk-[A-Za-z0-9_\-]+", "sk-...", text or "")
    return re.sub(r"lsv2_[A-Za-z0-9_\-]+", "lsv2_...", text)


class RunLog:
    def __init__(self, path):
        self.path = path or ""
        if self.path:
            folder = os.path.dirname(self.path)
            if folder:
                os.makedirs(folder, exist_ok=True)

    def write(self, message):
        if not self.path:
            return
        line = redact(str(message)).replace("\r", " ").replace("\n", " ")
        with open(self.path, "a", encoding="utf-8") as handle:
            handle.write(f"{datetime.now():%H:%M:%S} {line}\n")


def describe_actions(actions):
    parts = []
    for action in actions or []:
        label = action.get("label") or "Field"
        kind = (action.get("kind") or "").lower()
        if "password" in label.lower() or "passcode" in label.lower() or kind == "password":
            parts.append(f"{label}: (password not stored)")
        elif action.get("value"):
            parts.append(f"{label}: {action.get('value')}")
        else:
            parts.append(f"{label}: blank")
    return "; ".join(parts) if parts else "(none)"
