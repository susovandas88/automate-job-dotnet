import os

PROJECT = "automate-job"
_llm_tracing = False


def enable_langsmith():
    global _llm_tracing
    key = os.environ.get("LANGSMITH_API_KEY", "").strip()
    if not key.startswith("lsv2_"):
        os.environ["LANGSMITH_TRACING"] = "false"
        os.environ["LANGCHAIN_TRACING_V2"] = "false"
        _set_agent_processor(False)
        return False

    os.environ["LANGSMITH_API_KEY"] = key
    os.environ["LANGSMITH_TRACING"] = "true"
    os.environ["LANGCHAIN_TRACING_V2"] = "true"
    os.environ["LANGSMITH_PROJECT"] = os.environ.get("LANGSMITH_PROJECT") or PROJECT
    os.environ["LANGSMITH_ENDPOINT"] = "https://api.smith.langchain.com"
    _set_agent_processor(True)
    return True


def _set_agent_processor(enabled):
    global _llm_tracing
    try:
        from agents import set_trace_processors
    except Exception:
        _llm_tracing = False
        return
    if not enabled:
        set_trace_processors([])
        _llm_tracing = False
        return
    try:
        from langsmith.integrations.openai_agents_sdk import OpenAIAgentsTracingProcessor

        set_trace_processors([OpenAIAgentsTracingProcessor(project_name=PROJECT)])
        _llm_tracing = True
    except Exception:
        _llm_tracing = False


def flush_langsmith():
    if os.environ.get("LANGSMITH_TRACING") != "true":
        return
    try:
        from langsmith import Client

        Client().flush()
    except Exception:
        return
