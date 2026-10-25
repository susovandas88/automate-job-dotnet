import unittest

from agent.guard import guard_fields
from agent.openai_playwright import (
    PlaywrightBridge,
    ats_hint,
    blocked_button,
    canvas_route,
    known_value,
    parse_visual_action,
    upload_file,
    values_match,
)


class ToolTests(unittest.TestCase):
    def test_submit_and_identity_buttons_are_refused(self):
        self.assertTrue(blocked_button("Submit application"))
        self.assertTrue(blocked_button("Create account"))
        self.assertTrue(blocked_button("Sign in with Apple"))
        self.assertTrue(blocked_button("Sign in with Google"))
        self.assertFalse(blocked_button("Sign in with email"))
        self.assertFalse(blocked_button("Next"))

    def test_text_and_select_keep_only_known_facts(self):
        bridge = PlaywrightBridge("Alex Chen\nalex@example.com", {"country": "India"}, lambda _line: None)
        email = {"key": "0", "label": "Email Address", "kind": "email"}
        country = {"key": "1", "label": "Country", "kind": "select", "options": ["India", "United States"]}
        password = {"key": "2", "label": "Choose Password", "kind": "password"}

        self.assertEqual("alex@example.com", known_value(bridge, email, "other@example.com"))
        self.assertEqual("India", known_value(bridge, country, "United States"))
        self.assertIsNone(known_value(bridge, password, "secret"))
        self.assertIsNone(known_value(bridge, {"key": "3", "label": "Phone", "kind": "tel"}, "555-0100"))

    def test_upload_has_no_path_argument(self):
        properties = upload_file.params_json_schema["properties"]
        self.assertEqual(["field_id", "which"], list(properties))

    def test_masked_phone_still_counts_as_filled(self):
        self.assertTrue(values_match("9876543210", "(987) 654-3210"))
        self.assertFalse(values_match("ada@example.com", ""))

    def test_answers_bank_fills_the_same_question_later(self):
        mapped = guard_fields(
            [{"key": "0", "label": "Years of Python?", "kind": "text"}],
            [],
            "Ada Lovelace",
            {},
            [{"question": "Years of Python?", "answer": "5"}],
        )
        self.assertEqual("5", mapped[0]["value"])

    def test_unmapped_question_is_recorded_once(self):
        bridge = PlaywrightBridge("", {}, lambda _line: None)
        bridge.report("4", "Visa status", "No saved answer.")
        bridge.report("9", "Visa status?", "Asked again.")
        self.assertEqual(1, len(bridge.unmapped))
        self.assertEqual("Visa status", bridge.unmapped[0]["label"])

    def test_captcha_workday_and_canvas_need_a_person(self):
        self.assertIn("Workday", ats_hint("https://company.wd5.myworkdayjobs.com/en-US/login", [], "account"))
        self.assertEqual("", ats_hint("https://company.wd5.myworkdayjobs.com/en-US/apply", ["Next"], ""))
        self.assertTrue(canvas_route("canvas", []))
        self.assertFalse(canvas_route("canvas", [{"key": "f0e0"}]))
        self.assertFalse(canvas_route("", []))
        self.assertIsNone(parse_visual_action("not json"))
        self.assertEqual("stop", parse_visual_action('{"action":"stop"}')["action"])


if __name__ == "__main__":
    unittest.main()
