import os


def openai_enabled():
    raw = (os.environ.get("OPENAI_ENABLED") or "true").strip().lower()
    return raw not in ("false", "0", "off", "no")


def gemini_key():
    return (os.environ.get("GEMINI_API_KEY") or "").strip()


def gemini_model():
    return (os.environ.get("GEMINI_MODEL") or "gemini-2.5-flash").strip()


def gemini_base():
    base = (os.environ.get("GEMINI_BASE_URL") or "https://generativelanguage.googleapis.com/v1beta/openai").strip()
    return base if base.endswith("/") else base + "/"


def provider_name():
    return "OpenAI" if openai_enabled() else "Gemini"


def apply_provider_choice():
    """Keep one provider. OPENAI_ENABLED true drops Gemini. Any other value drops OpenAI."""
    if openai_enabled():
        os.environ.pop("GEMINI_API_KEY", None)
        return
    os.environ["OPENAI_ENABLED"] = "false"
    os.environ.pop("OPENAI_API_KEY", None)
    os.environ.pop("OPENAI_BASE_URL", None)
