import re

EMAIL_RE = re.compile(r"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", re.IGNORECASE)


def manual_apply(buttons):
    for text in buttons or []:
        if text and re.search(r"apply manually", text, re.IGNORECASE):
            return text.strip()
    return ""


def guard_fields(fields, proposed, resume, profile, answers=None):
    facts = "\n".join(
        part.strip()
        for part in [resume or "", fact_sheet(profile)]
        if part and part.strip()
    ).lower()
    email = first_text(profile.get("email")) or email_in(resume) or email_from(proposed)
    mapped = []
    for field in fields:
        label = field.get("label") or ""
        kind = (field.get("kind") or "").lower()
        if already_filled(field):
            mapped.append({**field, "value": None, "keep": True})
            continue
        if kind in ("radio", "checkbox"):
            if kind == "checkbox" and application_agreement(label):
                mapped.append({**field, "value": "Yes"})
                continue
            question = field.get("group") or label
            answer = saved_value(question, profile or {}, email) or previous_employer_answer(question, resume, profile or {})
            choice = label.strip()
            if answer and choice.lower() == answer.strip().lower():
                mapped.append({**field, "value": choice})
            else:
                mapped.append({**field, "value": None, "keep": True})
            continue
        if is_password(label) or kind == "password":
            mapped.append({**field, "value": None})
            continue

        saved = saved_value(label, profile or {}, email)
        if saved:
            mapped.append({**field, "value": saved})
            continue

        banked = bank_value(label, answers if answers is not None else (profile or {}).get("answers"))
        if banked:
            mapped.append({**field, "value": banked})
            continue

        value = proposed_value(field, proposed)
        if value and value.lower() in facts:
            mapped.append({**field, "value": value})
        else:
            mapped.append({**field, "value": None})
    return mapped


def bank_value(label, bank):
    key = normalize_question(label)
    if not key:
        return None
    for item in bank or []:
        if not isinstance(item, dict):
            continue
        question = normalize_question(item.get("question") or "")
        answer = item.get("answer")
        if question == key and isinstance(answer, str) and answer.strip():
            return answer.strip()
    return None


def normalize_question(text):
    return " ".join(re.sub(r"[^a-z0-9]+", " ", (text or "").lower()).split())


def choose_action(buttons):
    texts = [text.strip() for text in buttons or [] if text and text.strip()]
    if any(re.search(r"sign in with email|continue with email|use email", text, re.IGNORECASE) for text in texts):
        return "advance"
    if any("submit" in text.lower() for text in texts):
        return "stop"
    if any(re.search(r"^(next|continue|review)\b|save and continue", text, re.IGNORECASE) for text in texts):
        return "advance"
    return "stop"


def fact_sheet(profile):
    if not profile:
        return ""
    keys = [
        "firstName", "lastName", "email", "phone", "city", "country", "linkedInUrl", "website",
        "currentTitle", "yearsOfExperience", "workAuthorization", "noticePeriod", "expectedSalary",
        "willingToRelocate", "education", "workHistory", "skills", "additionalDetails",
    ]
    return "\n".join(first_text(profile.get(key)) or "" for key in keys if first_text(profile.get(key)))


def saved_value(label, profile, email):
    text = label.strip()
    if is_password(text):
        return None
    if contains(text, "extension"):
        return None
    if contains(text, "device type"):
        return "Mobile" if first_text(profile.get("phone")) else None
    if contains(text, "how did you hear") or contains(text, "where did you hear"):
        return "LinkedIn"
    if contains(text, "country") and contains(text, "code"):
        country = first_text(profile.get("country"))
        if country and country.lower() == "india":
            return "+91"
        return country
    if is_email(text):
        return email
    if contains(text, "first name") or contains(text, "given name"):
        return first_text(profile.get("firstName"))
    if contains(text, "last name") or contains(text, "surname") or contains(text, "family name"):
        return first_text(profile.get("lastName"))
    if contains(text, "full name") or text.lower() == "name":
        parts = [first_text(profile.get("firstName")), first_text(profile.get("lastName"))]
        joined = " ".join(part for part in parts if part)
        return joined or None
    if contains(text, "phone") or contains(text, "mobile"):
        phone = first_text(profile.get("phone"))
        if phone and contains(text, "number"):
            return national_number(phone)
        return phone
    if contains(text, "city"):
        return first_text(profile.get("city"))
    if contains(text, "country"):
        return first_text(profile.get("country"))
    if contains(text, "linkedin"):
        return first_text(profile.get("linkedInUrl"))
    if contains(text, "website") or contains(text, "portfolio"):
        return first_text(profile.get("website"))
    if contains(text, "current title") or text.lower() == "title" or contains(text, "job title"):
        return first_text(profile.get("currentTitle"))
    if contains(text, "years"):
        return first_text(profile.get("yearsOfExperience"))
    if contains(text, "authori"):
        return first_text(profile.get("workAuthorization"))
    if contains(text, "notice"):
        return first_text(profile.get("noticePeriod"))
    if contains(text, "salary"):
        return first_text(profile.get("expectedSalary"))
    if contains(text, "relocat"):
        return first_text(profile.get("willingToRelocate"))
    return None


def proposed_value(field, proposed):
    key = str(field.get("key") or "")
    label = field.get("label") or ""
    for item in proposed or []:
        if not isinstance(item, dict):
            continue
        same_key = key and str(item.get("key") or "") == key
        same_label = (item.get("label") or "").strip().lower() == label.strip().lower()
        if same_key or same_label:
            value = item.get("value")
            if isinstance(value, str) and value.strip():
                return value.strip()
    return None


def email_from(proposed):
    for item in proposed or []:
        label = item.get("label") or ""
        value = item.get("value") or ""
        if is_email(label) and not is_repeat(label) and isinstance(value, str) and "@" in value:
            return value.strip()
    return None


def email_in(text):
    if not text:
        return None
    match = EMAIL_RE.search(text)
    return match.group(0) if match else None


def previous_employer_answer(question, resume, profile):
    match = re.search(r"worked for (.+?)\?", question or "", re.IGNORECASE)
    if not match:
        return None
    company = match.group(1).strip().lower()
    history = "\n".join(part for part in [resume or "", first_text((profile or {}).get("workHistory")) or ""] if part)
    if company and company in history.lower():
        return "Yes"
    return "No" if company else None


def application_agreement(label):
    text = (label or "").lower()
    if any(part in text for part in ("communication", "future opening", "marketing", "newsletter")):
        return False
    return "i agree" in text and ("term" in text or "privacy" in text)


def already_filled(field):
    current = (field.get("currentValue") or "").strip().lower()
    if not current:
        return False
    if current.startswith("select") or current in {"search", "choose", "choose one"}:
        return False
    return True


def national_number(phone):
    digits = re.sub(r"\D", "", phone)
    if digits.startswith("91") and len(digits) > 10:
        return digits[2:]
    return digits or phone


def first_text(value):
    if not isinstance(value, str):
        return None
    trimmed = value.strip()
    return trimmed or None


def is_email(label):
    return contains(label, "email") or contains(label, "e-mail")


def is_repeat(label):
    return any(contains(label, part) for part in ("retype", "re-type", "confirm", "repeat", "verify"))


def is_password(label):
    return contains(label, "password") or contains(label, "passcode")


def contains(text, part):
    return part.lower() in (text or "").lower()
