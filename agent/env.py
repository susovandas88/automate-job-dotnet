import os
from pathlib import Path

from dotenv import load_dotenv

_AGENT_DIR = Path(__file__).resolve().parent


def load_agent_env():
    path = _AGENT_DIR / ".env"
    load_dotenv(path)
    chosen = _flag_in_file(path)
    if chosen is not None:
        os.environ["OPENAI_ENABLED"] = chosen
    from agent.llm_provider import apply_provider_choice

    apply_provider_choice()


def _flag_in_file(path):
    if not path.is_file():
        return None
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key.strip() == "OPENAI_ENABLED":
            return value.strip().strip('"').strip("'")
    return None
