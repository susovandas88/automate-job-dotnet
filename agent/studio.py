from agent.graph import build_apply_graph


class StudioSession:
    def observe(self):
        return {"fields": [], "buttons": [], "wall": "", "screenshot": ""}

    def fill(self, actions):
        return None

    def fill_experience(self):
        return None

    def click_named(self, name):
        return None

    def click_apply(self):
        return False

    def click_advance(self):
        return None

    def reopen_attempt(self, attempt):
        return False

    def open_gate(self):
        return {"opened": False, "waiting": False}

    def tab_open(self):
        return False


def decide(state):
    return []


graph = build_apply_graph(StudioSession(), decide)
